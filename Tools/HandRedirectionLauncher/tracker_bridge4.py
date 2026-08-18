import sys
import argparse
import json
import os
import time
import socket
import struct
import openvr
import select
import math

# =========================
# Settings
# =========================
QUEST_IP = "127.0.0.1"
QUEST_PORT = 9000

ACK_PORT = 9001
HZ = 60

# Tracker attached to the real desk.
REFERENCE_SERIAL = ""

# Optional tracker attached to the user's head. Leave this empty for Spatial Anchor mode.
# HEAD_SERIAL = "LHR-A44246FF"
HEAD_SERIAL = ""

# Tracked object serial -> objectId
OBJECT_SERIALS = {}

# Keep this if the Unity side already expects the STS-to-Unity z flip.
APPLY_STS_TO_UNITY = True


def parse_args():
    parser = argparse.ArgumentParser(description="SteamVR tracker to Unity/Quest UDP bridge")
    parser.add_argument(
        "--config",
        default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "tracker_bridge4.config.json"),
        help="JSON configuration file",
    )
    parser.add_argument("--quest-ip", help="Override questIp from the configuration file")
    parser.add_argument("--list", "-l", action="store_true", help="List SteamVR devices and exit")
    parser.add_argument(
        "--wait-for-openvr",
        type=float,
        default=0.0,
        metavar="SECONDS",
        help="Wait for SteamVR/OpenVR instead of failing immediately",
    )
    return parser.parse_args()


def load_config(path, quest_ip_override=None):
    global QUEST_IP, QUEST_PORT, ACK_PORT, HZ
    global REFERENCE_SERIAL, HEAD_SERIAL, OBJECT_SERIALS, APPLY_STS_TO_UNITY

    if path and os.path.isfile(path):
        with open(path, "r", encoding="utf-8-sig") as stream:
            config = json.load(stream)
        QUEST_IP = str(config.get("questIp", QUEST_IP))
        QUEST_PORT = int(config.get("questPort", QUEST_PORT))
        ACK_PORT = int(config.get("ackPort", ACK_PORT))
        HZ = float(config.get("hz", HZ))
        REFERENCE_SERIAL = str(config.get("referenceSerial", REFERENCE_SERIAL) or "")
        HEAD_SERIAL = str(config.get("headSerial", HEAD_SERIAL) or "")
        APPLY_STS_TO_UNITY = bool(config.get("applyStsToUnity", APPLY_STS_TO_UNITY))
        configured_objects = config.get("objectSerials")
        if isinstance(configured_objects, dict):
            OBJECT_SERIALS = {str(serial): int(object_id) for serial, object_id in configured_objects.items()}
    elif path:
        print(f"Config not found; using built-in defaults: {path}", flush=True)

    if quest_ip_override:
        QUEST_IP = quest_ip_override

    print(
        f"Configuration: target={QUEST_IP}:{QUEST_PORT}, ack=:{ACK_PORT}, hz={HZ:g}, "
        f"reference={REFERENCE_SERIAL or '(none)'}, head={HEAD_SERIAL or '(none)'}, objects={len(OBJECT_SERIALS)}",
        flush=True,
    )


def initialize_openvr_with_retry(wait_seconds):
    deadline = time.time() + max(0.0, wait_seconds)
    while True:
        try:
            openvr.init(openvr.VRApplication_Background)
            return openvr.VRSystem()
        except Exception as exc:
            if time.time() >= deadline:
                raise
            remaining = max(0.0, deadline - time.time())
            print(f"Waiting for SteamVR/OpenVR ({remaining:.0f}s): {exc}", flush=True)
            time.sleep(1.0)


# =========================
# OpenVR helpers
# =========================
def configured_tracker_serials():
    serials = set(OBJECT_SERIALS.keys())
    if REFERENCE_SERIAL:
        serials.add(REFERENCE_SERIAL)
    if HEAD_SERIAL:
        serials.add(HEAD_SERIAL)
    return serials


def get_serial(vr, dev_index: int) -> str:
    try:
        return vr.getStringTrackedDeviceProperty(
            dev_index,
            openvr.Prop_SerialNumber_String,
        )
    except openvr.error_code.OpenVRError:
        return ""


def mat34_to_mat44(m):
    return [
        [m[0][0], m[0][1], m[0][2], m[0][3]],
        [m[1][0], m[1][1], m[1][2], m[1][3]],
        [m[2][0], m[2][1], m[2][2], m[2][3]],
        [0.0, 0.0, 0.0, 1.0],
    ]


def mat44_mul(a, b):
    out = [[0.0] * 4 for _ in range(4)]
    for r in range(4):
        for c in range(4):
            out[r][c] = (
                a[r][0] * b[0][c]
                + a[r][1] * b[1][c]
                + a[r][2] * b[2][c]
                + a[r][3] * b[3][c]
            )
    return out


def mat44_inv_rigid(t):
    r00, r01, r02, px = t[0]
    r10, r11, r12, py = t[1]
    r20, r21, r22, pz = t[2]

    rt = [
        [r00, r10, r20, 0.0],
        [r01, r11, r21, 0.0],
        [r02, r12, r22, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ]

    tx = -(rt[0][0] * px + rt[0][1] * py + rt[0][2] * pz)
    ty = -(rt[1][0] * px + rt[1][1] * py + rt[1][2] * pz)
    tz = -(rt[2][0] * px + rt[2][1] * py + rt[2][2] * pz)

    rt[0][3] = tx
    rt[1][3] = ty
    rt[2][3] = tz
    return rt


def sts_conjugate_z(m):
    s = [
        [1.0, 0.0, 0.0, 0.0],
        [0.0, 1.0, 0.0, 0.0],
        [0.0, 0.0, -1.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ]
    return mat44_mul(mat44_mul(s, m), s)


def mat44_to_pos_quat(m):
    px, py, pz = m[0][3], m[1][3], m[2][3]

    r00, r01, r02 = m[0][0], m[0][1], m[0][2]
    r10, r11, r12 = m[1][0], m[1][1], m[1][2]
    r20, r21, r22 = m[2][0], m[2][1], m[2][2]

    tr = r00 + r11 + r22
    if tr > 0.0:
        s = math.sqrt(tr + 1.0) * 2.0
        qw = 0.25 * s
        qx = (r21 - r12) / s
        qy = (r02 - r20) / s
        qz = (r10 - r01) / s
    elif (r00 > r11) and (r00 > r22):
        s = math.sqrt(1.0 + r00 - r11 - r22) * 2.0
        qw = (r21 - r12) / s
        qx = 0.25 * s
        qy = (r01 + r10) / s
        qz = (r02 + r20) / s
    elif r11 > r22:
        s = math.sqrt(1.0 + r11 - r00 - r22) * 2.0
        qw = (r02 - r20) / s
        qx = (r01 + r10) / s
        qy = 0.25 * s
        qz = (r12 + r21) / s
    else:
        s = math.sqrt(1.0 + r22 - r00 - r11) * 2.0
        qw = (r10 - r01) / s
        qx = (r02 + r20) / s
        qy = (r12 + r21) / s
        qz = 0.25 * s

    n = math.sqrt(qx * qx + qy * qy + qz * qz + qw * qw)
    if n > 1e-8:
        qx, qy, qz, qw = qx / n, qy / n, qz / n, qw / n

    return (px, py, pz, qx, qy, qz, qw)


def collect_tracker_state(vr, poses):
    pose_by_serial = {}
    status_by_serial = {}

    for i, p in enumerate(poses):
        if not p.bDeviceIsConnected:
            continue

        dev_class = vr.getTrackedDeviceClass(i)
        serial = get_serial(vr, i)
        status_by_serial[serial] = {
            "dev_index": i,
            "dev_class": dev_class,
            "connected": bool(p.bDeviceIsConnected),
            "valid": bool(p.bPoseIsValid),
        }

        if dev_class != openvr.TrackedDeviceClass_GenericTracker:
            continue
        if not p.bPoseIsValid:
            continue

        pose_by_serial[serial] = mat34_to_mat44(p.mDeviceToAbsoluteTracking)

    return pose_by_serial, status_by_serial


def tracker_state_tuple(serial, status_by_serial, pose_by_serial):
    st = status_by_serial.get(serial)
    detected = serial in pose_by_serial
    if st is None:
        return (False, False, False, None)
    return (st["connected"], st["valid"], detected, st["dev_index"])


def tracker_label(serial):
    if serial == REFERENCE_SERIAL:
        return "DESK"
    if HEAD_SERIAL:
        if serial == HEAD_SERIAL:
            return "HEAD"
    if serial in OBJECT_SERIALS:
        return f"OBJ:{OBJECT_SERIALS[serial]}"
    return "TRACKER"


def describe_tracker_state(state):
    connected, valid, detected, dev_index = state
    dev = "-" if dev_index is None else str(dev_index)
    return (
        f"devIndex={dev} connected={1 if connected else 0} "
        f"valid={1 if valid else 0} detected={1 if detected else 0}"
    )


def log_tracking_changes(now_ms, previous_states, status_by_serial, pose_by_serial):
    for serial in sorted(configured_tracker_serials()):
        state = tracker_state_tuple(serial, status_by_serial, pose_by_serial)
        prev_record = previous_states.get(serial)
        prev = prev_record["state"] if prev_record else None

        if prev is None or prev == state:
            bad_since_ms = None
            if prev_record:
                bad_since_ms = prev_record["bad_since_ms"]
            if bad_since_ms is None and not state[2]:
                bad_since_ms = now_ms
            previous_states[serial] = {
                "state": state,
                "bad_since_ms": None if state[2] else bad_since_ms,
            }
            continue

        was_connected = prev[0]
        is_connected = state[0]
        was_detected = prev[2]
        is_detected = state[2]
        bad_since_ms = prev_record["bad_since_ms"]

        if was_connected and not is_connected:
            event = "DISCONNECTED"
            bad_since_ms = now_ms
        elif was_detected and not is_detected:
            event = "LOST"
            bad_since_ms = now_ms
        elif not was_detected and is_detected:
            event = "RESTORED"
        else:
            previous_states[serial] = {
                "state": state,
                "bad_since_ms": bad_since_ms if not is_detected else None,
            }
            continue

        duration = ""
        if event == "RESTORED" and bad_since_ms is not None:
            duration = f" duration_ms={now_ms - bad_since_ms}"

        print(
            f"[TRACKING {event}] tms={now_ms} {tracker_label(serial)} "
            f"serial={serial}{duration} {describe_tracker_state(state)} "
            f"prev=({describe_tracker_state(prev)})",
            flush=True,
        )

        previous_states[serial] = {
            "state": state,
            "bad_since_ms": None if is_detected else bad_since_ms,
        }


def print_tracker_list(vr):
    configured_serials = configured_tracker_serials()
    unconfigured = []
    poses = vr.getDeviceToAbsoluteTrackingPose(
        openvr.TrackingUniverseStanding,
        0,
        openvr.k_unMaxTrackedDeviceCount,
    )

    print("[Trackers]")
    for i, p in enumerate(poses):
        if not p.bDeviceIsConnected:
            continue

        dev_class = vr.getTrackedDeviceClass(i)
        serial = get_serial(vr, i)

        if dev_class == openvr.TrackedDeviceClass_GenericTracker:
            cls_name = "GenericTracker"
        elif dev_class == openvr.TrackedDeviceClass_HMD:
            cls_name = "HMD"
        elif dev_class == openvr.TrackedDeviceClass_Controller:
            cls_name = "Controller"
        elif dev_class == openvr.TrackedDeviceClass_TrackingReference:
            cls_name = "BaseStation"
        else:
            cls_name = f"Class{dev_class}"

        tag = ""
        if serial == REFERENCE_SERIAL:
            tag = " [DESK]"
        elif HEAD_SERIAL and serial == HEAD_SERIAL:
            tag = " [HEAD]"
        elif serial in OBJECT_SERIALS:
            tag = f" [OBJ:{OBJECT_SERIALS[serial]}]"
        elif dev_class == openvr.TrackedDeviceClass_GenericTracker and serial and serial not in configured_serials:
            tag = " [UNCONFIGURED]"
            unconfigured.append(serial)

        print(f"  devIndex={i:2d} class={cls_name:14s} serial={serial} valid={p.bPoseIsValid}{tag}")

    if unconfigured:
        print("[Unconfigured trackers]")
        for serial in sorted(unconfigured):
            print(f"  serial={serial}")
    else:
        print("[Unconfigured trackers] none")
    print("----", flush=True)


def pack_desk_packet(now_ms, px, py, pz, qx, qy, qz, qw):
    return struct.pack(
        "<4sq3f4f",
        b"DSK0",
        now_ms,
        px,
        py,
        pz,
        qx,
        qy,
        qz,
        qw,
    )


def pack_object_packet(now_ms, obj_id, px, py, pz, qx, qy, qz, qw):
    return struct.pack(
        "<4sqI3f4f",
        b"REL0",
        now_ms,
        obj_id,
        px,
        py,
        pz,
        qx,
        qy,
        qz,
        qw,
    )


def main():
    args = parse_args()
    load_config(args.config, args.quest_ip)
    list_only = args.list

    send_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    ack_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    ack_sock.bind(("0.0.0.0", ACK_PORT))
    ack_sock.setblocking(False)

    vr = initialize_openvr_with_retry(args.wait_for_openvr)
    print("OpenVR initialized (Background).", flush=True)

    try:
        print_tracker_list(vr)

        if list_only:
            print("List mode finished.", flush=True)
            return

        period = 1.0 / HZ
        next_t = time.time()
        previous_tracker_states = {}

        while True:
            r, _, _ = select.select([ack_sock], [], [], 0.0)
            if r:
                while True:
                    try:
                        ack_sock.recvfrom(2048)
                    except BlockingIOError:
                        break

            poses = vr.getDeviceToAbsoluteTrackingPose(
                openvr.TrackingUniverseStanding,
                0,
                openvr.k_unMaxTrackedDeviceCount,
            )

            pose_by_serial, status_by_serial = collect_tracker_state(vr, poses)

            now_ms = int(time.time() * 1000)
            head_t = pose_by_serial.get(HEAD_SERIAL) if HEAD_SERIAL else None
            desk_t = pose_by_serial.get(REFERENCE_SERIAL)
            log_tracking_changes(now_ms, previous_tracker_states, status_by_serial, pose_by_serial)

            if desk_t is None:
                next_t += period
                sleep = next_t - time.time()
                if sleep > 0:
                    time.sleep(sleep)
                else:
                    next_t = time.time()
                continue

            if head_t is not None:
                head_inv = mat44_inv_rigid(head_t)
                desk_in_head = mat44_mul(head_inv, desk_t)  # head <- desk
                if APPLY_STS_TO_UNITY:
                    desk_in_head = sts_conjugate_z(desk_in_head)

                px, py, pz, qx, qy, qz, qw = mat44_to_pos_quat(desk_in_head)
                desk_pkt = pack_desk_packet(now_ms, px, py, pz, qx, qy, qz, qw)
                send_sock.sendto(desk_pkt, (QUEST_IP, QUEST_PORT))

            desk_inv = mat44_inv_rigid(desk_t)
            for serial, obj_id in OBJECT_SERIALS.items():
                obj_t = pose_by_serial.get(serial)
                if obj_t is None:
                    continue

                rel_t = mat44_mul(desk_inv, obj_t)  # desk <- object
                if APPLY_STS_TO_UNITY:
                    rel_t = sts_conjugate_z(rel_t)

                px, py, pz, qx, qy, qz, qw = mat44_to_pos_quat(rel_t)
                pkt = pack_object_packet(now_ms, obj_id, px, py, pz, qx, qy, qz, qw)
                send_sock.sendto(pkt, (QUEST_IP, QUEST_PORT))

            next_t += period
            sleep = next_t - time.time()
            if sleep > 0:
                time.sleep(sleep)
            else:
                next_t = time.time()

    except KeyboardInterrupt:
        pass
    finally:
        try:
            openvr.shutdown()
        except Exception:
            pass
        try:
            send_sock.close()
            ack_sock.close()
        except Exception:
            pass
        print("Shutdown.", flush=True)


if __name__ == "__main__":
    main()

