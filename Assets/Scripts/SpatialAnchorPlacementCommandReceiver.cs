using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class SpatialAnchorPlacementCommandReceiver : MonoBehaviour
{
    [Header("Command UDP")]
    public int listenPort = 9101;
    public bool forceIPv4 = true;

    [Header("Status UDP")]
    public bool sendStatus = true;
    public int statusPort = 9102;
    public string statusHostOverride = "";

    [Header("Anchor Placement")]
    public ManualSpatialAnchorPlacer placer;
    public SpatialAnchorToDeskOriginBinder deskBinder;
    public SpatialAnchorRedirectionToggle featureToggle;
    public PassthroughScaniverseModeController passthroughScaniverseModeController;
    public GoGoInteractionController_NoY3 handRedirectorManager;

    [Header("Exhibition Reset")]
    public ExhibitionExperienceResetter experienceResetter;

    [Header("Scale Placement Challenge")]
    public ScalePlacementChallengeController scalePlacementChallenge;

    [Header("Debug")]
    public bool logCommands = false;

    private UdpClient udp;
    private Thread recvThread;
    private volatile bool running;
    private readonly object queueLock = new object();
    private readonly Queue<string> pendingCommands = new Queue<string>();
    private IPEndPoint lastSender;

    private void Awake()
    {
        if (placer == null)
            placer = FindAnyObjectByType<ManualSpatialAnchorPlacer>();
        if (deskBinder == null)
            deskBinder = FindAnyObjectByType<SpatialAnchorToDeskOriginBinder>();
        if (featureToggle == null)
            featureToggle = FindAnyObjectByType<SpatialAnchorRedirectionToggle>();
        if (passthroughScaniverseModeController == null)
            passthroughScaniverseModeController = FindAnyObjectByType<PassthroughScaniverseModeController>();
        if (handRedirectorManager == null)
            handRedirectorManager = FindAnyObjectByType<GoGoInteractionController_NoY3>();
        EnsureExperienceResetter();
        EnsureScalePlacementChallenge();
    }

    private void OnEnable()
    {
        EnsureScalePlacementChallenge();
        SubscribePlacerEvents();
        StartReceiver();
    }

    private void OnDisable()
    {
        StopReceiver();
        UnsubscribePlacerEvents();
        if (scalePlacementChallenge != null)
            scalePlacementChallenge.PatternCompleted -= OnScalePlacementPatternCompleted;
    }

    private void Update()
    {
        while (TryDequeueCommand(out string command))
            HandleCommand(command);
    }

    private void SubscribePlacerEvents()
    {
        if (placer == null)
            return;

        placer.PlacementStarted += OnPlacementStarted;
        placer.PlacementCanceled += OnPlacementCanceled;
        placer.AnchorCreated += OnAnchorCreated;
        placer.AnchorTransformCreated += OnAnchorTransformCreated;
        placer.AnchorCleared += OnAnchorCleared;
        placer.AnchorCreateFailed += OnAnchorCreateFailed;
    }

    private void UnsubscribePlacerEvents()
    {
        if (placer == null)
            return;

        placer.PlacementStarted -= OnPlacementStarted;
        placer.PlacementCanceled -= OnPlacementCanceled;
        placer.AnchorCreated -= OnAnchorCreated;
        placer.AnchorTransformCreated -= OnAnchorTransformCreated;
        placer.AnchorCleared -= OnAnchorCleared;
        placer.AnchorCreateFailed -= OnAnchorCreateFailed;
    }

    private void StartReceiver()
    {
        try
        {
            udp = forceIPv4 ? new UdpClient(AddressFamily.InterNetwork) : new UdpClient(AddressFamily.Unspecified);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));
            running = true;
            recvThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "SpatialAnchorPlacementCommandReceiver"
            };
            recvThread.Start();
            SendStatus("QUEST_COMMAND_RECEIVER_READY");
        }
        catch (Exception e)
        {
            Debug.LogError($"[SpatialAnchorPlacementCommandReceiver] UDP init failed: {e.Message}");
            running = false;
            StopReceiver();
        }
    }

    private void StopReceiver()
    {
        running = false;

        try { udp?.Close(); } catch { }
        udp = null;

        try
        {
            if (recvThread != null && recvThread.IsAlive)
                recvThread.Join(200);
        }
        catch { }

        recvThread = null;
    }

    private void ReceiveLoop()
    {
        while (running && udp != null)
        {
            try
            {
                IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = udp.Receive(ref sender);
                string command = Encoding.UTF8.GetString(data).Trim();

                lock (queueLock)
                {
                    lastSender = sender;
                    pendingCommands.Enqueue(command);
                }
            }
            catch (SocketException)
            {
                if (!running)
                    break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SpatialAnchorPlacementCommandReceiver] Receive error: {e.Message}");
            }
        }
    }

    private bool TryDequeueCommand(out string command)
    {
        lock (queueLock)
        {
            if (pendingCommands.Count == 0)
            {
                command = null;
                return false;
            }

            command = pendingCommands.Dequeue();
            return true;
        }
    }

    private void HandleCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        string normalized = command.Trim().ToUpperInvariant();
        if (logCommands)
            Debug.Log($"[SpatialAnchorPlacementCommandReceiver] Command: {normalized}");

        if (TryHandleTrackerOffsetCommand(normalized))
            return;
        if (TryHandleHandMappingScaleCommand(normalized))
            return;
        if (TryHandleGazeTargetRadiusCommand(normalized))
            return;
        if (TryHandleDeskScaleCommand(normalized))
            return;
        if (TryHandleTargetRingCommand(normalized))
            return;
        if (TryHandleTrackerAxisCommand(normalized))
            return;

        if (placer == null)
        {
            SendStatus("ERROR placer_not_assigned");
            return;
        }

        switch (normalized)
        {
            case "BEGIN_ANCHOR_PLACEMENT":
                placer.BeginPlacement();
                if (featureToggle != null)
                    featureToggle.UseSpatialAnchorMode();
                break;
            case "CANCEL_ANCHOR_PLACEMENT":
                placer.CancelPlacement();
                break;
            case "CONFIRM_ANCHOR_PLACEMENT":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder placementConfirmBinder) &&
                    placementConfirmBinder.IsAdjustingAlignment)
                {
                    placementConfirmBinder.ConfirmManualRotationAlignment();
                }
                if (placer.IsPlacementMode)
                    placer.ConfirmPlacement();
                break;
            case "CLEAR_ANCHOR":
                placer.ClearAnchor();
                SendStatus("ANCHOR_CLEARED");
                break;
            case "LOAD_SAVED_ANCHOR":
            case "LOAD_PERSISTENT_ANCHOR":
                placer.LoadSavedAnchor();
                if (featureToggle != null)
                    featureToggle.UseSpatialAnchorMode();
                SendStatus("LOAD_SAVED_ANCHOR_REQUESTED");
                break;
            case "CLEAR_SAVED_ANCHOR":
            case "ERASE_SAVED_ANCHOR":
                placer.ClearSavedAnchor();
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder savedClearBinder))
                    savedClearBinder.ClearSavedOffsetPrefs();
                SendStatus("CLEAR_SAVED_ANCHOR_REQUESTED");
                break;
            case "HAS_SAVED_ANCHOR":
                SendStatus(placer.HasSavedAnchor ? "HAS_SAVED_ANCHOR true" : "HAS_SAVED_ANCHOR false");
                break;
            case "BEGIN_DESK_ROTATION_ADJUSTMENT":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder beginBinder))
                {
                    beginBinder.BeginManualRotationAlignment();
                    SendStatus("DESK_ALIGNMENT_STARTED");
                }
                break;
            case "ROTATE_DESK_LEFT":
            case "ADJUST_DESK_YAW_LEFT":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder leftBinder))
                {
                    leftBinder.AdjustYawLeft();
                    SendStatus($"DESK_YAW_ADJUSTED {leftBinder.CurrentYawAdjustmentDegrees:0.###}");
                }
                break;
            case "ROTATE_DESK_RIGHT":
            case "ADJUST_DESK_YAW_RIGHT":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder rightBinder))
                {
                    rightBinder.AdjustYawRight();
                    SendStatus($"DESK_YAW_ADJUSTED {rightBinder.CurrentYawAdjustmentDegrees:0.###}");
                }
                break;
            case "ROTATE_DESK_LEFT_LARGE":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder leftLargeBinder))
                {
                    leftLargeBinder.AdjustYawLeftLarge();
                    SendStatus($"DESK_YAW_ADJUSTED {leftLargeBinder.CurrentYawAdjustmentDegrees:0.###}");
                }
                break;
            case "ROTATE_DESK_RIGHT_LARGE":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder rightLargeBinder))
                {
                    rightLargeBinder.AdjustYawRightLarge();
                    SendStatus($"DESK_YAW_ADJUSTED {rightLargeBinder.CurrentYawAdjustmentDegrees:0.###}");
                }
                break;
            case "RESET_DESK_ROTATION":
            case "RESET_DESK_YAW":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder resetBinder))
                {
                    resetBinder.ResetYawAdjustment();
                    SendStatus("DESK_YAW_RESET");
                }
                break;
            case "CONFIRM_DESK_ALIGNMENT":
            case "CONFIRM_DESK_ROTATION":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder confirmBinder))
                {
                    confirmBinder.ConfirmManualRotationAlignment();
                    SendStatus("DESK_ALIGNMENT_CONFIRMED");
                }
                break;
            case "SET_REDIRECTION_ORIGIN":
            case "REARM_REDIRECTION_ORIGIN":
            case "REARM_RIGHT_PINCH_REDIRECTION_ORIGIN":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder redirectionOriginBinder))
                {
                    redirectionOriginBinder.RearmRightPinchRedirectionOrigin();
                    if (placer != null)
                        placer.SetStatusMessage("Right pinch will set redirection origin");
                    SendStatus("REDIRECTION_ORIGIN_ARMED_RIGHT_PINCH");
                }
                break;
            case "RESET_REDIRECTION_ORIGIN":
            case "RESET_REDIRECTION_ORIGIN_TO_DESK":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder resetRedirectionOriginBinder))
                {
                    resetRedirectionOriginBinder.ResetRedirectionOriginToDesk();
                    if (placer != null)
                        placer.SetStatusMessage("Redirection origin reset to desk origin");
                    SendStatus("REDIRECTION_ORIGIN_RESET_TO_DESK");
                }
                break;
            case "ENABLE_ANCHOR_REDIRECTION":
            case "USE_SPATIAL_ANCHOR_REDIRECTION":
                if (featureToggle != null)
                {
                    featureToggle.UseSpatialAnchorMode();
                    SendStatus("SPATIAL_ANCHOR_REDIRECTION_MODE");
                }
                else
                {
                    SendStatus("ERROR toggle_not_assigned");
                }
                break;
            case "DISABLE_ANCHOR_REDIRECTION":
            case "RESTORE_ORIGINAL_HAND_REDIRECTION":
                if (featureToggle != null)
                {
                    featureToggle.UseOriginalMode();
                    SendStatus("ORIGINAL_HAND_REDIRECTION_MODE");
                }
                else
                {
                    SendStatus("ERROR toggle_not_assigned");
                }
                break;
            case "USE_DIMINISHED_REALITY":
            case "USE_PASSTHROUGH_DIMINISHED_REALITY":
            case "PASSTHROUGH_DIMINISHED_REALITY":
                if (TryGetPassthroughScaniverseModeController(out PassthroughScaniverseModeController diminishedController))
                {
                    diminishedController.UseDiminishedRealityMode();
                    SendStatus("PASSTHROUGH_SCANIVERSE_MODE diminished_reality");
                }
                break;
            case "USE_SCALED_SCANIVERSE":
            case "USE_SCALED_SCANIVERSE_ROOM":
            case "USE_SCANIVERSE_ROOM_DEFORMATION":
            case "PASSTHROUGH_SCALED_SCANIVERSE":
                if (TryGetPassthroughScaniverseModeController(out PassthroughScaniverseModeController scaledController))
                {
                    scaledController.UseScaledScaniverseRoomMode();
                    SendStatus("PASSTHROUGH_SCANIVERSE_MODE scaled_scaniverse_room");
                }
                break;
            case "TOGGLE_PASSTHROUGH_SCANIVERSE_MODE":
            case "TOGGLE_DIMINISHED_REALITY":
                if (TryGetPassthroughScaniverseModeController(out PassthroughScaniverseModeController toggleController))
                {
                    toggleController.ToggleMode();
                    SendStatus($"PASSTHROUGH_SCANIVERSE_MODE {toggleController.CurrentMode}");
                }
                break;
            case "ENABLE_GAZE_DEBUG_VISUALS":
            case "SHOW_GAZE_DEBUG_VISUALS":
                if (TryGetHandRedirectorManager(out GoGoInteractionController_NoY3 gazeDebugEnableController))
                {
                    gazeDebugEnableController.SetHmdGazeDebugVisuals(true);
                    SendStatus("GAZE_DEBUG_VISUALS enabled");
                }
                break;
            case "DISABLE_GAZE_DEBUG_VISUALS":
            case "HIDE_GAZE_DEBUG_VISUALS":
                if (TryGetHandRedirectorManager(out GoGoInteractionController_NoY3 gazeDebugDisableController))
                {
                    gazeDebugDisableController.SetHmdGazeDebugVisuals(false);
                    SendStatus("GAZE_DEBUG_VISUALS disabled");
                }
                break;
            case "TOGGLE_GAZE_DEBUG_VISUALS":
                if (TryGetHandRedirectorManager(out GoGoInteractionController_NoY3 gazeDebugToggleController))
                {
                    gazeDebugToggleController.ToggleHmdGazeDebugVisuals();
                    SendStatus($"GAZE_DEBUG_VISUALS {(gazeDebugToggleController.showHmdGazeDebugVisuals ? "enabled" : "disabled")}");
                }
                break;
            case "ENABLE_ANCHOR_AXES":
                placer.SetAnchorCoordinateAxesVisible(true);
                SendStatus("ANCHOR_AXES enabled");
                break;
            case "DISABLE_ANCHOR_AXES":
                placer.SetAnchorCoordinateAxesVisible(false);
                SendStatus("ANCHOR_AXES disabled");
                break;
            case "ENABLE_ORIGIN_AXES":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder originAxesEnableBinder))
                {
                    originAxesEnableBinder.SetOriginCoordinateAxesVisible(true);
                    SendStatus("ORIGIN_AXES enabled");
                }
                break;
            case "DISABLE_ORIGIN_AXES":
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder originAxesDisableBinder))
                {
                    originAxesDisableBinder.SetOriginCoordinateAxesVisible(false);
                    SendStatus("ORIGIN_AXES disabled");
                }
                break;
            case "ENABLE_TRACKER_AXES":
                SetTrackerAxesVisible(true);
                break;
            case "DISABLE_TRACKER_AXES":
                SetTrackerAxesVisible(false);
                break;
            case "ENABLE_ALL_COORDINATE_AXES":
                placer.SetAnchorCoordinateAxesVisible(true);
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder allAxesEnableBinder))
                    allAxesEnableBinder.SetOriginCoordinateAxesVisible(true);
                SetTrackerAxesVisible(true);
                SendStatus("ALL_COORDINATE_AXES enabled");
                break;
            case "DISABLE_ALL_COORDINATE_AXES":
                placer.SetAnchorCoordinateAxesVisible(false);
                if (TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder allAxesDisableBinder))
                    allAxesDisableBinder.SetOriginCoordinateAxesVisible(false);
                SetTrackerAxesVisible(false);
                SendStatus("ALL_COORDINATE_AXES disabled");
                break;
            case "NEXT_PARTICIPANT":
            case "RESET_FOR_NEXT_PARTICIPANT":
            case "RESET_EXPERIENCE_FOR_NEXT_PARTICIPANT":
            case "RESET_OBJECT_SCALES":
                int restoredCount = ResetExperienceForNextParticipant();
                SendStatus($"NEXT_PARTICIPANT_RESET_DONE {restoredCount}");
                break;
            case "PING":
                placer.SetStatusMessage("PING received from PC");
                SendStatus("PONG");
                break;
            default:
                SendStatus($"ERROR unknown_command {normalized}");
                break;
        }
    }

    private void OnPlacementStarted()
    {
        SendStatus("ANCHOR_PLACEMENT_STARTED");
    }

    private void OnPlacementCanceled()
    {
        SendStatus("ANCHOR_PLACEMENT_CANCELED");
    }

    private void OnAnchorCreated(UnityEngine.XR.ARFoundation.ARAnchor anchor)
    {
        SendStatus($"ANCHOR_CREATED {anchor.trackableId}");
    }

    private void OnAnchorTransformCreated(Transform anchorTransform)
    {
        SendStatus("ANCHOR_TRANSFORM_CREATED_BEGIN_DESK_ALIGNMENT");
    }

    private void OnAnchorCleared()
    {
        SendStatus("ANCHOR_CLEARED");
    }

    private void OnAnchorCreateFailed(string reason)
    {
        SendStatus($"ANCHOR_CREATE_FAILED {reason}");
    }

    private bool TryGetDeskBinder(out SpatialAnchorToDeskOriginBinder resolvedBinder)
    {
        resolvedBinder = deskBinder != null ? deskBinder : FindAnyObjectByType<SpatialAnchorToDeskOriginBinder>();
        if (resolvedBinder != null)
            return true;

        SendStatus("ERROR desk_binder_not_assigned");
        return false;
    }

    private void EnsureExperienceResetter()
    {
        if (experienceResetter != null)
            return;

        experienceResetter = FindAnyObjectByType<ExhibitionExperienceResetter>();
        if (experienceResetter != null)
            return;

        GameObject resetterObject = new GameObject("ExhibitionExperienceResetter");
        experienceResetter = resetterObject.AddComponent<ExhibitionExperienceResetter>();
    }

    private bool TryGetPassthroughScaniverseModeController(out PassthroughScaniverseModeController controller)
    {
        controller = passthroughScaniverseModeController != null
            ? passthroughScaniverseModeController
            : FindAnyObjectByType<PassthroughScaniverseModeController>();
        if (controller != null)
        {
            passthroughScaniverseModeController = controller;
            return true;
        }

        GameObject obj = new GameObject("PassthroughScaniverseModeController");
        controller = obj.AddComponent<PassthroughScaniverseModeController>();
        passthroughScaniverseModeController = controller;
        return true;
    }

    private void EnsureScalePlacementChallenge()
    {
        if (scalePlacementChallenge == null)
            scalePlacementChallenge = FindAnyObjectByType<ScalePlacementChallengeController>();
        if (scalePlacementChallenge == null)
        {
            GameObject challengeObject = new GameObject("Scale Placement Challenge");
            challengeObject.transform.SetParent(transform, false);
            scalePlacementChallenge = challengeObject.AddComponent<ScalePlacementChallengeController>();
        }

        scalePlacementChallenge.PatternCompleted -= OnScalePlacementPatternCompleted;
        scalePlacementChallenge.PatternCompleted += OnScalePlacementPatternCompleted;
    }

    private void OnScalePlacementPatternCompleted(string patternId)
    {
        SendStatus($"TARGET_RING_COMPLETED {patternId}");
    }

    private void SetTrackerAxesVisible(bool visible)
    {
        TrackerToCubeOffsetCalibrator3 calibrator = FindTrackerCalibrator();
        if (calibrator == null)
        {
            SendStatus("ERROR tracker_calibrator_not_found");
            return;
        }

        calibrator.SetDetectedPoseAxesVisible(visible);
        SendStatus($"TRACKER_AXES {(visible ? "enabled" : "disabled")}");
    }

    private bool TryHandleTrackerAxisCommand(string command)
    {
        string[] parts = command.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        bool visible;
        if (parts[0] == "ENABLE_TRACKER_AXIS" || parts[0] == "SHOW_TRACKER_AXIS")
            visible = true;
        else if (parts[0] == "DISABLE_TRACKER_AXIS" || parts[0] == "HIDE_TRACKER_AXIS")
            visible = false;
        else
            return false;

        if (parts.Length != 2 || !uint.TryParse(parts[1], out uint objectId))
        {
            SendStatus("ERROR tracker_axis_format");
            return true;
        }

        TrackerToCubeOffsetCalibrator3 calibrator = FindTrackerCalibrator();
        if (calibrator == null || !calibrator.SetDetectedPoseAxisVisible(objectId, visible))
        {
            SendStatus($"ERROR tracker_axis_target_not_found {objectId}");
            return true;
        }

        SendStatus($"TRACKER_AXIS {objectId} {(visible ? "enabled" : "disabled")}");
        return true;
    }

    private bool TryHandleTrackerOffsetCommand(string command)
    {
        string[] parts = command.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        if (parts[0] == "GET_TRACKER_GROUP_OFFSET")
        {
            TrackerToCubeOffsetCalibrator3 calibrator = FindTrackerCalibrator();
            if (calibrator == null)
            {
                SendStatus("ERROR tracker_calibrator_not_found");
                return true;
            }

            SendTrackerGroupOffsetStatus(calibrator.objectGroupDeskPositionOffset);
            return true;
        }

        if (parts[0] == "SET_TRACKER_GROUP_OFFSET")
        {
            if (parts.Length != 4 ||
                !TryParseInvariant(parts[1], out float x) ||
                !TryParseInvariant(parts[2], out float y) ||
                !TryParseInvariant(parts[3], out float z))
            {
                SendStatus("ERROR tracker_group_offset_format");
                return true;
            }

            TrackerToCubeOffsetCalibrator3 calibrator = FindTrackerCalibrator();
            if (calibrator == null)
            {
                SendStatus("ERROR tracker_calibrator_not_found");
                return true;
            }

            Vector3 offset = new Vector3(x, y, z);
            calibrator.SetObjectGroupDeskPositionOffset(offset);
            SendTrackerGroupOffsetStatus(offset);
            return true;
        }

        if (parts[0] == "RESET_TRACKER_GROUP_OFFSET")
        {
            TrackerToCubeOffsetCalibrator3 calibrator = FindTrackerCalibrator();
            if (calibrator == null)
            {
                SendStatus("ERROR tracker_calibrator_not_found");
                return true;
            }

            calibrator.SetObjectGroupDeskPositionOffset(Vector3.zero);
            SendTrackerGroupOffsetStatus(Vector3.zero);
            return true;
        }

        if (parts[0] == "GET_TRACKER_OFFSETS")
        {
            TrackerToCubeOffsetCalibrator3 calibrator = FindTrackerCalibrator();
            if (calibrator == null)
            {
                SendStatus("ERROR tracker_calibrator_not_found");
                return true;
            }

            int count = 0;
            foreach (TrackerToCubeOffsetCalibrator3.TargetEntry target in calibrator.EnumerateTargetOffsets())
            {
                if (target == null)
                    continue;
                SendTrackerOffsetStatus(target.objectId, target.centerOffsetInTracker, target.centerEulerOffset);
                count++;
            }
            SendStatus($"TRACKER_OFFSETS_DONE {count}");
            return true;
        }

        if (parts[0] == "SET_TRACKER_OFFSET")
        {
            if (parts.Length != 8 ||
                !uint.TryParse(parts[1], out uint objectId) ||
                !TryParseInvariant(parts[2], out float px) ||
                !TryParseInvariant(parts[3], out float py) ||
                !TryParseInvariant(parts[4], out float pz) ||
                !TryParseInvariant(parts[5], out float ex) ||
                !TryParseInvariant(parts[6], out float ey) ||
                !TryParseInvariant(parts[7], out float ez))
            {
                SendStatus("ERROR tracker_offset_format");
                return true;
            }

            TrackerToCubeOffsetCalibrator3 calibrator = FindTrackerCalibrator();
            if (calibrator == null || !calibrator.TrySetTargetOffset(
                    objectId,
                    new Vector3(px, py, pz),
                    new Vector3(ex, ey, ez)))
            {
                SendStatus($"ERROR tracker_target_not_found {objectId}");
                return true;
            }

            SendTrackerOffsetStatus(objectId, new Vector3(px, py, pz), new Vector3(ex, ey, ez));
            return true;
        }

        if (parts[0] == "RESET_TRACKER_OFFSET")
        {
            if (parts.Length != 2 || !uint.TryParse(parts[1], out uint objectId))
            {
                SendStatus("ERROR tracker_offset_format");
                return true;
            }

            TrackerToCubeOffsetCalibrator3 calibrator = FindTrackerCalibrator();
            if (calibrator == null || !calibrator.TrySetTargetOffset(objectId, Vector3.zero, Vector3.zero))
            {
                SendStatus($"ERROR tracker_target_not_found {objectId}");
                return true;
            }

            SendTrackerOffsetStatus(objectId, Vector3.zero, Vector3.zero);
            return true;
        }

        return false;
    }

    private bool TryHandleHandMappingScaleCommand(string command)
    {
        string[] parts = command.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        if (parts[0] != "GET_HAND_MAPPING_SCALE_MULTIPLIER" &&
            parts[0] != "SET_HAND_MAPPING_SCALE_MULTIPLIER" &&
            parts[0] != "RESET_HAND_MAPPING_SCALE_MULTIPLIER")
            return false;

        // Use the explicitly assigned runtime hand-redirection manager. The
        // scene also contains stray GoGo components on mesh children; a generic
        // FindAnyObjectByType can update one of those without affecting hands.
        if (!TryGetHandRedirectorManager(out GoGoInteractionController_NoY3 controller))
            return true;

        if (parts[0] == "SET_HAND_MAPPING_SCALE_MULTIPLIER")
        {
            if (parts.Length != 2 || !TryParseInvariant(parts[1], out float value) || value < 0f)
            {
                SendStatus("ERROR hand_mapping_scale_multiplier_format");
                return true;
            }
            controller.SetHandMappingScaleChangeMultiplier(value);
        }
        else if (parts[0] == "RESET_HAND_MAPPING_SCALE_MULTIPLIER")
        {
            controller.SetHandMappingScaleChangeMultiplier(1f);
        }

        SendStatus(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "HAND_MAPPING_SCALE_MULTIPLIER {0:R}",
            controller.handMappingScaleChangeMultiplier));
        return true;
    }

    private bool TryHandleGazeTargetRadiusCommand(string command)
    {
        string[] parts = command.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        if (parts[0] != "GET_GAZE_TARGET_RADIUS" &&
            parts[0] != "SET_GAZE_TARGET_RADIUS" &&
            parts[0] != "RESET_GAZE_TARGET_RADIUS")
            return false;

        if (!TryGetHandRedirectorManager(out GoGoInteractionController_NoY3 controller))
            return true;

        if (parts[0] == "GET_GAZE_TARGET_RADIUS")
        {
            if (parts.Length == 1)
            {
                for (int i = 0; i < controller.objects.Count; i++)
                {
                    GoGoInteractionController_NoY3.WarpObjectEntry entry = controller.objects[i];
                    if (entry != null && entry.enabled && !string.IsNullOrWhiteSpace(entry.name))
                        SendGazeTargetRadiusStatus(controller, entry.name);
                }
                return true;
            }
            if (parts.Length != 2)
            {
                SendStatus("ERROR gaze_target_radius_format");
                return true;
            }
            SendGazeTargetRadiusStatus(controller, parts[1]);
            return true;
        }

        if (parts[0] == "SET_GAZE_TARGET_RADIUS")
        {
            if (parts.Length != 3 || !TryParseInvariant(parts[2], out float value) || value < 0f)
            {
                SendStatus("ERROR gaze_target_radius_format");
                return true;
            }
            if (!controller.SetGazeTargetRadius(parts[1], value))
            {
                SendStatus($"ERROR gaze_target_not_found {parts[1]}");
                return true;
            }
        }
        else if (parts[0] == "RESET_GAZE_TARGET_RADIUS")
        {
            if (parts.Length != 2 || !controller.SetGazeTargetRadius(parts[1], 0.2f))
            {
                SendStatus($"ERROR gaze_target_not_found {(parts.Length > 1 ? parts[1] : "missing")}");
                return true;
            }
        }

        SendGazeTargetRadiusStatus(controller, parts[1]);
        return true;
    }

    private bool TryHandleDeskScaleCommand(string command)
    {
        string[] parts = command.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 ||
            (parts[0] != "GET_DESK_SCALE" && parts[0] != "SET_DESK_SCALE"))
            return false;

        DeskScaleSliderPanel slider = FindAnyObjectByType<DeskScaleSliderPanel>();
        if (slider == null)
        {
            SendStatus("ERROR desk_scale_slider_not_found");
            return true;
        }

        if (parts[0] == "GET_DESK_SCALE")
        {
            SendDeskScaleStatus(slider.currentScale);
            return true;
        }

        if (parts.Length != 3 || !TryParseInvariant(parts[1], out float requestedScale) ||
            requestedScale <= 0f || (parts[2] != "0" && parts[2] != "1"))
        {
            SendStatus("ERROR desk_scale_format");
            return true;
        }
        if (requestedScale < slider.minScale || requestedScale > slider.maxScale)
        {
            SendStatus(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "ERROR desk_scale_out_of_range {0:R} {1:R}", slider.minScale, slider.maxScale));
            return true;
        }

        bool useBlackout = parts[2] == "1";
        if (!useBlackout)
        {
            SendDeskScaleStatus(slider.SetScaleFromExternal(requestedScale));
            return true;
        }

        HmdBlackoutFader fader = HmdBlackoutFader.GetOrCreate();
        SendStatus("DESK_SCALE_BLACKOUT_STARTED");
        fader.BeginBlackout(() => SendDeskScaleStatus(slider.SetScaleFromExternal(requestedScale)));
        return true;
    }

    private bool TryHandleTargetRingCommand(string command)
    {
        string[] parts = command.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 ||
            (parts[0] != "GET_TARGET_RING_PATTERN" && parts[0] != "SET_TARGET_RING_PATTERN" &&
             parts[0] != "GET_TARGET_RING_SETTINGS" && parts[0] != "SET_TARGET_RING_SETTINGS" &&
             parts[0] != "DISABLE_TARGET_RING_CHALLENGE"))
            return false;

        EnsureScalePlacementChallenge();
        if (scalePlacementChallenge == null)
        {
            SendStatus("ERROR target_ring_controller_not_found");
            return true;
        }

        if (parts[0] == "GET_TARGET_RING_PATTERN")
        {
            SendTargetRingStatus();
            return true;
        }

        if (parts[0] == "DISABLE_TARGET_RING_CHALLENGE")
        {
            scalePlacementChallenge.DisableChallenge();
            SendTargetRingStatus();
            return true;
        }

        if (parts[0] == "GET_TARGET_RING_SETTINGS")
        {
            if (parts.Length > 2)
            {
                SendStatus("ERROR target_ring_settings_format");
                return true;
            }
            SendTargetRingSettingsStatus(parts.Length == 2 ? parts[1] : scalePlacementChallenge.ActivePatternId);
            return true;
        }

        if (parts[0] == "SET_TARGET_RING_SETTINGS")
        {
            // New format includes the target object ID after the pattern ID.
            // Keep accepting the old format so an already-open launcher can
            // still update numeric values until it is restarted.
            bool includesTargetObject = parts.Length == 12;
            int valueStart = includesTargetObject ? 3 : 2;
            if ((!includesTargetObject && parts.Length != 11) ||
                !TryParseInvariant(parts[valueStart], out float sx) || !TryParseInvariant(parts[valueStart + 1], out float sy) || !TryParseInvariant(parts[valueStart + 2], out float sz) ||
                !TryParseInvariant(parts[valueStart + 3], out float stx) || !TryParseInvariant(parts[valueStart + 4], out float sty) || !TryParseInvariant(parts[valueStart + 5], out float stz) ||
                !TryParseInvariant(parts[valueStart + 6], out float ptx) || !TryParseInvariant(parts[valueStart + 7], out float pty) || !TryParseInvariant(parts[valueStart + 8], out float ptz) ||
                sx <= 0f || sy <= 0f || sz <= 0f || stx < 0f || sty < 0f || stz < 0f || ptx <= 0f || pty < 0f || ptz <= 0f)
            {
                SendStatus("ERROR target_ring_settings_format");
                return true;
            }

            string targetObjectId = includesTargetObject
                ? parts[2]
                : GetCurrentTargetObjectId(parts[1]);
            if (!scalePlacementChallenge.SetPatternSettings(
                    parts[1], targetObjectId,
                    new Vector3(sx, sy, sz),
                    new Vector3(stx, sty, stz),
                    new Vector3(ptx, pty, ptz)))
            {
                SendStatus($"ERROR target_ring_pattern_not_found {parts[1]}");
                return true;
            }
            SendTargetRingSettingsStatus(parts[1]);
            return true;
        }

        if (parts.Length != 3 || (parts[2] != "0" && parts[2] != "1"))
        {
            SendStatus("ERROR target_ring_pattern_format");
            return true;
        }

        bool resetSizes = parts[2] == "1";
        if (!scalePlacementChallenge.ActivatePattern(parts[1], resetSizes))
        {
            SendStatus($"ERROR target_ring_pattern_not_found {parts[1]}");
            return true;
        }

        SendTargetRingStatus();
        return true;
    }

    private void SendTargetRingStatus()
    {
        if (!scalePlacementChallenge.IsChallengeEnabled)
        {
            SendStatus("TARGET_RING_PATTERN OFF inactive");
            return;
        }
        SendStatus($"TARGET_RING_PATTERN {scalePlacementChallenge.ActivePatternId} {(scalePlacementChallenge.IsComplete ? "complete" : "active")}");
    }

    private void SendTargetRingSettingsStatus(string patternId)
    {
        if (!scalePlacementChallenge.TryGetPatternSettings(
                patternId, out string targetObjectId,
                out Vector3 scale, out Vector3 scaleTolerance, out Vector3 positionTolerance))
        {
            SendStatus($"ERROR target_ring_pattern_not_found {patternId}");
            return;
        }
        SendStatus(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "TARGET_RING_SETTINGS {0} {1} {2:R} {3:R} {4:R} {5:R} {6:R} {7:R} {8:R} {9:R} {10:R}",
            patternId.ToUpperInvariant(),
            targetObjectId,
            scale.x, scale.y, scale.z,
            scaleTolerance.x, scaleTolerance.y, scaleTolerance.z,
            positionTolerance.x, positionTolerance.y, positionTolerance.z));
    }

    private string GetCurrentTargetObjectId(string patternId)
    {
        return scalePlacementChallenge.TryGetPatternSettings(
            patternId, out string targetObjectId,
            out _, out _, out _)
            ? targetObjectId
            : "1";
    }

    private void SendDeskScaleStatus(float scale)
    {
        SendStatus(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "DESK_SCALE {0:R}", scale));
    }

    private void SendGazeTargetRadiusStatus(GoGoInteractionController_NoY3 controller, string objectId)
    {
        if (!controller.TryGetGazeTargetRadius(objectId, out float radius))
        {
            SendStatus($"ERROR gaze_target_not_found {objectId}");
            return;
        }
        SendStatus(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "GAZE_TARGET_RADIUS {0} {1:R}", objectId, radius));
    }

    private static bool TryParseInvariant(string value, out float result)
    {
        return float.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out result);
    }

    private static TrackerToCubeOffsetCalibrator3 FindTrackerCalibrator()
    {
        return FindAnyObjectByType<TrackerToCubeOffsetCalibrator3>(FindObjectsInactive.Include);
    }

    private void SendTrackerOffsetStatus(uint objectId, Vector3 position, Vector3 euler)
    {
        SendStatus(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "TRACKER_OFFSET {0} {1:R} {2:R} {3:R} {4:R} {5:R} {6:R}",
            objectId,
            position.x, position.y, position.z,
            euler.x, euler.y, euler.z));
    }

    private void SendTrackerGroupOffsetStatus(Vector3 position)
    {
        SendStatus(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "TRACKER_GROUP_OFFSET {0:R} {1:R} {2:R}",
            position.x, position.y, position.z));
    }

    private bool TryGetHandRedirectorManager(out GoGoInteractionController_NoY3 controller)
    {
        controller = handRedirectorManager != null
            ? handRedirectorManager
            : FindAnyObjectByType<GoGoInteractionController_NoY3>();

        if (controller == null)
        {
            SendStatus("ERROR hand_redirector_manager_not_found");
            return false;
        }

        handRedirectorManager = controller;
        return true;
    }

    private int ResetExperienceForNextParticipant()
    {
        EnsureExperienceResetter();
        if (experienceResetter == null)
            return 0;

        return experienceResetter.ResetForNextParticipant();
    }

    private void SendStatus(string message)
    {
        if (!sendStatus)
            return;

        try
        {
            string host = statusHostOverride;
            if (string.IsNullOrWhiteSpace(host))
            {
                lock (queueLock)
                {
                    host = lastSender != null ? lastSender.Address.ToString() : null;
                }
            }

            if (string.IsNullOrWhiteSpace(host))
                return;

            using (UdpClient statusClient = new UdpClient())
            {
                byte[] data = Encoding.UTF8.GetBytes(message);
                statusClient.Send(data, data.Length, host, statusPort);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SpatialAnchorPlacementCommandReceiver] Status send failed: {e.Message}");
        }
    }
}
