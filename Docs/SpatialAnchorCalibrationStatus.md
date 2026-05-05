# Spatial Anchor Calibration Status

## Goal

This project is moving from the original Vive-tracker/head-tracker relative calibration flow toward a PCVR-compatible Spatial Anchor style calibration flow.

The current implementation is intentionally a first working handoff state, not a finished persistent Meta Spatial Anchor system.

## Current Operating Assumptions

- The project is expected to run through Quest Link / PCVR from Unity on a Windows PC.
- Meta/ARFoundation Spatial Anchor creation may not run under Quest Link / Editor.
- When AR anchor creation is unavailable, the system creates a Unity-world `PCVRSessionAnchor` fallback so calibration can still be tested in PCVR.
- The anchor is used as the reference position for `deskOrigin`.
- Manual yaw adjustment is required before enabling the Spatial Anchor based hand redirection.
- The old/original hand redirection path should remain recoverable.

## Intended Calibration Flow

1. Start the PC control window:

   ```powershell
   powershell -ExecutionPolicy Bypass -File D:\work\HandRedirection\Tools\SpatialAnchorCalibration\pc_anchor_control_window.ps1
   ```

2. Use `127.0.0.1` as the target IP when running through Unity Editor / Quest Link.
3. Press `Begin Anchor Placement`.
4. The Quest side enters anchor placement mode.
5. The yellow anchor preview should follow the tracked hand pointer pose.
6. Pinch in VR or press `Confirm Anchor` in the PC window.
7. The created anchor position is applied to `deskOrigin`.
8. Hand redirection remains disabled while desk angle is not confirmed.
9. Use `-1 deg`, `+1 deg`, `-5 deg`, `+5 deg` in the PC window to adjust desk yaw.
10. Press `Confirm Desk Alignment`.
11. Spatial Anchor based hand redirection is enabled.

## Mode Model

`SpatialAnchorRedirectionToggle` controls the active mode:

- `Original` mode enables the original tracking/redirection components.
- `SpatialAnchor` mode disables original-mode components.
- In `SpatialAnchor` mode, hand redirection stays disabled until:
  - an anchor exists,
  - anchor creation is not in progress,
  - placement mode is not active,
  - desk alignment has been confirmed.

The command receiver stays enabled in both modes so the PC window can always switch back.

## Main Components

- `ManualSpatialAnchorPlacer`
  - Receives placement start/confirm/cancel.
  - Tracks a live `OVRHand.PointerPose` by default for anchor preview placement.
  - Creates an ARFoundation anchor when available.
  - Falls back to a `PCVRSessionAnchor` when AR anchor creation is unavailable.

- `SpatialAnchorToDeskOriginBinder`
  - Applies the current anchor position to `deskOrigin`.
  - Keeps the existing desk rotation as the starting angle by default.
  - Provides manual yaw adjustment.
  - Only reports alignment as confirmed after `Confirm Desk Alignment`.

- `SpatialAnchorRedirectionToggle`
  - Switches between original and Spatial Anchor modes.
  - Keeps hand redirection disabled during placement and angle adjustment.
  - While disabled, repeatedly resets redirector hand transforms to the original hand transforms to avoid stale redirected hand visuals.

- `SpatialAnchorPlacementCommandReceiver`
  - Listens for UDP commands on port `9101`.
  - Sends status responses to port `9102`.
  - Supports anchor placement, mode switching, desk yaw adjustment, and desk alignment confirmation.

- `pc_anchor_control_window.ps1`
  - Windows Forms UI for controlling calibration from the PC.
  - Includes buttons for anchor placement, yaw adjustment, alignment confirmation, and mode restore.

## Unity Scene Setup

Use:

```text
Tools > Spatial Anchor > Create Basic Setup
```

This creates or updates `SpatialAnchorCalibrationRoot` with:

- `ManualSpatialAnchorPlacer`
- `SpatialAnchorPlacementCommandReceiver`
- `SpatialAnchorToDeskOriginBinder`
- `SpatialAnchorRedirectionToggle`

Important Inspector values:

```text
ManualSpatialAnchorPlacer
  Source Mode = Ovr Hand Joint
  Prefer Live Hand Pose For Placement = true
  Placement Hand Joint = Pointer Pose

SpatialAnchorToDeskOriginBinder
  Desk Origin = same Transform used by GoGoInteractionController_NoY3.deskOrigin
  Require Manual Rotation Confirmation = true

SpatialAnchorRedirectionToggle
  Start Mode = Original
  Original Mode Behaviours = original tracker/desk driving scripts, usually TrackerToCubeOffsetCalibrator3
  Hand Redirection Behaviours = GoGoInteractionController_NoY3
```

## PC Commands

The PC window sends these UDP commands:

- `BEGIN_ANCHOR_PLACEMENT`
- `CONFIRM_ANCHOR_PLACEMENT`
- `CANCEL_ANCHOR_PLACEMENT`
- `CLEAR_ANCHOR`
- `ROTATE_DESK_LEFT`
- `ROTATE_DESK_RIGHT`
- `ROTATE_DESK_LEFT_LARGE`
- `ROTATE_DESK_RIGHT_LARGE`
- `RESET_DESK_ROTATION`
- `CONFIRM_DESK_ALIGNMENT`
- `USE_SPATIAL_ANCHOR_REDIRECTION`
- `RESTORE_ORIGINAL_HAND_REDIRECTION`
- `PING`

## Known Risks / Current Limitations

- PCVR fallback anchors are session-only Unity transforms, not persistent Meta Spatial Anchors.
- If Unity crashes before the scene is saved, `SpatialAnchorCalibrationRoot` may need to be recreated from the Tools menu.
- `Desk Origin` must be assigned correctly, otherwise Spatial Anchor mode cannot drive the same space as GoGo.
- If another hand redirection script is active besides `GoGoInteractionController_NoY3`, add it manually to `Hand Redirection Behaviours`.
- The current safe default uses `OVRHand.PointerPose`, not fingertip bones. This is more stable for recovery, but less precise than direct fingertip placement.
- GitHub handoff does not include a saved validated Unity scene unless the scene has been saved in Unity.
