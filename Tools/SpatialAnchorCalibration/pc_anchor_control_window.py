import socket
import threading
import tkinter as tk
from datetime import datetime
from tkinter import ttk


DEFAULT_QUEST_IP = "127.0.0.1"
QUEST_COMMAND_PORT = 9101
PC_STATUS_PORT = 9102


class AnchorControlWindow:
    def __init__(self, root):
        self.root = root
        self.root.title("Spatial Anchor Calibration Control")
        self.root.geometry("520x420")
        self.running = True

        self.quest_ip = tk.StringVar(value=DEFAULT_QUEST_IP)
        self.status_text = tk.StringVar(value="Waiting for Quest status")

        self._build_ui()
        self._start_status_listener()

    def _build_ui(self):
        frame = ttk.Frame(self.root, padding=16)
        frame.pack(fill=tk.BOTH, expand=True)

        ttk.Label(frame, text="Target IP").grid(row=0, column=0, sticky="w")
        ttk.Entry(frame, textvariable=self.quest_ip, width=24).grid(row=0, column=1, sticky="ew", padx=(8, 0))
        frame.columnconfigure(1, weight=1)

        steps = (
            "1. Use 127.0.0.1 when running through Quest Link / Unity Editor.\n"
            "2. Use the Quest headset IP only for a standalone Quest build.\n"
            "3. Start the app and put the Vive tracker on the physical reference point.\n"
            "4. Press Begin Anchor Placement.\n"
            "5. In VR, move your hand marker to the reference point and pinch to confirm."
        )
        ttk.Label(frame, text=steps, justify=tk.LEFT).grid(row=1, column=0, columnspan=2, sticky="ew", pady=(16, 12))

        button_frame = ttk.Frame(frame)
        button_frame.grid(row=2, column=0, columnspan=2, sticky="ew")
        button_frame.columnconfigure(0, weight=1)
        button_frame.columnconfigure(1, weight=1)

        ttk.Button(button_frame, text="Begin Anchor Placement", command=lambda: self._send("BEGIN_ANCHOR_PLACEMENT")).grid(
            row=0, column=0, sticky="ew", padx=(0, 6)
        )
        ttk.Button(button_frame, text="Confirm Anchor", command=lambda: self._send("CONFIRM_ANCHOR_PLACEMENT")).grid(
            row=0, column=1, sticky="ew", padx=(6, 0)
        )
        ttk.Button(button_frame, text="Ping Quest", command=lambda: self._send("PING")).grid(
            row=1, column=0, sticky="ew", padx=(0, 6), pady=(8, 0)
        )
        ttk.Button(button_frame, text="Cancel", command=lambda: self._send("CANCEL_ANCHOR_PLACEMENT")).grid(
            row=1, column=1, sticky="ew", padx=(6, 0), pady=(8, 0)
        )
        ttk.Button(button_frame, text="Clear Anchor", command=lambda: self._send("CLEAR_ANCHOR")).grid(
            row=2, column=0, columnspan=2, sticky="ew", pady=(8, 0)
        )
        ttk.Button(button_frame, text="Use Spatial Anchor Mode", command=lambda: self._send("USE_SPATIAL_ANCHOR_REDIRECTION")).grid(
            row=3, column=0, sticky="ew", padx=(0, 6), pady=(8, 0)
        )
        ttk.Button(button_frame, text="Restore Original Mode", command=lambda: self._send("RESTORE_ORIGINAL_HAND_REDIRECTION")).grid(
            row=3, column=1, sticky="ew", padx=(6, 0), pady=(8, 0)
        )

        ttk.Label(frame, text="Status").grid(row=3, column=0, columnspan=2, sticky="w", pady=(20, 4))
        ttk.Label(frame, textvariable=self.status_text, relief=tk.SUNKEN, padding=8).grid(
            row=4, column=0, columnspan=2, sticky="ew"
        )

        self.log = tk.Text(frame, height=9, wrap="word")
        self.log.grid(row=5, column=0, columnspan=2, sticky="nsew", pady=(12, 0))
        frame.rowconfigure(5, weight=1)

        self.root.protocol("WM_DELETE_WINDOW", self._close)

    def _send(self, command):
        host = self.quest_ip.get().strip()
        if not host:
            self._append_log("Quest IP is empty")
            return

        try:
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
                sock.sendto(command.encode("utf-8"), (host, QUEST_COMMAND_PORT))
            self._append_log(f"Sent: {command}")
        except OSError as exc:
            self._append_log(f"Send failed: {exc}")

    def _start_status_listener(self):
        thread = threading.Thread(target=self._status_loop, daemon=True)
        thread.start()

    def _status_loop(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
            sock.bind(("", PC_STATUS_PORT))
            sock.settimeout(0.5)
            while self.running:
                try:
                    data, addr = sock.recvfrom(4096)
                except socket.timeout:
                    continue
                except OSError:
                    break

                message = data.decode("utf-8", errors="replace").strip()
                self.root.after(0, self._handle_status, addr[0], message)

    def _handle_status(self, host, message):
        self.status_text.set(f"{host}: {message}")
        self._append_log(f"Received: {message}")

    def _append_log(self, message):
        timestamp = datetime.now().strftime("%H:%M:%S")
        self.log.insert(tk.END, f"[{timestamp}] {message}\n")
        self.log.see(tk.END)

    def _close(self):
        self.running = False
        self.root.destroy()


if __name__ == "__main__":
    root = tk.Tk()
    AnchorControlWindow(root)
    root.mainloop()
