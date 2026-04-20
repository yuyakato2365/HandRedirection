/*
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// UDPで受信した「head座標系での tracker 相対姿勢 (head <- tracker)」を
/// Unity内の HMD Transform (hmdTransform) に合成して、
/// ターゲットオブジェクト (objTransform) をワールド空間で追従させる。
///
/// Packet format (44 bytes):
///   'R','E','L','0'  (4)
///   int64 nowMs      (8)
///   uint32 objId     (4)
///   float pos[3]     (12)  // tracker position in head space
///   float quat[4]    (16)  // tracker rotation in head space (x,y,z,w)
///
/// 追加機能:
/// - headToHmdPos / headToHmdEuler : head基準とUnity HMD基準のズレを補正（簡易）
/// - centerOffsetInTracker / centerEulerOffset : トラッカー原点→オブジェクト中心の固定オフセット補正
/// - スムージング（lerp/slerp）
/// </summary>
public class TrackerToCubeOffsetCalibrator3 : MonoBehaviour
{
    // =========================
    // Inspector
    // =========================

    [Header("UDP")]
    public int listenPort = 9000;
    public bool forceIPv4 = true;

    [Header("Target")]
    [Tooltip("この objId のパケットだけ反映する")]
    public uint targetObjectId = 1;

    [Tooltip("追従させたいUnity上のオブジェクト（iPad等）")]
    public Transform objTransform;

    [Tooltip("Unity側のHMD（CenterEyeAnchor等）")]
    public Transform hmdTransform;

    [Header("Head -> HMD Offset (approx)")]
    [Tooltip("head座標系で表現された head原点→HMD原点 の平行移動（m）")]
    public Vector3 headToHmdPos = Vector3.zero;

    [Tooltip("head座標系で表現された head→HMD の回転（deg）")]
    public Vector3 headToHmdEuler = Vector3.zero;

    [Header("Tracker -> Object Center Offset (in tracker local space)")]
    [Tooltip("トラッカー原点からオブジェクト中心までのオフセット（トラッカー座標系, m）")]
    public Vector3 centerOffsetInTracker = Vector3.zero;

    [Tooltip("トラッカー姿勢からオブジェクト中心姿勢へ回転を補正したい場合（通常は不要）。deg")]
    public Vector3 centerEulerOffset = Vector3.zero;

    [Header("Smoothing")]
    [Tooltip("0なら補間なし。大きいほど速く追従（位置）")]
    public float positionLerp = 0f;

    [Tooltip("0なら補間なし。大きいほど速く追従（回転）")]
    public float rotationSlerp = 0f;

    [Header("Debug")]
    public bool logBadPackets = false;

    // =========================
    // Internal state
    // =========================

    private UdpClient udp;
    private Thread recvThread;
    private volatile bool running;

    private readonly object latestLock = new object();
    private bool hasLatest = false;
    private RelPose latest;

    private struct RelPose
    {
        public long nowMs;
        public uint objId;
        public Vector3 pos;     // tracker position in head space
        public Quaternion rot;  // tracker rotation in head space
    }

    // =========================
    // Unity lifecycle
    // =========================

    private void Start()
    {
        if (objTransform == null)
        {
            Debug.LogWarning("[TrackerToCubeOffsetCalibrator3] objTransform is null.");
        }
        if (hmdTransform == null)
        {
            Debug.LogWarning("[TrackerToCubeOffsetCalibrator3] hmdTransform is null.");
        }

        try
        {
            if (forceIPv4)
            {
                // IPv4優先（環境によっては不要）
                udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));
            }
            else
            {
                udp = new UdpClient(listenPort);
            }

            running = true;
            recvThread = new Thread(RecvLoop)
            {
                IsBackground = true,
                Name = "UdpRelPoseReceiver"
            };
            recvThread.Start();

            Debug.Log($"[TrackerToCubeOffsetCalibrator3] UDP listening on :{listenPort}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[TrackerToCubeOffsetCalibrator3] UDP init failed: {e}");
            running = false;
        }
    }
/*
    private void OnDestroy()
    {
        running = false;

        try { udp?.Close(); } catch { /* ignore */ /* ←これだけ消す}
        udp = null;

        try
        {
            if (recvThread != null && recvThread.IsAlive)
            {
                recvThread.Join(200);
            }
        }
        catch { /* ignore */ /*　←ここも}

        recvThread = null;
    }
*//*
    private void Update()
    {
        if (!hasLatest) return;
        if (objTransform == null || hmdTransform == null) return;

        RelPose rel;
        lock (latestLock)
        {
            if (!hasLatest) return;
            rel = latest;
        }

        if (rel.objId != targetObjectId) return;

        // ---------
        // 1) head<-tracker を head<-hmd で補正して hmd<-tracker へ（簡易）
        // ---------
        Quaternion headToHmdRot = Quaternion.Euler(headToHmdEuler);
        Quaternion invHeadToHmdRot = Quaternion.Inverse(headToHmdRot);

        // tracker pose in HMD space
        Vector3 trackerInHmdPos = invHeadToHmdRot * (rel.pos - headToHmdPos);
        Quaternion trackerInHmdRot = invHeadToHmdRot * rel.rot;

        // ---------
        // 2) tracker原点 → オブジェクト中心 へオフセット補正（ここが追加）
        // ---------
        Quaternion centerRotOffset = Quaternion.Euler(centerEulerOffset);

        Vector3 centerInHmdPos = trackerInHmdPos + (trackerInHmdRot * centerOffsetInTracker);
        Quaternion centerInHmdRot = trackerInHmdRot * centerRotOffset;

        // ---------
        // 3) Unity HMD world pose と合成して object world pose へ
        // ---------
        Vector3 hmdPosW = hmdTransform.position;
        Quaternion hmdRotW = hmdTransform.rotation;

        Vector3 targetPosW = hmdPosW + (hmdRotW * centerInHmdPos);
        Quaternion targetRotW = hmdRotW * centerInHmdRot;

        // ---------
        // 4) Optional smoothing
        // ---------
        if (positionLerp > 0f)
        {
            float k = 1f - Mathf.Exp(-positionLerp * Time.deltaTime * 60f);
            objTransform.position = Vector3.Lerp(objTransform.position, targetPosW, k);
        }
        else
        {
            objTransform.position = targetPosW;
        }

        if (rotationSlerp > 0f)
        {
            float k = 1f - Mathf.Exp(-rotationSlerp * Time.deltaTime * 60f);
            objTransform.rotation = Quaternion.Slerp(objTransform.rotation, targetRotW, k);
        }
        else
        {
            objTransform.rotation = targetRotW;
        }
    }

    // =========================
    // UDP receive loop
    // =========================

    private void RecvLoop()
    {
        // 固定長 (44 bytes)
        const int PACKET_SIZE = 44;

        while (running)
        {
            try
            {
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = udp.Receive(ref ep);

                if (data == null || data.Length < PACKET_SIZE)
                {
                    if (logBadPackets) Debug.LogWarning("[UDP] packet too small");
                    continue;
                }

                // Header 'REL0'
                if (data[0] != (byte)'R' || data[1] != (byte)'E' || data[2] != (byte)'L' || data[3] != (byte)'0')
                {
                    if (logBadPackets) Debug.LogWarning("[UDP] bad header");
                    continue;
                }

                int o = 4;

                long nowMs = ReadInt64LE(data, ref o);
                uint objId = ReadUInt32LE(data, ref o);

                // float pos[3]
                float px = ReadFloatLE(data, ref o);
                float py = ReadFloatLE(data, ref o);
                float pz = ReadFloatLE(data, ref o);

                // float quat[4] (x,y,z,w)
                float qx = ReadFloatLE(data, ref o);
                float qy = ReadFloatLE(data, ref o);
                float qz = ReadFloatLE(data, ref o);
                float qw = ReadFloatLE(data, ref o);

                if (objId != targetObjectId)
                {
                    // 他のIDは無視（必要なら複数管理に拡張）
                    continue;
                }

                RelPose rel = new RelPose
                {
                    nowMs = nowMs,
                    objId = objId,
                    pos = new Vector3(px, py, pz),
                    rot = new Quaternion(qx, qy, qz, qw)
                };

                lock (latestLock)
                {
                    latest = rel;
                    hasLatest = true;
                }
            }
            catch (SocketException)
            {
                // Close()でReceiveが例外になることがある
                if (!running) break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception e)
            {
                if (logBadPackets) Debug.LogWarning($"[UDP] exception: {e.Message}");
            }
        }
    }

    // =========================
    // Little-endian readers
    // =========================

    private static long ReadInt64LE(byte[] b, ref int o)
    {
        long v =
            ((long)b[o + 0]) |
            ((long)b[o + 1] << 8) |
            ((long)b[o + 2] << 16) |
            ((long)b[o + 3] << 24) |
            ((long)b[o + 4] << 32) |
            ((long)b[o + 5] << 40) |
            ((long)b[o + 6] << 48) |
            ((long)b[o + 7] << 56);
        o += 8;
        return v;
    }

    private static uint ReadUInt32LE(byte[] b, ref int o)
    {
        uint v =
            ((uint)b[o + 0]) |
            ((uint)b[o + 1] << 8) |
            ((uint)b[o + 2] << 16) |
            ((uint)b[o + 3] << 24);
        o += 4;
        return v;
    }

    private static float ReadFloatLE(byte[] b, ref int o)
    {
        // BitConverterはエンディアン依存なので、明示的にLE化
        uint u = ReadUInt32LE(b, ref o);
        byte[] tmp = new byte[4];
        tmp[0] = (byte)(u & 0xFF);
        tmp[1] = (byte)((u >> 8) & 0xFF);
        tmp[2] = (byte)((u >> 16) & 0xFF);
        tmp[3] = (byte)((u >> 24) & 0xFF);
        return BitConverter.ToSingle(tmp, 0);
    }
}
*/

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// UDPで受信した「head座標系での tracker 相対姿勢 (head <- tracker)」を
/// Unity内の HMD Transform (hmdTransform) に合成して、
/// 複数ターゲットオブジェクトをワールド空間で追従させる。
///
/// Packet format (44 bytes):
///   'R','E','L','0'  (4)
///   int64 nowMs      (8)
///   uint32 objId     (4)
///   float pos[3]     (12)  // tracker position in head space
///   float quat[4]    (16)  // tracker rotation in head space (x,y,z,w)
///
/// 機能:
/// - headToHmdPos / headToHmdEuler : head基準とUnity HMD基準のズレを補正
/// - 各 target ごとに tracker原点→オブジェクト中心の固定オフセット補正
/// - 複数objIdを1コンポーネントで管理
/// - スムージング（lerp/slerp）
/// </summary>
public class TrackerToCubeOffsetCalibrator3 : MonoBehaviour
{
    [Serializable]
    public class TargetEntry
    {
        [Header("Identity")]
        [Tooltip("Python側 OBJECT_SERIALS で割り当てた objectId")]
        public uint objectId = 1;

        [Tooltip("追従させたいUnity上のオブジェクト")]
        public Transform objTransform;

        [Header("Tracker -> Object Center Offset (in tracker local space)")]
        [Tooltip("トラッカー原点からオブジェクト中心までのオフセット（トラッカー座標系, m）")]
        public Vector3 centerOffsetInTracker = Vector3.zero;

        [Tooltip("トラッカー姿勢からオブジェクト中心姿勢へ回転を補正したい場合（通常は不要）。deg")]
        public Vector3 centerEulerOffset = Vector3.zero;
    }

    // =========================
    // Inspector
    // =========================

    [Header("UDP")]
    public int listenPort = 9000;
    public bool forceIPv4 = true;

    [Header("Scene References")]
    [Tooltip("Unity側のHMD（CenterEyeAnchor等）")]
    public Transform hmdTransform;

    [Header("Head -> HMD Offset (approx)")]
    [Tooltip("head座標系で表現された head原点→HMD原点 の平行移動（m）")]
    public Vector3 headToHmdPos = Vector3.zero;

    [Tooltip("head座標系で表現された head→HMD の回転（deg）")]
    public Vector3 headToHmdEuler = Vector3.zero;

    [Header("Targets")]
    [Tooltip("受信した objId ごとの反映先一覧")]
    public List<TargetEntry> targets = new List<TargetEntry>();

    [Header("Smoothing")]
    [Tooltip("0なら補間なし。大きいほど速く追従（位置）")]
    public float positionLerp = 0f;

    [Tooltip("0なら補間なし。大きいほど速く追従（回転）")]
    public float rotationSlerp = 0f;

    [Header("Debug")]
    public bool logBadPackets = false;
    public bool logReceivedIds = false;

    // =========================
    // Internal state
    // =========================

    private UdpClient udp;
    private Thread recvThread;
    private volatile bool running;

    private readonly object latestLock = new object();
    private readonly Dictionary<uint, RelPose> latestById = new Dictionary<uint, RelPose>();

    private struct RelPose
    {
        public long nowMs;
        public uint objId;
        public Vector3 pos;     // tracker position in head space
        public Quaternion rot;  // tracker rotation in head space
    }

    // =========================
    // Unity lifecycle
    // =========================

    private void Start()
    {
        if (hmdTransform == null)
        {
            Debug.LogWarning("[TrackerToCubeOffsetCalibrator3] hmdTransform is null.");
        }

        if (targets == null || targets.Count == 0)
        {
            Debug.LogWarning("[TrackerToCubeOffsetCalibrator3] targets is empty.");
        }
        else
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] == null) continue;
                if (targets[i].objTransform == null)
                {
                    Debug.LogWarning($"[TrackerToCubeOffsetCalibrator3] targets[{i}].objTransform is null.");
                }
            }
        }

        try
        {
            if (forceIPv4)
            {
                udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));
            }
            else
            {
                udp = new UdpClient(listenPort);
            }

            running = true;
            recvThread = new Thread(RecvLoop)
            {
                IsBackground = true,
                Name = "UdpRelPoseReceiver"
            };
            recvThread.Start();

            Debug.Log($"[TrackerToCubeOffsetCalibrator3] UDP listening on :{listenPort}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[TrackerToCubeOffsetCalibrator3] UDP init failed: {e}");
            running = false;
        }
    }

    private void OnDestroy()
    {
        running = false;

        try { udp?.Close(); } catch { }
        udp = null;

        try
        {
            if (recvThread != null && recvThread.IsAlive)
            {
                recvThread.Join(200);
            }
        }
        catch { }

        recvThread = null;
    }

    private void Update()
    {
        if (hmdTransform == null) return;
        if (targets == null || targets.Count == 0) return;

        Quaternion headToHmdRot = Quaternion.Euler(headToHmdEuler);
        Quaternion invHeadToHmdRot = Quaternion.Inverse(headToHmdRot);

        for (int i = 0; i < targets.Count; i++)
        {
            TargetEntry target = targets[i];
            if (target == null || target.objTransform == null) continue;

            RelPose rel;
            bool found;

            lock (latestLock)
            {
                found = latestById.TryGetValue(target.objectId, out rel);
            }

            if (!found) continue;

            // 1) head<-tracker を head<-hmd で補正して hmd<-tracker へ
            Vector3 trackerInHmdPos = invHeadToHmdRot * (rel.pos - headToHmdPos);
            Quaternion trackerInHmdRot = invHeadToHmdRot * rel.rot;

            // 2) tracker原点 -> オブジェクト中心
            Quaternion centerRotOffset = Quaternion.Euler(target.centerEulerOffset);
            Vector3 centerInHmdPos = trackerInHmdPos + (trackerInHmdRot * target.centerOffsetInTracker);
            Quaternion centerInHmdRot = trackerInHmdRot * centerRotOffset;

            // 3) HMD world pose と合成
            Vector3 hmdPosW = hmdTransform.position;
            Quaternion hmdRotW = hmdTransform.rotation;

            Vector3 targetPosW = hmdPosW + (hmdRotW * centerInHmdPos);
            Quaternion targetRotW = hmdRotW * centerInHmdRot;

            // 4) smoothing
            if (positionLerp > 0f)
            {
                float k = 1f - Mathf.Exp(-positionLerp * Time.deltaTime * 60f);
                target.objTransform.position = Vector3.Lerp(target.objTransform.position, targetPosW, k);
            }
            else
            {
                target.objTransform.position = targetPosW;
            }

            if (rotationSlerp > 0f)
            {
                float k = 1f - Mathf.Exp(-rotationSlerp * Time.deltaTime * 60f);
                target.objTransform.rotation = Quaternion.Slerp(target.objTransform.rotation, targetRotW, k);
            }
            else
            {
                target.objTransform.rotation = targetRotW;
            }
        }
    }

    // =========================
    // UDP receive loop
    // =========================

    private void RecvLoop()
    {
        const int PACKET_SIZE = 44;

        while (running)
        {
            try
            {
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = udp.Receive(ref ep);

                if (data == null || data.Length < PACKET_SIZE)
                {
                    if (logBadPackets) Debug.LogWarning("[UDP] packet too small");
                    continue;
                }

                if (data[0] != (byte)'R' || data[1] != (byte)'E' || data[2] != (byte)'L' || data[3] != (byte)'0')
                {
                    if (logBadPackets) Debug.LogWarning("[UDP] bad header");
                    continue;
                }

                int o = 4;

                long nowMs = ReadInt64LE(data, ref o);
                uint objId = ReadUInt32LE(data, ref o);

                float px = ReadFloatLE(data, ref o);
                float py = ReadFloatLE(data, ref o);
                float pz = ReadFloatLE(data, ref o);

                float qx = ReadFloatLE(data, ref o);
                float qy = ReadFloatLE(data, ref o);
                float qz = ReadFloatLE(data, ref o);
                float qw = ReadFloatLE(data, ref o);

                RelPose rel = new RelPose
                {
                    nowMs = nowMs,
                    objId = objId,
                    pos = new Vector3(px, py, pz),
                    rot = new Quaternion(qx, qy, qz, qw)
                };

                lock (latestLock)
                {
                    latestById[objId] = rel;
                }

                if (logReceivedIds)
                {
                    Debug.Log($"[UDP] objId={objId} pos={rel.pos}");
                }
            }
            catch (SocketException)
            {
                if (!running) break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception e)
            {
                if (logBadPackets) Debug.LogWarning($"[UDP] exception: {e.Message}");
            }
        }
    }

    // =========================
    // Little-endian readers
    // =========================

    private static long ReadInt64LE(byte[] b, ref int o)
    {
        long v =
            ((long)b[o + 0]) |
            ((long)b[o + 1] << 8) |
            ((long)b[o + 2] << 16) |
            ((long)b[o + 3] << 24) |
            ((long)b[o + 4] << 32) |
            ((long)b[o + 5] << 40) |
            ((long)b[o + 6] << 48) |
            ((long)b[o + 7] << 56);
        o += 8;
        return v;
    }

    private static uint ReadUInt32LE(byte[] b, ref int o)
    {
        uint v =
            ((uint)b[o + 0]) |
            ((uint)b[o + 1] << 8) |
            ((uint)b[o + 2] << 16) |
            ((uint)b[o + 3] << 24);
        o += 4;
        return v;
    }

    private static float ReadFloatLE(byte[] b, ref int o)
    {
        uint u = ReadUInt32LE(b, ref o);
        byte[] tmp = BitConverter.GetBytes(u);
        return BitConverter.ToSingle(tmp, 0);
    }
}