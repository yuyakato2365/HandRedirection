import datetime as _dt
import os
import queue
import socket
import struct
import subprocess
import threading
import time
import tkinter as tk
from tkinter import filedialog, messagebox


DEFAULT_OUTPUT_DIR = os.path.join(os.path.expanduser("~"), "Videos", "HandRedirectionRecordings")
DEFAULT_OCULUS_MIRROR = r"C:\Program Files\Oculus\Support\oculus-diagnostics\OculusMirror.exe"
TOUCHDESIGNER_FFMPEG = r"C:\Program Files\Derivative\TouchDesigner\bin\ffmpeg.exe"


class MjpegAviWriter:
    def __init__(self, path, fps):
        self.path = path
        self.fps = max(1, int(fps))
        self.file = open(path, "wb")
        self.width = 0
        self.height = 0
        self.frames = 0
        self.max_frame = 0
        self.index = []
        self.movi_data_start = 4096
        self.file.write(b"\0" * 4096)

    def add_frame(self, jpeg):
        if self.frames == 0:
            self.width, self.height = parse_jpeg_size(jpeg)
        pos = self.file.tell()
        self.file.write(b"00dc")
        self.file.write(struct.pack("<I", len(jpeg)))
        self.file.write(jpeg)
        if len(jpeg) & 1:
            self.file.write(b"\0")
        self.index.append((pos - self.movi_data_start, len(jpeg)))
        self.frames += 1
        self.max_frame = max(self.max_frame, len(jpeg))

    def close(self):
        idx_start = self.file.tell()
        self.file.write(b"idx1")
        self.file.write(struct.pack("<I", len(self.index) * 16))
        for offset, size in self.index:
            self.file.write(b"00dc")
            self.file.write(struct.pack("<III", 0x10, offset, size))

        file_size = self.file.tell()
        self.file.seek(0)
        header = self._build_header(file_size, idx_start)
        self.file.write(header)
        if len(header) < 4096:
            self.file.write(b"\0" * (4096 - len(header)))
        self.file.close()

    def _chunk(self, fourcc, payload):
        pad = b"\0" if len(payload) & 1 else b""
        return fourcc + struct.pack("<I", len(payload)) + payload + pad

    def _list(self, list_type, payload):
        return b"LIST" + struct.pack("<I", len(payload) + 4) + list_type + payload

    def _build_header(self, file_size, idx_start):
        width = self.width or 2
        height = self.height or 2
        total_frames = self.frames
        usec_per_frame = int(1_000_000 / self.fps)

        avih = struct.pack(
            "<IIIIIIIIII4I",
            usec_per_frame,
            0,
            0,
            0x10,
            total_frames,
            0,
            1,
            self.max_frame,
            width,
            height,
            0,
            0,
            0,
            0,
        )

        strh = (
            b"vids"
            + b"MJPG"
            + struct.pack(
                "<IHHIIIIIIIIhhhh",
                0,
                0,
                0,
                0,
                1,
                self.fps,
                0,
                total_frames,
                self.max_frame,
                0xFFFFFFFF,
                0,
                0,
                0,
                width,
                height,
            )
        )

        strf = struct.pack(
            "<IiiHH4sIiiII",
            40,
            width,
            height,
            1,
            24,
            b"MJPG",
            width * height * 3,
            0,
            0,
            0,
            0,
        )

        strl = self._list(b"strl", self._chunk(b"strh", strh) + self._chunk(b"strf", strf))
        hdrl = self._list(b"hdrl", self._chunk(b"avih", avih) + strl)
        chunks_size = idx_start - 4096
        movi = b"LIST" + struct.pack("<I", chunks_size + 4) + b"movi"
        riff_size = file_size - 8
        return b"RIFF" + struct.pack("<I", riff_size) + b"AVI " + hdrl + movi


def parse_jpeg_size(data):
    i = 2
    while i < len(data) - 9:
        if data[i] != 0xFF:
            i += 1
            continue
        marker = data[i + 1]
        i += 2
        if marker in (0xD8, 0xD9):
            continue
        if i + 2 > len(data):
            break
        length = struct.unpack(">H", data[i : i + 2])[0]
        if marker in (0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF):
            height = struct.unpack(">H", data[i + 3 : i + 5])[0]
            width = struct.unpack(">H", data[i + 5 : i + 7])[0]
            return width, height
        i += length
    return 2, 2


def recv_exact(sock, size):
    chunks = []
    remaining = size
    while remaining > 0:
        chunk = sock.recv(remaining)
        if not chunk:
            raise ConnectionError("socket closed")
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def first_existing(paths):
    for path in paths:
        if path and os.path.exists(path):
            return path
    return ""


class RecordingController:
    def __init__(self, app):
        self.app = app
        self.stop_event = threading.Event()
        self.receiver_thread = None
        self.status_thread = None
        self.ffmpeg_proc = None
        self.mirror_proc = None
        self.local_mr_path = ""
        self.local_passthrough_path = ""

    def start(self):
        if self.receiver_thread and self.receiver_thread.is_alive():
            raise RuntimeError("recording is already running")

        output_dir = self.app.output_dir.get().strip() or DEFAULT_OUTPUT_DIR
        os.makedirs(output_dir, exist_ok=True)

        timestamp = _dt.datetime.now().strftime("%Y%m%d_%H%M%S")
        self.local_mr_path = os.path.join(output_dir, f"mr_pcvr_{timestamp}.mp4")
        self.local_passthrough_path = os.path.join(output_dir, f"passthrough_{timestamp}.avi")

        self.stop_event.clear()
        self.receiver_thread = threading.Thread(target=self._passthrough_receiver, daemon=True)
        self.receiver_thread.start()
        self.status_thread = threading.Thread(target=self._status_listener, daemon=True)
        self.status_thread.start()

        time.sleep(0.2)
        self._send_unity_start_command()
        self._start_mr_capture()
        self.app.log(f"Started PCVR session {timestamp}")

    def stop(self):
        self.stop_event.set()
        self._send_udp("127.0.0.1", int(self.app.command_port.get()), "STOP_PASSTHROUGH_STREAM")
        self._stop_mr_capture()

        if self.receiver_thread:
            self.receiver_thread.join(timeout=5)

        self.app.log("Stopped. Files are in: " + (self.app.output_dir.get().strip() or DEFAULT_OUTPUT_DIR))

    def _passthrough_receiver(self):
        fps = int(self.app.fps.get())
        tcp_port = int(self.app.tcp_port.get())
        writer = None
        server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        server.bind(("127.0.0.1", tcp_port))
        server.listen(1)
        server.settimeout(0.5)
        self.app.log(f"Waiting Unity passthrough stream on 127.0.0.1:{tcp_port}")
        wait_started = time.time()
        try:
            client = None
            while not self.stop_event.is_set() and client is None:
                try:
                    client, addr = server.accept()
                    self.app.log(f"Passthrough connected from {addr[0]}:{addr[1]}")
                except socket.timeout:
                    if time.time() - wait_started > 6:
                        self.app.log("Still waiting for passthrough. In PCVR/Unity Editor Play, Meta PassthroughCameraAccess may not expose raw camera frames.")
                        wait_started = time.time()
                    pass

            if client is None:
                return

            with client:
                writer = MjpegAviWriter(self.local_passthrough_path, fps)
                while not self.stop_event.is_set():
                    header = recv_exact(client, 4)
                    length = struct.unpack("<I", header)[0]
                    if length <= 0 or length > 20_000_000:
                        raise RuntimeError(f"bad JPEG frame length: {length}")
                    writer.add_frame(recv_exact(client, length))
        except Exception as exc:
            if not self.stop_event.is_set():
                self.app.log(f"Passthrough receiver stopped: {exc}")
        finally:
            try:
                server.close()
            except OSError:
                pass
            if writer:
                writer.close()
                self.app.log(f"Saved passthrough: {self.local_passthrough_path}")

    def _status_listener(self):
        port = int(self.app.status_port.get())
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.bind(("127.0.0.1", port))
        sock.settimeout(0.5)
        try:
            while not self.stop_event.is_set():
                try:
                    data, addr = sock.recvfrom(4096)
                    self.app.log(f"Unity status {addr[0]}: {data.decode(errors='replace')}")
                except socket.timeout:
                    pass
        finally:
            sock.close()

    def _send_unity_start_command(self):
        command = (
            "START_PASSTHROUGH_STREAM "
            "host=127.0.0.1 "
            f"port={int(self.app.tcp_port.get())} "
            f"fps={int(self.app.fps.get())} "
            f"quality={int(self.app.jpeg_quality.get())} "
            f"maxLongSide={int(self.app.max_long_side.get())}"
        )
        self._send_udp("127.0.0.1", int(self.app.command_port.get()), command)
        self.app.log("Sent Unity passthrough start command: " + command)

    def _send_udp(self, host, port, message):
        data = message.encode("utf-8")
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
            sock.sendto(data, (host, port))

    def _start_mr_capture(self):
        ffmpeg = self.app.ffmpeg_path.get().strip()
        if not ffmpeg:
            raise RuntimeError("ffmpeg path is required for PCVR MR recording")
        if not os.path.exists(ffmpeg):
            raise RuntimeError(f"ffmpeg not found: {ffmpeg}")

        source_mode = self.app.mr_source_mode.get()
        if source_mode == "Oculus Mirror":
            self._start_oculus_mirror_if_needed()
            time.sleep(float(self.app.mirror_warmup_sec.get()))
            source = "title=Oculus Mirror"
        elif source_mode == "Window title":
            title = self.app.window_title.get().strip()
            if not title:
                raise RuntimeError("window title is required")
            source = "title=" + title
        else:
            source = "desktop"

        fps = str(int(self.app.mr_fps.get()))
        bitrate = f"{int(self.app.mr_bitrate_mbps.get())}M"
        cmd = [
            ffmpeg,
            "-y",
            "-f",
            "gdigrab",
            "-framerate",
            fps,
            "-i",
            source,
            "-c:v",
            "libx264",
            "-preset",
            "veryfast",
            "-pix_fmt",
            "yuv420p",
            "-b:v",
            bitrate,
            self.local_mr_path,
        ]
        self.ffmpeg_proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.app.log("PCVR MR capture started: " + source)

    def _start_oculus_mirror_if_needed(self):
        mirror_path = self.app.oculus_mirror_path.get().strip()
        if not mirror_path:
            raise RuntimeError("OculusMirror.exe path is required for Oculus Mirror mode")
        if not os.path.exists(mirror_path):
            raise RuntimeError(f"OculusMirror.exe not found: {mirror_path}")

        args = [mirror_path]
        extra = self.app.oculus_mirror_args.get().strip()
        if extra:
            args += extra.split()
        self.mirror_proc = subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.app.log("Oculus Mirror launched")

    def _stop_mr_capture(self):
        if self.ffmpeg_proc:
            try:
                self.ffmpeg_proc.communicate(input=b"q", timeout=5)
            except subprocess.TimeoutExpired:
                self.ffmpeg_proc.terminate()
                try:
                    self.ffmpeg_proc.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    self.ffmpeg_proc.kill()
            self.ffmpeg_proc = None
            self.app.log(f"Saved MR capture: {self.local_mr_path}")

        if self.mirror_proc and self.app.close_oculus_mirror.get():
            try:
                self.mirror_proc.terminate()
            except OSError:
                pass
            self.mirror_proc = None


class App:
    def __init__(self, root):
        self.root = root
        self.root.title("HandRedirection PCVR Recorder")
        self.log_queue = queue.Queue()
        self.controller = RecordingController(self)

        ffmpeg_default = first_existing([TOUCHDESIGNER_FFMPEG])
        mirror_default = first_existing([DEFAULT_OCULUS_MIRROR])

        self.output_dir = tk.StringVar(value=DEFAULT_OUTPUT_DIR)
        self.command_port = tk.StringVar(value="9201")
        self.status_port = tk.StringVar(value="9202")
        self.tcp_port = tk.StringVar(value="9210")
        self.fps = tk.StringVar(value="15")
        self.jpeg_quality = tk.StringVar(value="75")
        self.max_long_side = tk.StringVar(value="1280")
        self.ffmpeg_path = tk.StringVar(value=ffmpeg_default)
        self.mr_source_mode = tk.StringVar(value="Oculus Mirror")
        self.oculus_mirror_path = tk.StringVar(value=mirror_default)
        self.oculus_mirror_args = tk.StringVar(value="")
        self.mirror_warmup_sec = tk.StringVar(value="1.0")
        self.window_title = tk.StringVar(value="Unity")
        self.mr_fps = tk.StringVar(value="30")
        self.mr_bitrate_mbps = tk.StringVar(value="20")
        self.close_oculus_mirror = tk.BooleanVar(value=True)

        self._build_ui()
        self.root.after(100, self._drain_log)

    def _build_ui(self):
        fields = [
            ("Output folder", self.output_dir, "dir"),
            ("ffmpeg.exe", self.ffmpeg_path, "file"),
            ("MR source", self.mr_source_mode, "choice"),
            ("OculusMirror.exe", self.oculus_mirror_path, "file"),
            ("Oculus Mirror args", self.oculus_mirror_args, None),
            ("Mirror warmup sec", self.mirror_warmup_sec, None),
            ("Window title", self.window_title, None),
            ("MR FPS", self.mr_fps, None),
            ("MR bitrate Mbps", self.mr_bitrate_mbps, None),
            ("Unity command UDP", self.command_port, None),
            ("Unity status UDP", self.status_port, None),
            ("Passthrough TCP", self.tcp_port, None),
            ("Passthrough FPS", self.fps, None),
            ("JPEG quality", self.jpeg_quality, None),
            ("Max long side px", self.max_long_side, None),
        ]

        for row, (label, var, kind) in enumerate(fields):
            tk.Label(self.root, text=label, anchor="w").grid(row=row, column=0, sticky="ew", padx=8, pady=3)
            if kind == "choice":
                menu = tk.OptionMenu(self.root, var, "Oculus Mirror", "Window title", "Desktop")
                menu.grid(row=row, column=1, sticky="ew", padx=8, pady=3)
            else:
                tk.Entry(self.root, textvariable=var, width=58).grid(row=row, column=1, sticky="ew", padx=8, pady=3)

            if kind == "dir":
                tk.Button(self.root, text="Browse", command=lambda v=var: self._browse_dir(v)).grid(row=row, column=2, padx=8, pady=3)
            elif kind == "file":
                tk.Button(self.root, text="Browse", command=lambda v=var: self._browse_file(v)).grid(row=row, column=2, padx=8, pady=3)

        option_row = len(fields)
        tk.Checkbutton(self.root, text="Close Oculus Mirror on Stop", variable=self.close_oculus_mirror).grid(
            row=option_row, column=1, sticky="w", padx=8, pady=3
        )
        tk.Button(self.root, text="Start", command=self._start).grid(row=option_row + 1, column=0, sticky="ew", padx=8, pady=8)
        tk.Button(self.root, text="Stop", command=self._stop).grid(row=option_row + 1, column=1, sticky="ew", padx=8, pady=8)

        self.log_text = tk.Text(self.root, width=96, height=14)
        self.log_text.grid(row=option_row + 2, column=0, columnspan=3, sticky="nsew", padx=8, pady=8)
        self.root.columnconfigure(1, weight=1)
        self.root.rowconfigure(option_row + 2, weight=1)

    def _browse_dir(self, var):
        folder = filedialog.askdirectory(initialdir=var.get() or DEFAULT_OUTPUT_DIR)
        if folder:
            var.set(folder)

    def _browse_file(self, var):
        path = filedialog.askopenfilename(initialdir=os.path.dirname(var.get()) if var.get() else os.getcwd())
        if path:
            var.set(path)

    def _start(self):
        try:
            self.controller.start()
        except Exception as exc:
            messagebox.showerror("Start failed", str(exc))

    def _stop(self):
        threading.Thread(target=self._stop_worker, daemon=True).start()

    def _stop_worker(self):
        try:
            self.controller.stop()
        except Exception as exc:
            self.log("Stop failed: " + str(exc))

    def log(self, message):
        self.log_queue.put(f"{time.strftime('%H:%M:%S')}  {message}")

    def _drain_log(self):
        while True:
            try:
                message = self.log_queue.get_nowait()
            except queue.Empty:
                break
            self.log_text.insert("end", message + "\n")
            self.log_text.see("end")
        self.root.after(100, self._drain_log)


if __name__ == "__main__":
    root = tk.Tk()
    App(root)
    root.mainloop()
