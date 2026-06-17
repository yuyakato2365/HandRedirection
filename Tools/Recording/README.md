# PCVR + passthrough recording controller

This tool is for the PCVR workflow where the Quest is connected to the PC and the project is run from Unity with the Play button.

It starts two recordings from the PC:

- MR view: recorded on the PC with `ffmpeg` from Oculus Mirror, a named window, or the desktop.
- Passthrough-only view: streamed by `PassthroughTcpStreamRecorderBridge` from the Unity Play session to the PC and saved as MJPEG AVI.

## Unity setup

1. Open the scene used for PCVR Play mode.
2. Run `Tools > Recording > Create Or Update Passthrough Recording Bridge`.
3. If the bridge cannot auto-find the Meta `PassthroughCameraAccess` component, assign it manually in the Inspector.
4. Press Play before pressing Start in the PC recording tool.

## PC setup

1. Make sure Quest Link / PCVR is running.
2. Make sure `ffmpeg.exe` is available. The tool can use TouchDesigner ffmpeg if installed at the default path.
3. Run from the repository root:

```powershell
python Tools\Recording\quest_recording_controller.py
```

Fill in the output folder, confirm the ffmpeg path, then press Start. Press Stop to stop both recordings.

The passthrough video is saved as `passthrough_*.avi`. The MR video is saved as `mr_pcvr_*.mp4`.

If Oculus Mirror cannot be captured by title, change `MR source` to `Desktop` or `Window title`.
