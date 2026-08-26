using System.Reflection;
using UnityEngine;

public class SpatialAnchorRedirectionToggle : MonoBehaviour
{
    public enum RedirectionMode
    {
        Original,
        SpatialAnchor
    }

    [Header("Spatial Anchor Components")]
    public ManualSpatialAnchorPlacer placer;
    public SpatialAnchorToDeskOriginBinder deskBinder;
    public SpatialAnchorPlacementCommandReceiver commandReceiver;

    [Header("Mode Specific Components")]
    [Tooltip("Components that should run only in the old/original setup, e.g. TrackerToCubeOffsetCalibrator3.")]
    public MonoBehaviour[] originalModeBehaviours;

    [Tooltip("Components that should run only in the Spatial Anchor setup. Leave GoGo hand redirection out unless you truly want to disable it.")]
    public MonoBehaviour[] spatialAnchorModeBehaviours;

    [Tooltip("The actual hand redirection components, e.g. GoGoInteractionController_NoY3. These stay enabled in Original mode, but wait for an anchor in Spatial Anchor mode.")]
    public MonoBehaviour[] handRedirectionBehaviours;

    [Header("State")]
    public RedirectionMode startMode = RedirectionMode.Original;
    public bool clearAnchorWhenReturningToOriginal = false;
    public bool disableHandRedirectionUntilAnchorExists = true;
    public bool returnToOriginalWhenPlacementCanceledWithoutAnchor = true;
    public bool autoRefreshGoGoHandRedirectionBehaviours = true;
    [Tooltip("Hand-local Scaniverse passthrough overlay is an always-on visual layer, not part of anchor readiness gating.")]
    public bool leaveHandLocalScaniverseOcclusionEnabled = true;
    [Tooltip("When present, this controls whether DiminishedReality overlay or the scaled Scaniverse room is active.")]
    public PassthroughScaniverseModeController passthroughScaniverseModeController;

    public RedirectionMode CurrentMode { get; private set; }
    public bool IsSpatialAnchorMode => CurrentMode == RedirectionMode.SpatialAnchor;

    private bool handRedirectionCurrentlyEnabled = true;
    private TrackerToCubeOffsetCalibrator3[] trackerCalibrators = new TrackerToCubeOffsetCalibrator3[0];

    private void Awake()
    {
        AutoAssignMissingReferences();
    }

    private void OnEnable()
    {
        AutoAssignMissingReferences();
        SubscribePlacerEvents();
    }

    private void OnDisable()
    {
        UnsubscribePlacerEvents();
        UnsubscribeDeskBinderEvents();
    }

    private void Start()
    {
        SetMode(startMode);
    }

    public void SetMode(RedirectionMode mode)
    {
        if (passthroughScaniverseModeController == null)
            passthroughScaniverseModeController = FindAnyObjectByType<PassthroughScaniverseModeController>();

        CurrentMode = mode;
        if (mode == RedirectionMode.SpatialAnchor && placer != null && placer.IsPlacementMode && deskBinder != null)
            deskBinder.BeginPlacementPreviewAlignment();

        bool spatialMode = mode == RedirectionMode.SpatialAnchor;
        bool placementActive = placer != null && (placer.IsPlacementMode || placer.IsCreatingAnchor);
        bool anchorReady = HasUsableSpatialAnchor();
        bool suppressRedirectionForPlacement = spatialMode && placementActive;
        bool enableSpatialAnchorDrivenRedirection = spatialMode && anchorReady && !suppressRedirectionForPlacement;
        bool hasPlacedAnchor = placer != null && placer.HasAnchor && !placer.IsPlacementMode && !placer.IsCreatingAnchor;
        bool enableHandRedirection = !suppressRedirectionForPlacement &&
                                     (!spatialMode || !disableHandRedirectionUntilAnchorExists || anchorReady || hasPlacedAnchor);
        bool enableDeskBinder = spatialMode && (anchorReady || (deskBinder != null && deskBinder.IsAdjustingAlignment));

        // Keep the command receiver alive so the PC window can always switch modes back.
        SetBehaviourEnabled(commandReceiver, true);

        SetBehaviourEnabled(placer, spatialMode);
        SetBehaviourEnabled(deskBinder, enableDeskBinder);
        SetBehaviourListEnabled(spatialAnchorModeBehaviours, enableSpatialAnchorDrivenRedirection);
        if (passthroughScaniverseModeController != null)
        {
            passthroughScaniverseModeController.ApplyCurrentMode();
        }
        else if (!leaveHandLocalScaniverseOcclusionEnabled)
        {
            SetHandLocalScaniverseOcclusionEnabled(spatialMode && (enableSpatialAnchorDrivenRedirection || (deskBinder != null && deskBinder.IsAdjustingAlignment)));
        }
        else
        {
            SetHandLocalScaniverseOcclusionEnabled(true);
        }
        SetOriginalModeBehavioursForMode(spatialMode);
        SetBehaviourListEnabled(handRedirectionBehaviours, enableHandRedirection);
        handRedirectionCurrentlyEnabled = enableHandRedirection;

        if (!enableHandRedirection)
            ResetHandRedirectorsToOriginalHands();

        if (enableSpatialAnchorDrivenRedirection && deskBinder != null)
            deskBinder.ApplyNow();

        if (!spatialMode && clearAnchorWhenReturningToOriginal && placer != null)
            placer.ClearAnchor();
    }

    public void UseOriginalMode()
    {
        SetMode(RedirectionMode.Original);
    }

    public void UseSpatialAnchorMode()
    {
        SetMode(RedirectionMode.SpatialAnchor);
    }

    public void ToggleMode()
    {
        SetMode(IsSpatialAnchorMode ? RedirectionMode.Original : RedirectionMode.SpatialAnchor);
    }

    private void LateUpdate()
    {
        if (!handRedirectionCurrentlyEnabled)
            ResetHandRedirectorsToOriginalHands();
    }

    private void AutoAssignMissingReferences()
    {
        if (placer == null)
            placer = FindAnyObjectByType<ManualSpatialAnchorPlacer>();
        if (deskBinder == null)
            deskBinder = FindAnyObjectByType<SpatialAnchorToDeskOriginBinder>();
        if (commandReceiver == null)
            commandReceiver = FindAnyObjectByType<SpatialAnchorPlacementCommandReceiver>();
        if (passthroughScaniverseModeController == null)
            passthroughScaniverseModeController = FindAnyObjectByType<PassthroughScaniverseModeController>();
        if (autoRefreshGoGoHandRedirectionBehaviours || handRedirectionBehaviours == null || handRedirectionBehaviours.Length == 0)
        {
            GoGoInteractionController_NoY3[] goGoControllers = FindObjectsByType<GoGoInteractionController_NoY3>(FindObjectsSortMode.None);
            if (goGoControllers != null && goGoControllers.Length > 0)
                handRedirectionBehaviours = goGoControllers;
        }

        trackerCalibrators = FindObjectsByType<TrackerToCubeOffsetCalibrator3>(FindObjectsSortMode.None);
        SubscribeDeskBinderEvents();
    }

    private void SubscribePlacerEvents()
    {
        if (placer == null)
            return;

        placer.PlacementStarted += OnPlacementStarted;
        placer.PlacementCanceled += OnPlacementCanceled;
        placer.CandidatePoseConfirmRequested += OnCandidatePoseConfirmRequested;
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
        placer.CandidatePoseConfirmRequested -= OnCandidatePoseConfirmRequested;
        placer.AnchorTransformCreated -= OnAnchorTransformCreated;
        placer.AnchorCleared -= OnAnchorCleared;
        placer.AnchorCreateFailed -= OnAnchorCreateFailed;
    }

    private void SubscribeDeskBinderEvents()
    {
        if (deskBinder == null)
            return;

        deskBinder.AlignmentStarted -= OnDeskAlignmentChanged;
        deskBinder.AlignmentChanged -= OnDeskAlignmentChanged;
        deskBinder.AlignmentConfirmed -= OnDeskAlignmentConfirmed;
        deskBinder.AlignmentCleared -= OnDeskAlignmentChanged;

        deskBinder.AlignmentStarted += OnDeskAlignmentChanged;
        deskBinder.AlignmentChanged += OnDeskAlignmentChanged;
        deskBinder.AlignmentConfirmed += OnDeskAlignmentConfirmed;
        deskBinder.AlignmentCleared += OnDeskAlignmentChanged;
    }

    private void UnsubscribeDeskBinderEvents()
    {
        if (deskBinder == null)
            return;

        deskBinder.AlignmentStarted -= OnDeskAlignmentChanged;
        deskBinder.AlignmentChanged -= OnDeskAlignmentChanged;
        deskBinder.AlignmentConfirmed -= OnDeskAlignmentConfirmed;
        deskBinder.AlignmentCleared -= OnDeskAlignmentChanged;
    }

    private void OnPlacementStarted()
    {
        if (deskBinder != null)
            deskBinder.BeginPlacementPreviewAlignment();

        if (IsSpatialAnchorMode)
            SetMode(RedirectionMode.SpatialAnchor);
    }

    private void OnPlacementCanceled()
    {
        if (deskBinder != null)
            deskBinder.CancelPlacementPreviewAlignment();

        if (IsSpatialAnchorMode && returnToOriginalWhenPlacementCanceledWithoutAnchor && !HasUsableSpatialAnchor())
        {
            UseOriginalMode();
            return;
        }

        SetMode(CurrentMode);
    }

    private void OnCandidatePoseConfirmRequested()
    {
        if (deskBinder != null && deskBinder.IsAdjustingAlignment)
        {
            deskBinder.ConfirmManualRotationAlignment();
            return;
        }

        if (placer != null && placer.IsPlacementMode)
            placer.ConfirmPlacement();
    }

    private void OnAnchorTransformCreated(Transform anchorTransform)
    {
        if (deskBinder != null)
        {
            if (placer != null && placer.LastAnchorWasLoadedSavedAnchor)
            {
                deskBinder.LoadSavedOffsetFromPrefs();
                deskBinder.ApplySavedOffsetAsConfirmed();
            }
            else
            {
                if (deskBinder.IsAlignmentConfirmed)
                {
                    deskBinder.CaptureCurrentDeskAsOffset();
                    deskBinder.SaveCurrentOffsetToPrefs();
                    deskBinder.ApplySavedOffsetAsConfirmed();
                }
                else
                {
                    deskBinder.BeginManualRotationAlignment();
                }
            }
        }

        if (placer != null && deskBinder != null && deskBinder.requireManualRotationConfirmation && !placer.LastAnchorWasLoadedSavedAnchor)
            placer.SetStatusMessage("Anchor position applied to deskOrigin\nLeft pinch rotates desk in 3D\nRight pinch confirms rotation");

        if (IsSpatialAnchorMode)
            SetMode(RedirectionMode.SpatialAnchor);
    }

    private void OnAnchorCreateFailed(string reason)
    {
        if (IsSpatialAnchorMode)
            SetMode(RedirectionMode.SpatialAnchor);
    }

    private void OnAnchorCleared()
    {
        if (deskBinder != null)
            deskBinder.ClearAlignmentState();

        if (IsSpatialAnchorMode)
            SetMode(RedirectionMode.SpatialAnchor);
    }

    private void OnDeskAlignmentChanged()
    {
        if (placer != null && deskBinder != null && deskBinder.IsAdjustingAlignment)
            placer.SetStatusMessage($"Left pinch rotates desk in 3D\nRight pinch confirms\nYaw offset: {deskBinder.CurrentYawAdjustmentDegrees:0.###} deg");

        if (IsSpatialAnchorMode)
            SetMode(RedirectionMode.SpatialAnchor);
    }

    private void OnDeskAlignmentConfirmed()
    {
        if (placer != null)
            placer.ClearStatusMessage();

        if (IsSpatialAnchorMode)
            SetMode(RedirectionMode.SpatialAnchor);
    }

    private bool HasUsableSpatialAnchor()
    {
        if (!disableHandRedirectionUntilAnchorExists)
            return true;
        if (placer == null || !placer.HasAnchor)
            return false;
        if (placer.IsPlacementMode || placer.IsCreatingAnchor)
            return false;
        if (deskBinder != null && deskBinder.requireManualRotationConfirmation && !deskBinder.IsAlignmentConfirmed)
            return false;

        return true;
    }

    private void ResetHandRedirectorsToOriginalHands()
    {
        if (handRedirectionBehaviours == null)
            return;

        for (int i = 0; i < handRedirectionBehaviours.Length; i++)
        {
            MonoBehaviour behaviour = handRedirectionBehaviours[i];
            if (behaviour == null)
                continue;

            ResetBehaviourRedirectorsToOriginalHands(behaviour);
        }
    }

    private static void ResetBehaviourRedirectorsToOriginalHands(MonoBehaviour behaviour)
    {
        if (behaviour is GoGoInteractionController_NoY3 goGo)
        {
            goGo.ResetRedirectorsToOriginalHands();
            return;
        }

        CopyTransformPairFromFields(behaviour, "leftHandOriginal", "leftHandRedirector");
        CopyTransformPairFromFields(behaviour, "rightHandOriginal", "rightHandRedirector");
    }

    private static void CopyTransformPairFromFields(MonoBehaviour behaviour, string originalFieldName, string redirectorFieldName)
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        FieldInfo originalField = behaviour.GetType().GetField(originalFieldName, flags);
        FieldInfo redirectorField = behaviour.GetType().GetField(redirectorFieldName, flags);
        if (originalField == null || redirectorField == null)
            return;

        Transform original = originalField.GetValue(behaviour) as Transform;
        Transform redirector = redirectorField.GetValue(behaviour) as Transform;
        if (original == null || redirector == null)
            return;

        redirector.SetPositionAndRotation(original.position, original.rotation);
    }

    private static void SetBehaviourListEnabled(MonoBehaviour[] behaviours, bool enabled)
    {
        if (behaviours == null)
            return;

        for (int i = 0; i < behaviours.Length; i++)
            SetBehaviourEnabled(behaviours[i], enabled);
    }

    private void SetOriginalModeBehavioursForMode(bool spatialMode)
    {
        ConfigureTrackerCalibratorsForMode(spatialMode);

        if (originalModeBehaviours == null)
            return;

        for (int i = 0; i < originalModeBehaviours.Length; i++)
        {
            MonoBehaviour behaviour = originalModeBehaviours[i];
            if (behaviour == null)
                continue;

            if (behaviour is TrackerToCubeOffsetCalibrator3 trackerCalibrator)
            {
                ConfigureTrackerCalibratorForMode(trackerCalibrator, spatialMode);
                continue;
            }

            SetBehaviourEnabled(behaviour, !spatialMode);
        }
    }

    private void ConfigureTrackerCalibratorsForMode(bool spatialMode)
    {
        if (trackerCalibrators == null || trackerCalibrators.Length == 0)
            trackerCalibrators = FindObjectsByType<TrackerToCubeOffsetCalibrator3>(FindObjectsSortMode.None);

        if (trackerCalibrators == null)
            return;

        for (int i = 0; i < trackerCalibrators.Length; i++)
            ConfigureTrackerCalibratorForMode(trackerCalibrators[i], spatialMode);
    }

    private static void ConfigureTrackerCalibratorForMode(TrackerToCubeOffsetCalibrator3 trackerCalibrator, bool spatialMode)
    {
        if (trackerCalibrator == null)
            return;

        trackerCalibrator.updateDeskTransformFromPackets = !spatialMode;
        trackerCalibrator.objectPoseSpace = TrackerToCubeOffsetCalibrator3.ObjectPoseSpace.DeskTracker;
        SetBehaviourEnabled(trackerCalibrator, true);
    }

    private static void SetBehaviourEnabled(MonoBehaviour behaviour, bool enabled)
    {
        if (behaviour != null)
            behaviour.enabled = enabled;
    }

    private static void SetHandLocalScaniverseOcclusionEnabled(bool enabled)
    {
        HandLocalScaniverseOcclusion[] occlusions = FindObjectsByType<HandLocalScaniverseOcclusion>(FindObjectsSortMode.None);
        if (occlusions == null)
            return;

        for (int i = 0; i < occlusions.Length; i++)
        {
            if (occlusions[i] != null)
                occlusions[i].enabled = enabled;
        }
    }

}
