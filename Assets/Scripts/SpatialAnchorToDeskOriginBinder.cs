using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

public class SpatialAnchorToDeskOriginBinder : MonoBehaviour
{
    [Header("Source")]
    public ManualSpatialAnchorPlacer anchorPlacer;

    [Header("Targets")]
    [Tooltip("Assign the same Transform used by GoGoInteractionController_NoY3.deskOrigin.")]
    public Transform deskOrigin;

    [Tooltip("Optional: assign TrackerToCubeOffsetCalibrator3.deskTransform if that component is still enabled.")]
    public Transform trackerDeskTransform;

    [Header("Redirection Origin")]
    [Tooltip("Optional separate origin used by GoGo hand redirection after desk alignment is confirmed.")]
    public Transform redirectionOrigin;
    public bool createRedirectionOriginIfMissing = true;
    public bool syncRedirectionOriginWithDeskOnAlignment = true;
    public bool enableRightPinchRedirectionOriginAfterConfirmation = true;
    public bool setRedirectionOriginOnlyOncePerAlignment = true;
    [Tooltip("Keep the redirection origin on the confirmed desk plane, using the right pinch X/Z in desk space.")]
    public bool keepRedirectionOriginOnDeskPlane = true;
    [Tooltip("While redirection-origin placement is armed, move the origin every frame to the right-hand position projected onto the DeskOrigin plane.")]
    public bool followRedirectionOriginOnDeskPlaneWhileArmed = true;
    public bool persistRedirectionOriginInPlayerPrefs = true;
    [Tooltip("Automatically restore a saved redirection origin when desk alignment is loaded. Keep false to preserve the old DeskOrigin-based hand pose on startup.")]
    public bool applySavedRedirectionOriginOnAlignment = false;
    public string savedRedirectionOriginPlayerPrefsKey = "HandRedirection.RedirectionOriginOffset";
    public float maxSavedRedirectionOriginDistanceFromDeskMeters = 2.5f;
    public GoGoInteractionController_NoY3[] redirectionControllers;

    [Header("Redirection Origin Visual")]
    public bool showRedirectionOriginVisual = true;
    public GameObject redirectionOriginMarkerPrefab;
    public float redirectionOriginMarkerScale = 0.035f;
    public float redirectionOriginMarkerArmedScaleMultiplier = 1.15f;
    [Tooltip("Legacy option for showing the marker at the hand. Desk-plane placement always shows the projected position.")]
    public bool previewRedirectionOriginMarkerAtHand = false;
    public Vector3 redirectionOriginMarkerHandOffsetWorld = new Vector3(0f, 0.04f, 0f);
    public float redirectionOriginMarkerTowardViewerMeters = 0.08f;
    public Transform redirectionOriginMarkerViewer;
    public bool enableLeftHandRedirectionOriginRotation = true;
    public Color redirectionOriginMarkerColor = new Color(0f, 0.9f, 1f, 0.35f);

    [Header("Runtime Origin Coordinate Axes")]
    [Tooltip("Show XYZ axes at DeskOrigin and RedirectOrigin while running.")]
    public bool showOriginCoordinateAxes = true;
    public float originCoordinateAxisLength = 0.12f;
    public float originCoordinateAxisLineWidth = 0.006f;
    public bool originCoordinateAxisShowLabels = true;

    [Header("Offset From Anchor To Desk Origin")]
    public Vector3 localPositionOffset = Vector3.zero;
    public Vector3 localEulerOffset = Vector3.zero;
    public bool persistOffsetInPlayerPrefs = true;
    public string savedOffsetPlayerPrefsKey = "HandRedirection.DeskAnchorOffset";

    [Header("Manual Rotation Alignment")]
    public bool requireManualRotationConfirmation = true;
    [Tooltip("If false, anchor confirmation only translates deskOrigin first and keeps the current desk rotation as the starting angle.")]
    public bool useAnchorRotationAsInitialRotation = false;
    public bool yawOnlyRotationAdjustment = true;
    public float yawAdjustStepDegrees = 1f;
    public float yawAdjustLargeStepDegrees = 5f;

    [Header("Hand Rotation Alignment")]
    public bool enableHandRotationAlignment = true;
    public OVRHand leftRotationHand;
    public OVRHand rightConfirmHand;
    public PinchProvider leftRotationPinchProvider;
    public PinchProvider rightConfirmPinchProvider;
    public OVRHand.HandFinger rotationFinger = OVRHand.HandFinger.Index;
    public OVRHand.HandFinger fineRotationFinger = OVRHand.HandFinger.Middle;
    public OVRHand.HandFinger confirmFinger = OVRHand.HandFinger.Index;
    [Range(0f, 1f)] public float pinchStartThreshold = 0.7f;
    [Range(0f, 1f)] public float pinchReleaseThreshold = 0.35f;
    [Range(0.01f, 1f)] public float fineRotationScale = 0.1f;
    public bool autoFindAlignmentHands = true;
    public bool applyFullLeftHandRotation = true;
    public bool useLeftWristRotation = true;
    public bool invertLeftHandYaw = false;
    public bool requireRightPinchReleaseBeforeConfirm = true;

    [Header("Behaviour")]
    public bool followEveryFrame = true;
    [Tooltip("Keep applying anchor pose after desk alignment is confirmed. Usually false avoids head-motion drift from live anchor updates.")]
    public bool followConfirmedAnchorEveryFrame = false;
    [Tooltip("When the saved persistent anchor is refreshed after HMD remount, reapply the saved Anchor -> DeskOrigin offset once.")]
    public bool reapplySavedOffsetOnAnchorRefresh = true;
    [Tooltip("On HMD remount, keep DeskOrigin fixed in Unity world and apply the anchor-derived delta to the XR rig/root instead.")]
    public bool correctHmdRemountByKeepingDeskOriginFixed = false;
    public Transform hmdRemountCorrectionRoot;
    [Tooltip("After desk alignment is confirmed, keep using the anchor pose captured at confirmation/load time instead of following live Spatial Anchor jitter.")]
    public bool useLatchedAnchorPoseAfterConfirmation = true;
    [Tooltip("After alignment is confirmed, periodically verify DeskOrigin still matches the saved Anchor -> DeskOrigin offset. This catches HMD remounts when OVR HMDMounted is not delivered.")]
    public bool correctConfirmedDeskDrift = true;
    public float confirmedDeskDriftCheckIntervalSec = 0.5f;
    public float confirmedDeskPositionDriftThresholdMeters = 0.02f;
    public float confirmedDeskRotationDriftThresholdDegrees = 1f;
    public float confirmedAnchorRelatchPositionThresholdMeters = 0.05f;
    public float confirmedAnchorRelatchRotationThresholdDegrees = 3f;
    [Tooltip("Reject live Spatial Anchor samples that jump away from the recent average before deciding whether to relatch DeskOrigin.")]
    public bool rejectAnchorPoseOutliersForRelatch = true;
    public float anchorPoseAverageWindowSeconds = 6f;
    public int anchorPoseAverageMinSamples = 10;
    public float anchorPoseOutlierPositionThresholdMeters = 0.03f;
    public float anchorPoseOutlierRotationThresholdDegrees = 4f;
    public bool applyOnStart = true;

    [Header("Placement Preview")]
    [Tooltip("While anchor placement is active, drive DeskOrigin from the moving candidate anchor pose before the persistent anchor is created.")]
    public bool previewDeskDuringAnchorPlacement = true;
    [Tooltip("Fade the desk visuals during anchor/desk adjustment so it is clear the pose is temporary.")]
    public bool makeDeskTransparentWhileAdjusting = false;
    [Range(0.05f, 1f)] public float adjustingDeskAlpha = 0.35f;
    [Tooltip("Optional root for the desk visuals to fade. If unset, a DeskVisualFollower using deskOrigin is preferred, then deskOrigin itself.")]
    public Transform transparentDeskRoot;
    [Tooltip("Reduce Gaussian Splat opacity while the Spatial Anchor / DeskOrigin pose is being configured.")]
    public bool fadeGaussianSplatsWhileAdjusting = true;
    [Range(0.05f, 1f)] public float adjustingGaussianSplatOpacityMultiplier = 0.35f;

    [Header("Debug")]
    public bool logHandAlignmentDebug = false;
    public bool writeHandAlignmentLogFile = false;
    public float activePinchLogIntervalSec = 1f;
    public bool logAnchorDeskDiagnostics = false;

    [Header("Scene View Visualization")]
    [Tooltip("Show DeskOrigin, RedirectOrigin, and the derived Spatial Anchor pose in the Scene view while not playing.")]
    public bool showReferenceAxesInEditMode = true;
    public bool showDerivedSpatialAnchorInEditMode = true;
    [Min(0.02f)] public float editModeAxisLength = 0.18f;
    [Min(1f)] public float editModeAxisLineWidth = 3f;

    public bool HasAlignmentState { get; private set; }
    public bool IsAlignmentConfirmed { get; private set; }
    public bool IsAdjustingAlignment => HasAlignmentState && !IsAlignmentConfirmed;
    public float CurrentYawAdjustmentDegrees => yawAdjustmentDegrees;

    public event Action AlignmentStarted;
    public event Action AlignmentChanged;
    public event Action AlignmentConfirmed;
    public event Action AlignmentCleared;

    private Quaternion initialAlignmentRotation = Quaternion.identity;
    private float yawAdjustmentDegrees;
    private bool wasLeftRotationPinching;
    private bool wasRightConfirmPinching;
    private bool waitingForRightConfirmRelease;
    private float leftPinchStartYawDegrees;
    private float yawAdjustmentAtLeftPinchStart;
    private Quaternion leftPinchStartRotation = Quaternion.identity;
    private Quaternion handRotationAdjustment = Quaternion.identity;
    private Quaternion handRotationAdjustmentAtLeftPinchStart = Quaternion.identity;
    private Transform leftWristTransform;
    private bool wasLeftFineRotationPinching;
    private bool wasRightRedirectionOriginPinching;
    private bool waitingForRightRedirectionOriginRelease;
    private bool redirectionOriginSetAfterConfirmation;
    private bool redirectionOriginPlacementArmed;
    private bool redirectionOriginRearmRequested;
    private Quaternion redirectionOriginRotationAdjustment = Quaternion.identity;
    private Quaternion redirectionOriginRotationAtLeftPinchStart = Quaternion.identity;
    private Quaternion leftRedirectionOriginPinchStartRotation = Quaternion.identity;
    private float leftRedirectionOriginPinchStartYawDegrees;
    private float redirectionOriginYawAdjustmentDegrees;
    private float redirectionOriginYawAtLeftPinchStart;
    private bool wasLeftRedirectionOriginPinching;
    private bool wasLeftFineRedirectionOriginPinching;
    private GameObject redirectionOriginMarkerInstance;
    private Transform redirectionOriginMarkerCenter;
    private RuntimeCoordinateAxes deskOriginCoordinateAxes;
    private RuntimeCoordinateAxes redirectionOriginCoordinateAxes;
    private float nextActivePinchLogTime;
    private float nextLeftWristFailureLogTime;
    private string lastLeftRotationSource = "none";
    private string handAlignmentLogPath;
    private bool subscribedToAnchorRefresh;
    private float nextConfirmedDeskDriftCheckTime;
    private bool hasLatchedConfirmedAnchorPose;
    private Vector3 latchedAnchorPosition;
    private Quaternion latchedAnchorRotation = Quaternion.identity;
    private readonly Queue<AnchorPoseSample> anchorPoseSamples = new Queue<AnchorPoseSample>();
    private bool usingPlacementCandidateAnchor;
    private readonly List<MaterialAlphaState> transparentMaterialStates = new List<MaterialAlphaState>();
    private readonly List<GaussianSplatOpacityState> gaussianSplatOpacityStates = new List<GaussianSplatOpacityState>();
    private bool deskTransparencyApplied;

    private struct AnchorPoseSample
    {
        public float time;
        public Vector3 position;
        public Quaternion rotation;
    }

    private struct MaterialAlphaState
    {
        public Material material;
        public bool hasColor;
        public Color color;
        public bool hasBaseColor;
        public Color baseColor;
        public bool hasMode;
        public float mode;
        public bool hasSurface;
        public float surface;
        public bool hasSrcBlend;
        public float srcBlend;
        public bool hasDstBlend;
        public float dstBlend;
        public bool hasZWrite;
        public float zWrite;
        public int renderQueue;
    }

    private struct GaussianSplatOpacityState
    {
        public GaussianSplatRenderer renderer;
        public float opacityScale;
    }

    private void Awake()
    {
        if (anchorPlacer == null)
            anchorPlacer = FindAnyObjectByType<ManualSpatialAnchorPlacer>();
        AutoAssignTargets();
        AutoAssignPinchProviders();
        AutoAssignHands();
        AutoAssignLeftWrist();
        LoadSavedOffsetFromPrefs();
        LogAlignmentEvent($"Awake {BuildSourceDebugString()}");
    }

    private void Start()
    {
        SubscribeAnchorRefresh();

        if (applyOnStart)
            ApplyNow();
    }

    private void OnEnable()
    {
        SubscribeAnchorRefresh();
    }

    private void OnDisable()
    {
        UnsubscribeAnchorRefresh();
        SetDeskTransparency(false);
        SetOriginCoordinateAxesActive(false);
    }

    private void LateUpdate()
    {
        if (!subscribedToAnchorRefresh)
            SubscribeAnchorRefresh();

        if (ShouldApplyAnchorPoseThisFrame())
        {
            ApplyNow();
        }
        else
        {
            CheckConfirmedDeskDrift();
        }

        UpdateDeskTransparencyForPlacementState();
        UpdateHandRotationAlignment();
        UpdateRedirectionOriginPlacement();
        UpdateRedirectionOriginVisual();
        UpdateOriginCoordinateAxes();
    }

    public void SetOriginCoordinateAxesVisible(bool visible)
    {
        showOriginCoordinateAxes = visible;
        if (visible)
            UpdateOriginCoordinateAxes();
        else
            SetOriginCoordinateAxesActive(false);
    }

    private void UpdateOriginCoordinateAxes()
    {
        if (!Application.isPlaying || !showOriginCoordinateAxes)
        {
            SetOriginCoordinateAxesActive(false);
            return;
        }

        UpdateOriginCoordinateAxis(ref deskOriginCoordinateAxes, "DeskOriginAxes", deskOrigin);
        UpdateOriginCoordinateAxis(ref redirectionOriginCoordinateAxes, "RedirectOriginAxes", redirectionOrigin);
    }

    private void UpdateOriginCoordinateAxis(ref RuntimeCoordinateAxes axes, string objectName, Transform source)
    {
        if (source == null)
        {
            if (axes != null)
                axes.gameObject.SetActive(false);
            return;
        }

        if (axes == null)
        {
            axes = RuntimeCoordinateAxes.Create(
                objectName,
                transform,
                originCoordinateAxisLength,
                originCoordinateAxisLineWidth,
                originCoordinateAxisShowLabels);
        }

        axes.gameObject.SetActive(true);
        axes.transform.SetPositionAndRotation(source.position, source.rotation);
    }

    private void SetOriginCoordinateAxesActive(bool active)
    {
        if (deskOriginCoordinateAxes != null)
            deskOriginCoordinateAxes.gameObject.SetActive(active && deskOrigin != null);
        if (redirectionOriginCoordinateAxes != null)
            redirectionOriginCoordinateAxes.gameObject.SetActive(active && redirectionOrigin != null);
    }

    [ContextMenu("Anchor Binder/Apply Now")]
    public void ApplyNow()
    {
        AutoAssignTargets();

        if (!TryGetAnchorPoseForDesk(out Vector3 anchorPosition, out Quaternion anchorRotation))
        {
            LogAlignmentEvent("BeginManualRotationAlignment ignored: anchor is null");
            return;
        }

        // During live placement the candidate already represents DeskOrigin's
        // requested world position (directly below the right hand). Do not add
        // a previously saved Anchor -> DeskOrigin offset to that preview.
        bool usingLivePlacementPose = previewDeskDuringAnchorPlacement &&
                                      anchorPlacer != null &&
                                      anchorPlacer.IsPlacementMode;
        Vector3 targetPos = usingLivePlacementPose
            ? anchorPosition
            : anchorPosition + (anchorRotation * localPositionOffset);
        Quaternion targetRot = ResolveTargetRotation(anchorRotation);
        if (!usingLivePlacementPose &&
            TryGetLatchedAnchorPose(out Vector3 latchedPosition, out Quaternion latchedRotation))
        {
            targetPos = latchedPosition + (latchedRotation * localPositionOffset);
            targetRot = ResolveTargetRotation(latchedRotation);
        }

        ApplyPose(deskOrigin, targetPos, targetRot);
        ApplyPose(trackerDeskTransform, targetPos, targetRot);
        EnsureRedirectionOrigin();
        ApplyRedirectionOriginToControllers();
    }

    [ContextMenu("Anchor Binder/Begin Manual Rotation Alignment")]
    public void BeginManualRotationAlignment()
    {
        AutoAssignTargets();

        if (!TryGetAnchorPoseForDesk(out Vector3 anchorPosition, out Quaternion anchorRotation))
            return;

        initialAlignmentRotation = useAnchorRotationAsInitialRotation
            ? anchorRotation * Quaternion.Euler(localEulerOffset)
            : GetCurrentTargetRotation(anchorRotation * Quaternion.Euler(localEulerOffset));

        yawAdjustmentDegrees = 0f;
        handRotationAdjustment = Quaternion.identity;
        hasLatchedConfirmedAnchorPose = false;
        waitingForRightRedirectionOriginRelease = false;
        wasRightRedirectionOriginPinching = false;
        redirectionOriginSetAfterConfirmation = false;
        redirectionOriginPlacementArmed = false;
        redirectionOriginRearmRequested = false;
        ResetRedirectionOriginRotationAdjustment();
        ClearAnchorPoseSamples();
        HasAlignmentState = true;
        IsAlignmentConfirmed = !requireManualRotationConfirmation;
        wasLeftRotationPinching = IsLeftRotationPinching();
        wasLeftFineRotationPinching = IsLeftFineRotationPinching();
        wasRightConfirmPinching = IsRightConfirmPinching();
        waitingForRightConfirmRelease = requireRightPinchReleaseBeforeConfirm;
        UpdateDeskTransparencyForPlacementState();
        ApplyNow();
        LogAlignmentEvent(
            $"BeginManualRotationAlignment initial={FormatRotation(initialAlignmentRotation)} " +
            $"leftPinch={wasLeftRotationPinching} rightPinch={wasRightConfirmPinching} " +
            $"waitingRightRelease={waitingForRightConfirmRelease} {BuildSourceDebugString()}");

        if (IsAlignmentConfirmed)
            AlignmentConfirmed?.Invoke();
        else
            AlignmentStarted?.Invoke();
    }

    public void AdjustYawLeft()
    {
        AdjustYaw(-yawAdjustStepDegrees);
    }

    public void AdjustYawRight()
    {
        AdjustYaw(yawAdjustStepDegrees);
    }

    public void AdjustYawLeftLarge()
    {
        AdjustYaw(-yawAdjustLargeStepDegrees);
    }

    public void AdjustYawRightLarge()
    {
        AdjustYaw(yawAdjustLargeStepDegrees);
    }

    public void AdjustYaw(float deltaDegrees)
    {
        EnsureAlignmentStarted();
        if (!HasAlignmentState)
            return;

        yawAdjustmentDegrees += deltaDegrees;
        ApplyNow();
        AlignmentChanged?.Invoke();
    }

    [ContextMenu("Anchor Binder/Reset Yaw Adjustment")]
    public void ResetYawAdjustment()
    {
        EnsureAlignmentStarted();
        if (!HasAlignmentState)
            return;

        yawAdjustmentDegrees = 0f;
        ApplyNow();
        AlignmentChanged?.Invoke();
    }

    public void BeginPlacementPreviewAlignment()
    {
        if (!previewDeskDuringAnchorPlacement)
            return;
        if (usingPlacementCandidateAnchor && HasAlignmentState && !IsAlignmentConfirmed)
            return;

        usingPlacementCandidateAnchor = true;
        BeginManualRotationAlignment();
    }

    public void CancelPlacementPreviewAlignment()
    {
        if (!usingPlacementCandidateAnchor && !IsAdjustingAlignment)
            return;

        ClearAlignmentState();
    }

    [ContextMenu("Anchor Binder/Confirm Manual Rotation Alignment")]
    public void ConfirmManualRotationAlignment()
    {
        EnsureAlignmentStarted();
        if (!HasAlignmentState)
            return;

        IsAlignmentConfirmed = true;
        ApplyNow();
        CaptureCurrentDeskAsOffset();
        SaveCurrentOffsetToPrefs();
        if (TryGetAnchorPoseForDesk(out Vector3 confirmedAnchorPosition, out Quaternion confirmedAnchorRotation))
            CaptureLatchedAnchorPose(confirmedAnchorPosition, confirmedAnchorRotation, "ConfirmManualRotationAlignment");
        else
            CaptureLatchedAnchorPose(anchorPlacer != null ? anchorPlacer.CurrentAnchorTransform : null, "ConfirmManualRotationAlignment");
        LogAnchorDeskDiagnostic("ConfirmManualRotationAlignment saved", anchorPlacer != null ? anchorPlacer.CurrentAnchorTransform : null);
        initialAlignmentRotation = GetCurrentTargetRotation(Quaternion.identity);
        yawAdjustmentDegrees = 0f;
        handRotationAdjustment = Quaternion.identity;
        usingPlacementCandidateAnchor = false;
        SetDeskTransparency(false);
        bool appliedSavedRedirectionOrigin = false;
        if (redirectionOriginRearmRequested)
            SyncRedirectionOriginWithDesk();
        else
            appliedSavedRedirectionOrigin = ApplySavedRedirectionOriginOrSyncToDesk();

        bool shouldArmRedirectionOrigin = enableRightPinchRedirectionOriginAfterConfirmation && (!appliedSavedRedirectionOrigin || redirectionOriginRearmRequested);
        waitingForRightRedirectionOriginRelease = shouldArmRedirectionOrigin && IsRightConfirmPinching();
        wasRightRedirectionOriginPinching = IsRightConfirmPinching();
        redirectionOriginSetAfterConfirmation = appliedSavedRedirectionOrigin;
        redirectionOriginPlacementArmed = shouldArmRedirectionOrigin;
        redirectionOriginRearmRequested = false;
        LogAlignmentEvent($"ConfirmManualRotationAlignment finalDesk={FormatTransform(deskOrigin)} savedOffsetPos={localPositionOffset} savedOffsetEuler={localEulerOffset} handAdjustment={FormatRotation(handRotationAdjustment)} yaw={yawAdjustmentDegrees:0.###}");
        AlignmentConfirmed?.Invoke();

        if (anchorPlacer != null && anchorPlacer.IsPlacementMode)
            anchorPlacer.ConfirmPlacement();
    }

    public void ClearAlignmentState()
    {
        HasAlignmentState = false;
        IsAlignmentConfirmed = false;
        yawAdjustmentDegrees = 0f;
        handRotationAdjustment = Quaternion.identity;
        hasLatchedConfirmedAnchorPose = false;
        ClearAnchorPoseSamples();
        wasLeftRotationPinching = false;
        wasLeftFineRotationPinching = false;
        wasRightConfirmPinching = false;
        wasRightRedirectionOriginPinching = false;
        waitingForRightConfirmRelease = false;
        waitingForRightRedirectionOriginRelease = false;
        redirectionOriginSetAfterConfirmation = false;
        redirectionOriginPlacementArmed = false;
        redirectionOriginRearmRequested = false;
        ResetRedirectionOriginRotationAdjustment();
        usingPlacementCandidateAnchor = false;
        SetDeskTransparency(false);
        SetRedirectionOriginVisualVisible(false);
        ApplyRedirectionSuppressionState();
        LogAlignmentEvent("ClearAlignmentState");
        AlignmentCleared?.Invoke();
    }

    [ContextMenu("Anchor Binder/Capture Current Desk As Offset")]
    public void CaptureCurrentDeskAsOffset()
    {
        Transform target = deskOrigin != null ? deskOrigin : trackerDeskTransform;
        if (!TryGetAnchorPoseForDesk(out Vector3 anchorPosition, out Quaternion anchorRotation) || target == null)
            return;

        localPositionOffset = Quaternion.Inverse(anchorRotation) * (target.position - anchorPosition);
        localEulerOffset = (Quaternion.Inverse(anchorRotation) * target.rotation).eulerAngles;
        LogAlignmentEvent($"CaptureCurrentDeskAsOffset anchorPos={anchorPosition} anchorRot={FormatRotation(anchorRotation)} target={FormatTransform(target)} savedOffsetPos={localPositionOffset} savedOffsetEuler={localEulerOffset}");
    }

    public bool LoadSavedOffsetFromPrefs()
    {
        if (!persistOffsetInPlayerPrefs || string.IsNullOrEmpty(savedOffsetPlayerPrefsKey))
            return false;

        string json = PlayerPrefs.GetString(savedOffsetPlayerPrefsKey, "");
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            SavedDeskAnchorOffset saved = JsonUtility.FromJson<SavedDeskAnchorOffset>(json);
            localPositionOffset = saved.localPositionOffset;
            localEulerOffset = saved.localEulerOffset;
            LogAlignmentEvent($"Loaded saved desk offset pos={localPositionOffset} euler={localEulerOffset}");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SpatialAnchorToDeskOriginBinder] Failed to load saved desk offset: {e.Message}");
            return false;
        }
    }

    public void SaveCurrentOffsetToPrefs()
    {
        if (!persistOffsetInPlayerPrefs || string.IsNullOrEmpty(savedOffsetPlayerPrefsKey))
            return;

        SavedDeskAnchorOffset saved = new SavedDeskAnchorOffset
        {
            localPositionOffset = localPositionOffset,
            localEulerOffset = localEulerOffset
        };
        PlayerPrefs.SetString(savedOffsetPlayerPrefsKey, JsonUtility.ToJson(saved));
        PlayerPrefs.Save();
        LogAlignmentEvent($"Saved desk offset pos={localPositionOffset} euler={localEulerOffset}");
    }

    public void ClearSavedOffsetPrefs()
    {
        if (string.IsNullOrEmpty(savedOffsetPlayerPrefsKey))
            return;

        PlayerPrefs.DeleteKey(savedOffsetPlayerPrefsKey);
        PlayerPrefs.Save();
    }

    public void ApplySavedOffsetAsConfirmed()
    {
        ApplySavedOffsetAsConfirmed(true, true);
    }

    private void ApplySavedOffsetAsConfirmed(bool refreshLatchedAnchorPose)
    {
        ApplySavedOffsetAsConfirmed(refreshLatchedAnchorPose, true);
    }

    private void ApplySavedOffsetAsConfirmed(bool refreshLatchedAnchorPose, bool armRedirectionOrigin)
    {
        AutoAssignTargets();

        Transform anchor = anchorPlacer != null ? anchorPlacer.CurrentAnchorTransform : null;
        if (anchor == null)
            return;

        Quaternion anchorRotationForDesk = anchor.rotation;
        if (!refreshLatchedAnchorPose && TryGetLatchedAnchorPose(out _, out Quaternion latchedRotationForDesk))
            anchorRotationForDesk = latchedRotationForDesk;

        initialAlignmentRotation = anchorRotationForDesk * Quaternion.Euler(localEulerOffset);
        yawAdjustmentDegrees = 0f;
        handRotationAdjustment = Quaternion.identity;
        HasAlignmentState = true;
        IsAlignmentConfirmed = true;
        if (refreshLatchedAnchorPose && armRedirectionOrigin)
        {
            redirectionOriginSetAfterConfirmation = false;
            waitingForRightRedirectionOriginRelease = enableRightPinchRedirectionOriginAfterConfirmation;
            wasRightRedirectionOriginPinching = IsRightConfirmPinching();
            redirectionOriginPlacementArmed = enableRightPinchRedirectionOriginAfterConfirmation;
        }

        if (refreshLatchedAnchorPose)
            CaptureLatchedAnchorPose(anchor, "ApplySavedOffsetAsConfirmed");
        LogAlignmentEvent($"ApplySavedOffsetAsConfirmed begin anchor={FormatTransform(anchor)} savedOffsetPos={localPositionOffset} savedOffsetEuler={localEulerOffset}");
        LogAnchorDeskDiagnostic("ApplySavedOffsetAsConfirmed before", anchor);
        ApplyNow();
        bool shouldForceRearmRedirectionOrigin = redirectionOriginRearmRequested;
        if (shouldForceRearmRedirectionOrigin)
            SyncRedirectionOriginWithDesk();
        else if (!redirectionOriginSetAfterConfirmation)
        {
            if (armRedirectionOrigin)
                redirectionOriginSetAfterConfirmation = ApplySavedRedirectionOriginOrSyncToDesk();
            else
                ApplyRedirectionOriginToControllers();
        }
        else
            ApplyRedirectionOriginToControllers();
        if (shouldForceRearmRedirectionOrigin)
            redirectionOriginSetAfterConfirmation = false;
        redirectionOriginPlacementArmed = armRedirectionOrigin && enableRightPinchRedirectionOriginAfterConfirmation && (!redirectionOriginSetAfterConfirmation || shouldForceRearmRedirectionOrigin);
        waitingForRightRedirectionOriginRelease = redirectionOriginPlacementArmed && IsRightConfirmPinching();
        redirectionOriginRearmRequested = false;
        ApplyRedirectionSuppressionState();
        LogAnchorDeskDiagnostic("ApplySavedOffsetAsConfirmed after", anchor);
        LogAlignmentEvent($"ApplySavedOffsetAsConfirmed desk={FormatTransform(deskOrigin)}");
        AlignmentConfirmed?.Invoke();
    }

    [Serializable]
    private struct SavedDeskAnchorOffset
    {
        public Vector3 localPositionOffset;
        public Vector3 localEulerOffset;
    }

    [Serializable]
    private struct SavedRedirectionOriginOffset
    {
        public Vector3 localPosition;
        public Vector3 localEuler;
    }

    private static void ApplyPose(Transform target, Vector3 position, Quaternion rotation)
    {
        if (target == null)
            return;

        target.SetPositionAndRotation(position, rotation);
    }

    private void EnsureAlignmentStarted()
    {
        if (!HasAlignmentState)
        {
            if (previewDeskDuringAnchorPlacement && anchorPlacer != null && anchorPlacer.IsPlacementMode)
                BeginPlacementPreviewAlignment();
            else
                BeginManualRotationAlignment();
        }
    }

    private bool TryGetAnchorPoseForDesk(out Vector3 position, out Quaternion rotation)
    {
        if (previewDeskDuringAnchorPlacement &&
            anchorPlacer != null &&
            (usingPlacementCandidateAnchor || anchorPlacer.IsPlacementMode || (HasAlignmentState && !IsAlignmentConfirmed && anchorPlacer.IsCreatingAnchor && anchorPlacer.CurrentAnchorTransform == null)))
        {
            Pose candidatePose = anchorPlacer.CandidatePose;
            position = candidatePose.position;
            rotation = candidatePose.rotation;
            return true;
        }

        Transform anchor = anchorPlacer != null ? anchorPlacer.CurrentAnchorTransform : null;
        if (anchor != null)
        {
            position = anchor.position;
            rotation = anchor.rotation;
            return true;
        }

        position = Vector3.zero;
        rotation = Quaternion.identity;
        return false;
    }

    private bool ShouldApplyAnchorPoseThisFrame()
    {
        if (!followEveryFrame)
            return false;

        if (!HasAlignmentState)
            return true;

        return !IsAlignmentConfirmed || followConfirmedAnchorEveryFrame;
    }

    private void SubscribeAnchorRefresh()
    {
        if (subscribedToAnchorRefresh)
            return;

        if (anchorPlacer == null)
            anchorPlacer = FindAnyObjectByType<ManualSpatialAnchorPlacer>();
        if (anchorPlacer == null)
            return;

        anchorPlacer.SavedAnchorRefreshed += OnSavedAnchorRefreshed;
        anchorPlacer.CandidatePoseLockStateChanged += OnCandidatePoseLockStateChanged;
        subscribedToAnchorRefresh = true;
    }

    private void UnsubscribeAnchorRefresh()
    {
        if (!subscribedToAnchorRefresh)
            return;

        if (anchorPlacer != null)
        {
            anchorPlacer.SavedAnchorRefreshed -= OnSavedAnchorRefreshed;
            anchorPlacer.CandidatePoseLockStateChanged -= OnCandidatePoseLockStateChanged;
        }
        subscribedToAnchorRefresh = false;
    }

    private void OnCandidatePoseLockStateChanged(bool locked, bool adjusting)
    {
        UpdateDeskTransparencyForPlacementState();
    }

    private void OnSavedAnchorRefreshed(Transform anchor)
    {
        if (!reapplySavedOffsetOnAnchorRefresh)
        {
            LogAlignmentEvent($"SavedAnchorRefreshed ignored reapplySavedOffsetOnAnchorRefresh=false anchor={FormatTransform(anchor)} desk={FormatTransform(deskOrigin)}");
            return;
        }

        AutoAssignTargets();
        LogAlignmentEvent($"SavedAnchorRefreshed before load anchor={FormatTransform(anchor)} desk={FormatTransform(deskOrigin)} savedOffsetPos={localPositionOffset} savedOffsetEuler={localEulerOffset}");
        LoadSavedOffsetFromPrefs();
        if (correctHmdRemountByKeepingDeskOriginFixed && TryApplyHmdRemountCorrection(anchor, "SavedAnchorRefreshed"))
        {
            LogAlignmentEvent($"SavedAnchorRefreshed corrected rig root anchor={FormatTransform(anchor)} desk={FormatTransform(deskOrigin)} savedOffsetPos={localPositionOffset} savedOffsetEuler={localEulerOffset}");
            return;
        }

        ApplySavedOffsetAsConfirmed(true, false);
        LogAlignmentEvent($"SavedAnchorRefreshed after apply anchor={FormatTransform(anchor)} desk={FormatTransform(deskOrigin)} savedOffsetPos={localPositionOffset} savedOffsetEuler={localEulerOffset}");
    }

    private void CheckConfirmedDeskDrift()
    {
        if (!correctConfirmedDeskDrift || !IsAlignmentConfirmed)
            return;

        if (Time.realtimeSinceStartup < nextConfirmedDeskDriftCheckTime)
            return;

        nextConfirmedDeskDriftCheckTime = Time.realtimeSinceStartup + Mathf.Max(0.05f, confirmedDeskDriftCheckIntervalSec);

        AutoAssignTargets();
        Transform anchor = anchorPlacer != null ? anchorPlacer.CurrentAnchorTransform : null;
        Transform target = deskOrigin != null ? deskOrigin : trackerDeskTransform;
        if (anchor == null || target == null)
            return;

        if (TryRelatchConfirmedAnchorAfterLargeMove(anchor))
            return;

        Vector3 anchorPosition = anchor.position;
        Quaternion anchorRotation = anchor.rotation;
        if (TryGetLatchedAnchorPose(out Vector3 latchedPosition, out Quaternion latchedRotation))
        {
            anchorPosition = latchedPosition;
            anchorRotation = latchedRotation;
        }

        Vector3 expectedPosition = anchorPosition + (anchorRotation * localPositionOffset);
        Quaternion expectedRotation = anchorRotation * Quaternion.Euler(localEulerOffset);
        float positionDelta = Vector3.Distance(target.position, expectedPosition);
        float rotationDelta = Quaternion.Angle(target.rotation, expectedRotation);

        if (positionDelta < confirmedDeskPositionDriftThresholdMeters &&
            rotationDelta < confirmedDeskRotationDriftThresholdDegrees)
        {
            return;
        }

        string anchorBasis = TryGetLatchedAnchorPose(out _, out _) ? "latched" : "live";
        LogAlignmentEvent(
            $"ConfirmedDeskDrift detected posDelta={positionDelta:0.###}m rotDelta={rotationDelta:0.###}deg " +
            $"anchorBasis={anchorBasis} anchor={FormatTransform(anchor)} latchedAnchor={FormatLatchedAnchorPose()} deskBefore={FormatTransform(deskOrigin)} " +
            $"savedOffsetPos={localPositionOffset} savedOffsetEuler={localEulerOffset}");
        LogAnchorDeskDiagnostic("ConfirmedDeskDrift before", anchor);
        if (correctHmdRemountByKeepingDeskOriginFixed && TryApplyHmdRemountCorrection(anchor, "ConfirmedDeskDrift"))
        {
            LogAnchorDeskDiagnostic("ConfirmedDeskDrift corrected", anchor);
            return;
        }

        ApplySavedOffsetAsConfirmed(false, false);
        LogAnchorDeskDiagnostic("ConfirmedDeskDrift after", anchor);
    }

    private bool TryApplyHmdRemountCorrection(Transform anchor, string reason)
    {
        Transform desk = deskOrigin != null ? deskOrigin : trackerDeskTransform;
        if (anchor == null || desk == null)
            return false;

        Transform correctionRoot = ResolveHmdRemountCorrectionRoot();
        if (correctionRoot == null)
        {
            LogAlignmentEvent($"HmdRemountCorrection skipped reason={reason}; correction root not found");
            return false;
        }

        Vector3 fixedDeskPosition = desk.position;
        Quaternion fixedDeskRotation = desk.rotation;
        Vector3 shiftedDeskPosition = anchor.position + (anchor.rotation * localPositionOffset);
        Quaternion shiftedDeskRotation = anchor.rotation * Quaternion.Euler(localEulerOffset);

        Quaternion deltaRotation = fixedDeskRotation * Quaternion.Inverse(shiftedDeskRotation);
        Vector3 deltaPosition = fixedDeskPosition - (deltaRotation * shiftedDeskPosition);

        correctionRoot.SetPositionAndRotation(
            deltaRotation * correctionRoot.position + deltaPosition,
            deltaRotation * correctionRoot.rotation);

        ApplyPose(deskOrigin, fixedDeskPosition, fixedDeskRotation);
        ApplyPose(trackerDeskTransform, fixedDeskPosition, fixedDeskRotation);

        initialAlignmentRotation = fixedDeskRotation;
        yawAdjustmentDegrees = 0f;
        handRotationAdjustment = Quaternion.identity;
        HasAlignmentState = true;
        IsAlignmentConfirmed = true;
        if (useLatchedAnchorPoseAfterConfirmation)
            CaptureLatchedAnchorPose(anchor, $"HmdRemountCorrection:{reason}");

        EnsureRedirectionOrigin();
        ApplyRedirectionOriginToControllers();
        ApplyRedirectionSuppressionState();
        AlignmentConfirmed?.Invoke();

        LogAlignmentEvent(
            $"HmdRemountCorrection applied reason={reason} root={GetObjectName(correctionRoot)} " +
            $"fixedDesk=pos({fixedDeskPosition.x:0.###},{fixedDeskPosition.y:0.###},{fixedDeskPosition.z:0.###}) rot={FormatRotation(fixedDeskRotation)} " +
            $"shiftedDesk=pos({shiftedDeskPosition.x:0.###},{shiftedDeskPosition.y:0.###},{shiftedDeskPosition.z:0.###}) rot={FormatRotation(shiftedDeskRotation)} " +
            $"deltaPos=({deltaPosition.x:0.###},{deltaPosition.y:0.###},{deltaPosition.z:0.###}) deltaRot={FormatRotation(deltaRotation)}");
        return true;
    }

    private Transform ResolveHmdRemountCorrectionRoot()
    {
        if (hmdRemountCorrectionRoot != null)
            return hmdRemountCorrectionRoot;

        OVRCameraRig cameraRig = FindAnyObjectByType<OVRCameraRig>();
        if (cameraRig != null)
        {
            hmdRemountCorrectionRoot = cameraRig.transform;
            return hmdRemountCorrectionRoot;
        }

        XROriginFallback(out Transform xrOrigin);
        if (xrOrigin != null)
        {
            hmdRemountCorrectionRoot = xrOrigin;
            return hmdRemountCorrectionRoot;
        }

        Camera mainCamera = Camera.main;
        if (mainCamera != null)
        {
            Transform current = mainCamera.transform;
            while (current.parent != null)
                current = current.parent;
            hmdRemountCorrectionRoot = current;
        }

        return hmdRemountCorrectionRoot;
    }

    private static void XROriginFallback(out Transform xrOrigin)
    {
        xrOrigin = null;
        Type xrOriginType = Type.GetType("Unity.XR.CoreUtils.XROrigin, Unity.XR.CoreUtils");
        if (xrOriginType == null)
            return;

        UnityEngine.Object origin = FindAnyObjectByType(xrOriginType);
        if (origin is Component component)
            xrOrigin = component.transform;
    }

    private bool TryRelatchConfirmedAnchorAfterLargeMove(Transform anchor)
    {
        if (anchor == null || !TryGetLatchedAnchorPose(out Vector3 latchedPosition, out Quaternion latchedRotation))
            return false;

        if (!TryGetFilteredAnchorPoseForRelatch(anchor, out Vector3 filteredPosition, out Quaternion filteredRotation))
            return false;

        float anchorPositionDelta = Vector3.Distance(filteredPosition, latchedPosition);
        float anchorRotationDelta = Quaternion.Angle(filteredRotation, latchedRotation);
        if (anchorPositionDelta < confirmedAnchorRelatchPositionThresholdMeters &&
            anchorRotationDelta < confirmedAnchorRelatchRotationThresholdDegrees)
        {
            return false;
        }

        LogAlignmentEvent(
            $"ConfirmedAnchorRelatch detected posDelta={anchorPositionDelta:0.###}m rotDelta={anchorRotationDelta:0.###}deg " +
            $"old={FormatLatchedAnchorPose()} filteredAnchor=pos({filteredPosition.x:0.###},{filteredPosition.y:0.###},{filteredPosition.z:0.###}) rot={FormatRotation(filteredRotation)} live={FormatTransform(anchor)}");
        CaptureLatchedAnchorPose(filteredPosition, filteredRotation, "ConfirmedAnchorRelatchAverage");
        ApplySavedOffsetAsConfirmed(false, false);
        LogAnchorDeskDiagnostic("ConfirmedAnchorRelatch after", anchor);
        return true;
    }

    private bool TryGetFilteredAnchorPoseForRelatch(Transform anchor, out Vector3 filteredPosition, out Quaternion filteredRotation)
    {
        filteredPosition = anchor.position;
        filteredRotation = anchor.rotation;

        PruneAnchorPoseSamples();
        bool hasAverageBeforeCurrent = TryGetAnchorPoseAverage(out Vector3 averagePosition, out Quaternion averageRotation);
        if (rejectAnchorPoseOutliersForRelatch && hasAverageBeforeCurrent)
        {
            float positionFromAverage = Vector3.Distance(anchor.position, averagePosition);
            float rotationFromAverage = Quaternion.Angle(anchor.rotation, averageRotation);
            if (positionFromAverage > anchorPoseOutlierPositionThresholdMeters ||
                rotationFromAverage > anchorPoseOutlierRotationThresholdDegrees)
            {
                LogAlignmentEvent(
                    $"AnchorPoseOutlier ignored posDelta={positionFromAverage:0.###}m rotDelta={rotationFromAverage:0.###}deg " +
                    $"average=pos({averagePosition.x:0.###},{averagePosition.y:0.###},{averagePosition.z:0.###}) rot={FormatRotation(averageRotation)} live={FormatTransform(anchor)}");
                return false;
            }
        }

        AddAnchorPoseSample(anchor);
        if (!TryGetAnchorPoseAverage(out filteredPosition, out filteredRotation))
            return false;

        return true;
    }

    private void AddAnchorPoseSample(Transform anchor)
    {
        anchorPoseSamples.Enqueue(new AnchorPoseSample
        {
            time = Time.realtimeSinceStartup,
            position = anchor.position,
            rotation = anchor.rotation
        });
        PruneAnchorPoseSamples();
    }

    private void PruneAnchorPoseSamples()
    {
        float minTime = Time.realtimeSinceStartup - Mathf.Max(0.1f, anchorPoseAverageWindowSeconds);
        while (anchorPoseSamples.Count > 0 && anchorPoseSamples.Peek().time < minTime)
            anchorPoseSamples.Dequeue();
    }

    private bool TryGetAnchorPoseAverage(out Vector3 averagePosition, out Quaternion averageRotation)
    {
        averagePosition = Vector3.zero;
        averageRotation = Quaternion.identity;
        if (anchorPoseSamples.Count < Mathf.Max(1, anchorPoseAverageMinSamples))
            return false;

        Vector4 rotationSum = Vector4.zero;
        Quaternion referenceRotation = Quaternion.identity;
        bool hasReferenceRotation = false;
        foreach (AnchorPoseSample sample in anchorPoseSamples)
        {
            averagePosition += sample.position;

            Quaternion rotation = sample.rotation;
            if (!hasReferenceRotation)
            {
                referenceRotation = rotation;
                hasReferenceRotation = true;
            }
            else if (Quaternion.Dot(referenceRotation, rotation) < 0f)
            {
                rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
            }

            rotationSum += new Vector4(rotation.x, rotation.y, rotation.z, rotation.w);
        }

        float count = anchorPoseSamples.Count;
        averagePosition /= count;
        if (rotationSum.sqrMagnitude > 1e-6f)
        {
            rotationSum.Normalize();
            averageRotation = new Quaternion(rotationSum.x, rotationSum.y, rotationSum.z, rotationSum.w);
        }

        return true;
    }

    private void ClearAnchorPoseSamples()
    {
        anchorPoseSamples.Clear();
    }

    private void UpdateHandRotationAlignment()
    {
        if (!enableHandRotationAlignment || !IsAdjustingAlignment)
            return;

        AutoAssignHands();
        AutoAssignPinchProviders();
        AutoAssignLeftWrist();

        bool leftNormalPinching = IsLeftRotationPinching();
        bool leftFinePinching = IsLeftFineRotationPinching();
        bool leftPinching = leftNormalPinching || leftFinePinching;
        bool fineModeChanged = leftFinePinching != wasLeftFineRotationPinching;
        if (leftPinching != wasLeftRotationPinching)
            LogAlignmentEvent($"Left pinch changed {wasLeftRotationPinching} -> {leftPinching} {BuildPinchDebugString()}");
        if (fineModeChanged)
            LogAlignmentEvent($"Left fine pinch changed {wasLeftFineRotationPinching} -> {leftFinePinching} scale={GetLeftRotationScale():0.###} {BuildPinchDebugString()}");

        if (leftPinching && (!wasLeftRotationPinching || fineModeChanged))
        {
            leftPinchStartYawDegrees = GetHandYawDegrees(leftRotationHand);
            yawAdjustmentAtLeftPinchStart = yawAdjustmentDegrees;
            leftPinchStartRotation = GetLeftRotationAlignmentRotation();
            handRotationAdjustmentAtLeftPinchStart = handRotationAdjustment;
            LogAlignmentEvent(
                $"Left pinch start source={lastLeftRotationSource} wrist={FormatTransform(leftWristTransform)} " +
                $"scale={GetLeftRotationScale():0.###} " +
                $"startRot={FormatRotation(leftPinchStartRotation)} carryAdjustment={FormatRotation(handRotationAdjustmentAtLeftPinchStart)}");
        }
        else if (leftPinching)
        {
            if (applyFullLeftHandRotation)
            {
                Quaternion deltaRotation = GetLeftRotationAlignmentRotation() * Quaternion.Inverse(leftPinchStartRotation);
                Quaternion scaledDeltaRotation = ScaleRotation(deltaRotation, GetLeftRotationScale());
                handRotationAdjustment = scaledDeltaRotation * handRotationAdjustmentAtLeftPinchStart;
                LogActivePinchFrame(deltaRotation, scaledDeltaRotation);
            }
            else
            {
                float currentYaw = GetHandYawDegrees(leftRotationHand);
                float deltaYaw = Mathf.DeltaAngle(leftPinchStartYawDegrees, currentYaw);
                if (invertLeftHandYaw)
                    deltaYaw = -deltaYaw;

                yawAdjustmentDegrees = yawAdjustmentAtLeftPinchStart + deltaYaw * GetLeftRotationScale();
            }

            ApplyNow();
            AlignmentChanged?.Invoke();
        }

        wasLeftRotationPinching = leftPinching;
        wasLeftFineRotationPinching = leftFinePinching;

        if (previewDeskDuringAnchorPlacement && anchorPlacer != null && anchorPlacer.IsPlacementMode)
        {
            wasRightConfirmPinching = false;
            waitingForRightConfirmRelease = false;
            return;
        }

        bool rightPinching = IsRightConfirmPinching();
        if (rightPinching != wasRightConfirmPinching)
            LogAlignmentEvent($"Right pinch changed {wasRightConfirmPinching} -> {rightPinching} {BuildPinchDebugString()} waitingRightRelease={waitingForRightConfirmRelease}");

        if (waitingForRightConfirmRelease)
        {
            if (!rightPinching)
            {
                waitingForRightConfirmRelease = false;
                LogAlignmentEvent("Right confirm release observed; next right pinch will confirm rotation");
            }
        }
        else if (rightPinching && !wasRightConfirmPinching)
        {
            LogAlignmentEvent("Right confirm pinch detected");
            ConfirmManualRotationAlignment();
        }

        wasRightConfirmPinching = rightPinching;
    }

    private void UpdateRedirectionOriginPlacement()
    {
        if (!enableRightPinchRedirectionOriginAfterConfirmation || !IsAlignmentConfirmed || IsAdjustingAlignment)
            return;
        if (setRedirectionOriginOnlyOncePerAlignment && redirectionOriginSetAfterConfirmation)
            return;

        AutoAssignPinchProviders();
        AutoAssignHands();
        EnsureRedirectionOrigin();
        ApplyRedirectionOriginToControllers();
        redirectionOriginPlacementArmed = true;
        ApplyRedirectionSuppressionState();
        UpdateRedirectionOriginRotationAdjustment();

        if (followRedirectionOriginOnDeskPlaneWhileArmed && !redirectionOriginSetAfterConfirmation && redirectionOrigin != null)
        {
            Vector3 projectedHandPosition = ResolveRedirectionOriginPlacementPosition(GetRightHandTrackingWorldPosition());
            redirectionOrigin.SetPositionAndRotation(projectedHandPosition, GetRedirectionOriginRotation());
        }

        bool rightPinching = IsRightConfirmPinching();
        if (rightPinching != wasRightRedirectionOriginPinching)
            LogAlignmentEvent($"Right redirection-origin pinch changed {wasRightRedirectionOriginPinching} -> {rightPinching} waitingRelease={waitingForRightRedirectionOriginRelease} {BuildPinchDebugString()}");

        if (waitingForRightRedirectionOriginRelease)
        {
            if (!rightPinching)
            {
                waitingForRightRedirectionOriginRelease = false;
                LogAlignmentEvent("Right release observed; next right pinch will set redirection origin");
            }
        }
        else if (rightPinching && !wasRightRedirectionOriginPinching)
        {
            SetRedirectionOriginFromRightPinch();
        }

        wasRightRedirectionOriginPinching = rightPinching;
    }

    private void UpdateRedirectionOriginRotationAdjustment()
    {
        if (!enableLeftHandRedirectionOriginRotation)
            return;

        AutoAssignPinchProviders();
        AutoAssignHands();
        AutoAssignLeftWrist();

        bool leftNormalPinching = IsLeftRotationPinching();
        bool leftFinePinching = IsLeftFineRotationPinching();
        bool leftPinching = leftNormalPinching || leftFinePinching;
        bool fineModeChanged = leftFinePinching != wasLeftFineRedirectionOriginPinching;

        if (leftPinching != wasLeftRedirectionOriginPinching)
            LogAlignmentEvent($"Left redirection-origin rotation pinch changed {wasLeftRedirectionOriginPinching} -> {leftPinching} {BuildPinchDebugString()}");
        if (fineModeChanged)
            LogAlignmentEvent($"Left redirection-origin fine pinch changed {wasLeftFineRedirectionOriginPinching} -> {leftFinePinching} scale={GetLeftRotationScale():0.###}");

        if (leftPinching && (!wasLeftRedirectionOriginPinching || fineModeChanged))
        {
            leftRedirectionOriginPinchStartYawDegrees = GetHandYawDegrees(leftRotationHand);
            redirectionOriginYawAtLeftPinchStart = redirectionOriginYawAdjustmentDegrees;
            leftRedirectionOriginPinchStartRotation = GetLeftRotationAlignmentRotation();
            redirectionOriginRotationAtLeftPinchStart = redirectionOriginRotationAdjustment;
            LogAlignmentEvent(
                $"Left redirection-origin rotation start source={lastLeftRotationSource} " +
                $"scale={GetLeftRotationScale():0.###} startRot={FormatRotation(leftRedirectionOriginPinchStartRotation)}");
        }
        else if (leftPinching)
        {
            if (applyFullLeftHandRotation)
            {
                Quaternion deltaRotation = GetLeftRotationAlignmentRotation() * Quaternion.Inverse(leftRedirectionOriginPinchStartRotation);
                Quaternion scaledDeltaRotation = ScaleRotation(deltaRotation, GetLeftRotationScale());
                redirectionOriginRotationAdjustment = scaledDeltaRotation * redirectionOriginRotationAtLeftPinchStart;
                LogActivePinchFrame(deltaRotation, scaledDeltaRotation);
            }
            else
            {
                float currentYaw = GetHandYawDegrees(leftRotationHand);
                float deltaYaw = Mathf.DeltaAngle(leftRedirectionOriginPinchStartYawDegrees, currentYaw);
                if (invertLeftHandYaw)
                    deltaYaw = -deltaYaw;

                redirectionOriginYawAdjustmentDegrees = redirectionOriginYawAtLeftPinchStart + deltaYaw * GetLeftRotationScale();
            }
        }

        wasLeftRedirectionOriginPinching = leftPinching;
        wasLeftFineRedirectionOriginPinching = leftFinePinching;
    }

    [ContextMenu("Anchor Binder/Sync Redirection Origin With Desk")]
    public void SyncRedirectionOriginWithDesk()
    {
        if (!syncRedirectionOriginWithDeskOnAlignment)
        {
            EnsureRedirectionOrigin();
            ApplyRedirectionOriginToControllers();
            return;
        }

        if (!EnsureRedirectionOrigin())
            return;

        Transform source = deskOrigin != null ? deskOrigin : trackerDeskTransform;
        if (source == null)
            return;

        redirectionOrigin.SetPositionAndRotation(source.position, source.rotation);
        ResetRedirectionOriginRotationAdjustment();
        ApplyRedirectionOriginToControllers();
        LogAlignmentEvent($"Redirection origin synced to desk origin={FormatTransform(redirectionOrigin)}");
    }

    private bool ApplySavedRedirectionOriginOrSyncToDesk()
    {
        if (applySavedRedirectionOriginOnAlignment && TryApplySavedRedirectionOrigin())
            return true;

        SyncRedirectionOriginWithDesk();
        SaveCurrentRedirectionOriginToPrefs();
        return false;
    }

    public bool TryApplySavedRedirectionOrigin()
    {
        if (!persistRedirectionOriginInPlayerPrefs || string.IsNullOrEmpty(savedRedirectionOriginPlayerPrefsKey))
            return false;
        if (!EnsureRedirectionOrigin())
            return false;

        Transform basis = deskOrigin != null ? deskOrigin : trackerDeskTransform;
        if (basis == null)
            return false;

        string json = PlayerPrefs.GetString(savedRedirectionOriginPlayerPrefsKey, "");
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            SavedRedirectionOriginOffset saved = JsonUtility.FromJson<SavedRedirectionOriginOffset>(json);
            if (!IsFinite(saved.localPosition) || saved.localPosition.magnitude > Mathf.Max(0.01f, maxSavedRedirectionOriginDistanceFromDeskMeters))
            {
                LogAlignmentEvent($"Ignored saved redirection origin offset localPos={saved.localPosition}; outside allowed range");
                return false;
            }

            Vector3 position = basis.TransformPoint(saved.localPosition);
            Quaternion rotation = basis.rotation * Quaternion.Euler(saved.localEuler);
            redirectionOrigin.SetPositionAndRotation(position, rotation);
            redirectionOriginSetAfterConfirmation = true;
            ApplyRedirectionOriginToControllers();
            LogAlignmentEvent($"Applied saved redirection origin offset localPos={saved.localPosition} localEuler={saved.localEuler} origin={FormatTransform(redirectionOrigin)}");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SpatialAnchorToDeskOriginBinder] Failed to load saved redirection origin offset: {e.Message}");
            return false;
        }
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public void SaveCurrentRedirectionOriginToPrefs()
    {
        if (!persistRedirectionOriginInPlayerPrefs || string.IsNullOrEmpty(savedRedirectionOriginPlayerPrefsKey))
            return;
        if (redirectionOrigin == null)
            return;

        Transform basis = deskOrigin != null ? deskOrigin : trackerDeskTransform;
        if (basis == null)
            return;

        SavedRedirectionOriginOffset saved = new SavedRedirectionOriginOffset
        {
            localPosition = basis.InverseTransformPoint(redirectionOrigin.position),
            localEuler = (Quaternion.Inverse(basis.rotation) * redirectionOrigin.rotation).eulerAngles
        };
        PlayerPrefs.SetString(savedRedirectionOriginPlayerPrefsKey, JsonUtility.ToJson(saved));
        PlayerPrefs.Save();
        LogAlignmentEvent($"Saved redirection origin offset localPos={saved.localPosition} localEuler={saved.localEuler}");
    }

    [ContextMenu("Anchor Binder/Set Redirection Origin From Right Pinch")]
    public void SetRedirectionOriginFromRightPinch()
    {
        if (!EnsureRedirectionOrigin())
            return;

        Vector3 targetPosition = ResolveRedirectionOriginPlacementPosition(GetRightPinchWorldPosition());
        Quaternion targetRotation = GetRedirectionOriginRotation();

        redirectionOrigin.SetPositionAndRotation(targetPosition, targetRotation);
        redirectionOriginSetAfterConfirmation = true;
        redirectionOriginPlacementArmed = false;
        redirectionOriginRearmRequested = false;
        ApplyRedirectionOriginToControllers();
        SaveCurrentRedirectionOriginToPrefs();
        ApplyRedirectionSuppressionState();

        if (anchorPlacer != null)
            anchorPlacer.SetStatusMessage("Redirection origin set from right pinch", 3f);

        LogAlignmentEvent($"Redirection origin set from right pinch origin={FormatTransform(redirectionOrigin)} rightPinch={targetPosition}");
    }

    private Vector3 ResolveRedirectionOriginPlacementPosition(Vector3 sourcePosition)
    {
        if (keepRedirectionOriginOnDeskPlane && deskOrigin != null)
        {
            Vector3 deskLocal = deskOrigin.InverseTransformPoint(sourcePosition);
            deskLocal.y = 0f;
            return deskOrigin.TransformPoint(deskLocal);
        }

        return sourcePosition;
    }

    private Vector3 ResolveRedirectionOriginPreviewPosition(Vector3 sourcePosition)
    {
        if (keepRedirectionOriginOnDeskPlane)
            return ResolveRedirectionOriginPlacementPosition(sourcePosition);

        if (!previewRedirectionOriginMarkerAtHand)
            return ResolveRedirectionOriginPlacementPosition(sourcePosition);

        Vector3 position = sourcePosition + redirectionOriginMarkerHandOffsetWorld;
        Transform viewer = GetRedirectionOriginMarkerViewer();
        if (viewer != null && redirectionOriginMarkerTowardViewerMeters > 0f)
        {
            Vector3 toViewer = viewer.position - sourcePosition;
            if (toViewer.sqrMagnitude > 1e-6f)
                position += toViewer.normalized * redirectionOriginMarkerTowardViewerMeters;
        }

        return position;
    }

    private Transform GetRedirectionOriginMarkerViewer()
    {
        if (redirectionOriginMarkerViewer != null)
            return redirectionOriginMarkerViewer;

        Camera mainCamera = Camera.main;
        return mainCamera != null ? mainCamera.transform : null;
    }

    [ContextMenu("Anchor Binder/Rearm Right Pinch Redirection Origin")]
    public void RearmRightPinchRedirectionOrigin()
    {
        AutoAssignPinchProviders();
        AutoAssignHands();
        EnsureRedirectionOrigin();
        redirectionOriginSetAfterConfirmation = false;
        redirectionOriginPlacementArmed = true;
        redirectionOriginRearmRequested = !IsAlignmentConfirmed || IsAdjustingAlignment;
        ResetRedirectionOriginRotationAdjustment();
        waitingForRightRedirectionOriginRelease = IsRightConfirmPinching();
        wasRightRedirectionOriginPinching = waitingForRightRedirectionOriginRelease;
        ApplyRedirectionSuppressionState();
        if (anchorPlacer != null)
        {
            anchorPlacer.SetStatusMessage(redirectionOriginRearmRequested
                ? "Redirection origin will be set after desk alignment"
                : "Release, then right pinch to set redirection origin");
        }
        LogAlignmentEvent($"Rearmed right pinch redirection origin waitingRelease={waitingForRightRedirectionOriginRelease} deferred={redirectionOriginRearmRequested}");
    }

    [ContextMenu("Anchor Binder/Reset Redirection Origin To Desk")]
    public void ResetRedirectionOriginToDesk()
    {
        redirectionOriginPlacementArmed = false;
        redirectionOriginSetAfterConfirmation = false;
        redirectionOriginRearmRequested = false;
        waitingForRightRedirectionOriginRelease = false;
        wasRightRedirectionOriginPinching = false;
        ResetRedirectionOriginRotationAdjustment();
        SyncRedirectionOriginWithDesk();
        SaveCurrentRedirectionOriginToPrefs();
        ApplyRedirectionSuppressionState();
        if (anchorPlacer != null)
            anchorPlacer.SetStatusMessage("Redirection origin reset to desk origin");
        LogAlignmentEvent($"Reset redirection origin to desk origin={FormatTransform(redirectionOrigin)}");
    }

    private bool EnsureRedirectionOrigin()
    {
        if (redirectionOrigin == null && createRedirectionOriginIfMissing)
        {
            Transform parent = deskOrigin != null ? deskOrigin : trackerDeskTransform;
            GameObject originObject = new GameObject("RedirectionOrigin");
            redirectionOrigin = originObject.transform;
            if (parent != null)
            {
                redirectionOrigin.SetParent(parent, false);
                redirectionOrigin.SetPositionAndRotation(parent.position, parent.rotation);
            }
            LogAlignmentEvent($"Created redirection origin {FormatTransform(redirectionOrigin)}");
        }

        if (redirectionOrigin == null)
            return false;

        EnsureRedirectionOriginVisual();
        ApplyRedirectionOriginToControllers();
        return true;
    }

    private void UpdateRedirectionOriginVisual()
    {
        if (!showRedirectionOriginVisual)
        {
            SetRedirectionOriginVisualVisible(false);
            return;
        }

        bool canPreviewArmedPlacement = IsAlignmentConfirmed && !IsAdjustingAlignment;
        bool previewArmedPlacement = canPreviewArmedPlacement && redirectionOriginPlacementArmed && !redirectionOriginSetAfterConfirmation;
        if ((!IsAlignmentConfirmed || redirectionOrigin == null) && !previewArmedPlacement)
        {
            SetRedirectionOriginVisualVisible(false);
            return;
        }

        EnsureRedirectionOriginVisual();
        if (redirectionOriginMarkerInstance == null)
            return;

        Vector3 visualPosition = redirectionOrigin != null ? redirectionOrigin.position : Vector3.zero;
        Quaternion visualRotation = redirectionOrigin != null ? redirectionOrigin.rotation : GetRedirectionOriginRotation();
        if (previewArmedPlacement)
        {
            visualPosition = ResolveRedirectionOriginPreviewPosition(GetRightHandTrackingWorldPosition());
            visualRotation = GetRedirectionOriginRotation();
        }

        redirectionOriginMarkerInstance.transform.SetPositionAndRotation(visualPosition, visualRotation);
        float markerScale = redirectionOriginMarkerScale;
        if (previewArmedPlacement)
            markerScale *= Mathf.Max(1f, redirectionOriginMarkerArmedScaleMultiplier);
        markerScale = Mathf.Max(0.001f, markerScale);
        if (redirectionOriginMarkerCenter != null)
        {
            redirectionOriginMarkerInstance.transform.localScale = Vector3.one;
            redirectionOriginMarkerCenter.localScale = Vector3.one * markerScale;
        }
        else
        {
            redirectionOriginMarkerInstance.transform.localScale = Vector3.one * markerScale;
        }
        SetRedirectionOriginVisualVisible(true);
    }

    private void EnsureRedirectionOriginVisual()
    {
        if (!showRedirectionOriginVisual || redirectionOrigin == null || redirectionOriginMarkerInstance != null)
            return;

        redirectionOriginMarkerInstance = redirectionOriginMarkerPrefab != null
            ? Instantiate(redirectionOriginMarkerPrefab)
            : CreateDefaultRedirectionOriginMarker();

        redirectionOriginMarkerInstance.name = "RedirectionOriginMarker";
        redirectionOriginMarkerInstance.transform.SetParent(null, true);
        redirectionOriginMarkerInstance.transform.SetPositionAndRotation(redirectionOrigin.position, redirectionOrigin.rotation);
        redirectionOriginMarkerInstance.transform.localScale = Vector3.one;
        if (redirectionOriginMarkerCenter != null)
            redirectionOriginMarkerCenter.localScale = Vector3.one * Mathf.Max(0.001f, redirectionOriginMarkerScale);
        else
            redirectionOriginMarkerInstance.transform.localScale = Vector3.one * Mathf.Max(0.001f, redirectionOriginMarkerScale);
        SetRedirectionOriginVisualVisible(IsAlignmentConfirmed);
    }

    private GameObject CreateDefaultRedirectionOriginMarker()
    {
        GameObject root = new GameObject("RedirectionOriginMarker");

        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "Center";
        sphere.transform.SetParent(root.transform, false);
        sphere.transform.localScale = Vector3.one * Mathf.Max(0.001f, redirectionOriginMarkerScale);
        redirectionOriginMarkerCenter = sphere.transform;
        DestroyRuntimeCollider(sphere);
        ApplyRedirectionOriginMarkerMaterial(sphere);

        return root;
    }

    private void ApplyRedirectionOriginMarkerMaterial(GameObject target)
    {
        Renderer renderer = target != null ? target.GetComponent<Renderer>() : null;
        if (renderer == null)
            return;

        Shader shader = FindSupportedRuntimeUnlitShader();
        if (shader == null)
            return;

        Color markerColor = redirectionOriginMarkerColor;
        markerColor.a = Mathf.Clamp(markerColor.a, 0.05f, 0.6f);
        Material material = new Material(shader)
        {
            color = markerColor,
            hideFlags = HideFlags.DontSave
        };
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", markerColor);
        if (material.HasProperty("_UnlitColor"))
            material.SetColor("_UnlitColor", markerColor);
        ConfigureTransparentRuntimeMaterial(material);
        renderer.sharedMaterial = material;
    }

    private static Shader FindSupportedRuntimeUnlitShader()
    {
        string pipelineName = GraphicsSettings.currentRenderPipeline != null
            ? GraphicsSettings.currentRenderPipeline.GetType().Name
            : string.Empty;

        string[] candidates = pipelineName.Contains("HDRenderPipeline")
            ? new[] { "HDRP/Unlit", "Unlit/Color", "Sprites/Default" }
            : new[] { "Universal Render Pipeline/Unlit", "Unlit/Color", "Sprites/Default", "Standard" };

        for (int i = 0; i < candidates.Length; i++)
        {
            Shader candidate = Shader.Find(candidates[i]);
            if (candidate != null && candidate.isSupported)
                return candidate;
        }

        return null;
    }

    private static void ConfigureTransparentRuntimeMaterial(Material material)
    {
        if (material == null)
            return;

        if (material.HasProperty("_SurfaceType"))
            material.SetFloat("_SurfaceType", 1f);
        if (material.HasProperty("_Surface"))
            material.SetFloat("_Surface", 1f);
        if (material.HasProperty("_SrcBlend"))
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        if (material.HasProperty("_DstBlend"))
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        if (material.HasProperty("_ZWrite"))
            material.SetFloat("_ZWrite", 0f);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.renderQueue = (int)RenderQueue.Transparent;
    }

    private void DestroyRuntimeCollider(GameObject target)
    {
        Collider collider = target != null ? target.GetComponent<Collider>() : null;
        if (collider == null)
            return;

        if (Application.isPlaying)
            Destroy(collider);
        else
            DestroyImmediate(collider);
    }

    private void SetRedirectionOriginVisualVisible(bool visible)
    {
        if (redirectionOriginMarkerInstance != null && redirectionOriginMarkerInstance.activeSelf != visible)
            redirectionOriginMarkerInstance.SetActive(visible);
    }

    private void ApplyRedirectionOriginToControllers()
    {
        AutoAssignRedirectionControllers();
        if (redirectionControllers == null)
            return;

        for (int i = 0; i < redirectionControllers.Length; i++)
        {
            GoGoInteractionController_NoY3 controller = redirectionControllers[i];
            if (controller == null)
                continue;

            controller.redirectionOrigin = redirectionOrigin;
            controller.useRedirectionOriginWhenAvailable = redirectionOriginSetAfterConfirmation;
        }
    }

    private void ApplyRedirectionSuppressionState()
    {
        AutoAssignRedirectionControllers();
        if (redirectionControllers == null)
            return;

        bool suppress = redirectionOriginPlacementArmed && !redirectionOriginSetAfterConfirmation;
        for (int i = 0; i < redirectionControllers.Length; i++)
        {
            GoGoInteractionController_NoY3 controller = redirectionControllers[i];
            if (controller == null)
                continue;

            controller.suppressRedirection = suppress;
        }
    }

    private void AutoAssignRedirectionControllers()
    {
        if (redirectionControllers != null && redirectionControllers.Length > 0)
            return;

        redirectionControllers = FindObjectsByType<GoGoInteractionController_NoY3>(FindObjectsSortMode.None);
    }

    private Vector3 GetRightPinchWorldPosition()
    {
        if (rightConfirmPinchProvider != null)
            return rightConfirmPinchProvider.PinchPosWorld;

        if (TryGetTrackedHandWorldPosition(rightConfirmHand, out Vector3 trackedHandPosition))
            return trackedHandPosition;

        if (rightConfirmHand != null)
            return rightConfirmHand.transform.position;

        return redirectionOrigin != null ? redirectionOrigin.position : Vector3.zero;
    }

    private Vector3 GetRightHandTrackingWorldPosition()
    {
        if (TryGetTrackedHandWorldPosition(rightConfirmHand, out Vector3 trackedHandPosition))
            return trackedHandPosition;

        if (rightConfirmPinchProvider != null)
            return rightConfirmPinchProvider.PinchPosWorld;

        if (rightConfirmHand != null)
            return rightConfirmHand.transform.position;

        return redirectionOrigin != null ? redirectionOrigin.position : Vector3.zero;
    }

    private static bool TryGetTrackedHandWorldPosition(OVRHand hand, out Vector3 position)
    {
        position = default;
        if (hand == null || !hand.IsTracked)
            return false;

        OVRSkeleton skeleton = hand.GetComponent<OVRSkeleton>();
        if (skeleton != null && skeleton.Bones != null)
        {
            foreach (OVRBone bone in skeleton.Bones)
            {
                if (bone.Id == OVRSkeleton.BoneId.Hand_WristRoot && bone.Transform != null)
                {
                    position = bone.Transform.position;
                    return true;
                }
            }
        }

        if (hand.IsPointerPoseValid)
        {
            Transform pointer = hand.GetPointerRayTransform();
            if (pointer != null)
            {
                position = pointer.position;
                return true;
            }
        }

        return false;
    }

    private Quaternion GetRedirectionOriginRotation()
    {
        Quaternion baseRotation;
        if (deskOrigin != null)
            baseRotation = deskOrigin.rotation;
        else if (trackerDeskTransform != null)
            baseRotation = trackerDeskTransform.rotation;
        else if (redirectionOrigin != null)
            baseRotation = redirectionOrigin.rotation;
        else
            baseRotation = Quaternion.identity;

        Quaternion yawAdjustment = Quaternion.Euler(0f, redirectionOriginYawAdjustmentDegrees, 0f);
        Quaternion adjustedRotation = yawOnlyRotationAdjustment ? yawAdjustment * baseRotation : baseRotation * yawAdjustment;
        return applyFullLeftHandRotation ? redirectionOriginRotationAdjustment * adjustedRotation : adjustedRotation;
    }

    private void ResetRedirectionOriginRotationAdjustment()
    {
        redirectionOriginRotationAdjustment = Quaternion.identity;
        redirectionOriginRotationAtLeftPinchStart = Quaternion.identity;
        leftRedirectionOriginPinchStartRotation = Quaternion.identity;
        leftRedirectionOriginPinchStartYawDegrees = 0f;
        redirectionOriginYawAdjustmentDegrees = 0f;
        redirectionOriginYawAtLeftPinchStart = 0f;
        wasLeftRedirectionOriginPinching = false;
        wasLeftFineRedirectionOriginPinching = false;
    }

    private Quaternion ResolveTargetRotation(Transform anchor)
    {
        Quaternion baseRotation = HasAlignmentState
            ? initialAlignmentRotation
            : anchor.rotation * Quaternion.Euler(localEulerOffset);

        Quaternion yawAdjustment = Quaternion.Euler(0f, yawAdjustmentDegrees, 0f);
        Quaternion adjustedRotation = yawOnlyRotationAdjustment ? yawAdjustment * baseRotation : baseRotation * yawAdjustment;
        return applyFullLeftHandRotation ? handRotationAdjustment * adjustedRotation : adjustedRotation;
    }

    private Quaternion ResolveTargetRotation(Quaternion anchorRotation)
    {
        Quaternion baseRotation = HasAlignmentState
            ? initialAlignmentRotation
            : anchorRotation * Quaternion.Euler(localEulerOffset);

        Quaternion yawAdjustment = Quaternion.Euler(0f, yawAdjustmentDegrees, 0f);
        Quaternion adjustedRotation = yawOnlyRotationAdjustment ? yawAdjustment * baseRotation : baseRotation * yawAdjustment;
        return applyFullLeftHandRotation ? handRotationAdjustment * adjustedRotation : adjustedRotation;
    }

    private void CaptureLatchedAnchorPose(Transform anchor, string reason)
    {
        if (!useLatchedAnchorPoseAfterConfirmation || anchor == null)
            return;

        CaptureLatchedAnchorPose(anchor.position, anchor.rotation, reason);
        LogAlignmentEvent($"LatchedAnchorPose reason={reason} {FormatLatchedAnchorPose()} liveAnchor={FormatTransform(anchor)}");
    }

    private void CaptureLatchedAnchorPose(Vector3 position, Quaternion rotation, string reason)
    {
        if (!useLatchedAnchorPoseAfterConfirmation)
            return;

        latchedAnchorPosition = position;
        latchedAnchorRotation = rotation;
        hasLatchedConfirmedAnchorPose = true;
        ClearAnchorPoseSamples();
        LogAlignmentEvent($"LatchedAnchorPose reason={reason} {FormatLatchedAnchorPose()}");
    }

    private bool TryGetLatchedAnchorPose(out Vector3 position, out Quaternion rotation)
    {
        position = latchedAnchorPosition;
        rotation = latchedAnchorRotation;
        return useLatchedAnchorPoseAfterConfirmation && IsAlignmentConfirmed && hasLatchedConfirmedAnchorPose;
    }

    private void SetDeskTransparency(bool transparent)
    {
        if (!makeDeskTransparentWhileAdjusting && !fadeGaussianSplatsWhileAdjusting)
        {
            if (!transparent)
                RestoreDeskTransparency();
            return;
        }

        if (transparent)
            ApplyDeskTransparency();
        else
            RestoreDeskTransparency();
    }

    private void UpdateDeskTransparencyForPlacementState()
    {
        if (previewDeskDuringAnchorPlacement && anchorPlacer != null && anchorPlacer.IsPlacementMode)
        {
            SetDeskTransparency(true);
            return;
        }

        SetDeskTransparency(IsAdjustingAlignment);
    }

    private void ApplyDeskTransparency()
    {
        if (deskTransparencyApplied)
            return;

        Transform root = ResolveTransparentDeskRoot();
        if (root == null)
            return;

        gaussianSplatOpacityStates.Clear();
        if (fadeGaussianSplatsWhileAdjusting)
        {
            GaussianSplatRenderer[] splatRenderers = root.GetComponentsInChildren<GaussianSplatRenderer>(true);
            float opacityMultiplier = Mathf.Clamp01(adjustingGaussianSplatOpacityMultiplier);
            for (int i = 0; i < splatRenderers.Length; i++)
            {
                GaussianSplatRenderer splatRenderer = splatRenderers[i];
                if (splatRenderer == null)
                    continue;

                gaussianSplatOpacityStates.Add(new GaussianSplatOpacityState
                {
                    renderer = splatRenderer,
                    opacityScale = splatRenderer.m_OpacityScale
                });
                splatRenderer.m_OpacityScale *= opacityMultiplier;
            }
        }

        transparentMaterialStates.Clear();
        if (makeDeskTransparentWhileAdjusting)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            float alpha = Mathf.Clamp01(adjustingDeskAlpha);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || IsScaniverseRenderer(renderer))
                    continue;

                Material[] materials = renderer.sharedMaterials;
                for (int m = 0; m < materials.Length; m++)
                {
                    Material material = materials[m];
                    if (material == null || ContainsMaterialState(material))
                        continue;

                    MaterialAlphaState state = CaptureMaterialState(material);
                    transparentMaterialStates.Add(state);
                    ForceMaterialTransparent(material, alpha);
                }
            }
        }

        deskTransparencyApplied = transparentMaterialStates.Count > 0 || gaussianSplatOpacityStates.Count > 0;
    }

    private void RestoreDeskTransparency()
    {
        if (!deskTransparencyApplied && transparentMaterialStates.Count == 0 && gaussianSplatOpacityStates.Count == 0)
            return;

        for (int i = 0; i < gaussianSplatOpacityStates.Count; i++)
        {
            GaussianSplatOpacityState state = gaussianSplatOpacityStates[i];
            if (state.renderer != null)
                state.renderer.m_OpacityScale = state.opacityScale;
        }

        for (int i = 0; i < transparentMaterialStates.Count; i++)
            RestoreMaterialState(transparentMaterialStates[i]);

        gaussianSplatOpacityStates.Clear();
        transparentMaterialStates.Clear();
        deskTransparencyApplied = false;
    }

    private Transform ResolveTransparentDeskRoot()
    {
        if (transparentDeskRoot != null)
            return transparentDeskRoot;

        DeskVisualFollower[] followers = FindObjectsByType<DeskVisualFollower>(FindObjectsSortMode.None);
        if (followers != null)
        {
            for (int i = 0; i < followers.Length; i++)
            {
                if (followers[i] != null && followers[i].deskOrigin == deskOrigin)
                    return followers[i].transform;
            }
        }

        return deskOrigin;
    }

    private bool ContainsMaterialState(Material material)
    {
        for (int i = 0; i < transparentMaterialStates.Count; i++)
        {
            if (transparentMaterialStates[i].material == material)
                return true;
        }

        return false;
    }

    private static bool IsScaniverseRenderer(Renderer renderer)
    {
        if (renderer == null)
            return true;

        Transform current = renderer.transform;
        while (current != null)
        {
            if (current.name.Contains("Scaniverse"))
                return true;

            current = current.parent;
        }

        return false;
    }

    private static MaterialAlphaState CaptureMaterialState(Material material)
    {
        return new MaterialAlphaState
        {
            material = material,
            hasColor = material.HasProperty("_Color"),
            color = material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white,
            hasBaseColor = material.HasProperty("_BaseColor"),
            baseColor = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : Color.white,
            hasMode = material.HasProperty("_Mode"),
            mode = material.HasProperty("_Mode") ? material.GetFloat("_Mode") : 0f,
            hasSurface = material.HasProperty("_Surface"),
            surface = material.HasProperty("_Surface") ? material.GetFloat("_Surface") : 0f,
            hasSrcBlend = material.HasProperty("_SrcBlend"),
            srcBlend = material.HasProperty("_SrcBlend") ? material.GetFloat("_SrcBlend") : 0f,
            hasDstBlend = material.HasProperty("_DstBlend"),
            dstBlend = material.HasProperty("_DstBlend") ? material.GetFloat("_DstBlend") : 0f,
            hasZWrite = material.HasProperty("_ZWrite"),
            zWrite = material.HasProperty("_ZWrite") ? material.GetFloat("_ZWrite") : 1f,
            renderQueue = material.renderQueue
        };
    }

    private static void ForceMaterialTransparent(Material material, float alpha)
    {
        if (material.HasProperty("_Color"))
        {
            Color color = material.GetColor("_Color");
            color.a = alpha;
            material.SetColor("_Color", color);
        }

        if (material.HasProperty("_BaseColor"))
        {
            Color color = material.GetColor("_BaseColor");
            color.a = alpha;
            material.SetColor("_BaseColor", color);
        }

        if (material.HasProperty("_Mode"))
            material.SetFloat("_Mode", 3f);
        if (material.HasProperty("_Surface"))
            material.SetFloat("_Surface", 1f);
        if (material.HasProperty("_SrcBlend"))
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        if (material.HasProperty("_DstBlend"))
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        if (material.HasProperty("_ZWrite"))
            material.SetFloat("_ZWrite", 0f);

        material.EnableKeyword("_ALPHABLEND_ON");
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.renderQueue = (int)RenderQueue.Transparent;
    }

    private static void RestoreMaterialState(MaterialAlphaState state)
    {
        Material material = state.material;
        if (material == null)
            return;

        if (state.hasColor)
            material.SetColor("_Color", state.color);
        if (state.hasBaseColor)
            material.SetColor("_BaseColor", state.baseColor);
        if (state.hasMode)
            material.SetFloat("_Mode", state.mode);
        if (state.hasSurface)
            material.SetFloat("_Surface", state.surface);
        if (state.hasSrcBlend)
            material.SetFloat("_SrcBlend", state.srcBlend);
        if (state.hasDstBlend)
            material.SetFloat("_DstBlend", state.dstBlend);
        if (state.hasZWrite)
            material.SetFloat("_ZWrite", state.zWrite);

        if ((!state.hasMode || state.mode < 2.5f) && (!state.hasSurface || state.surface < 0.5f))
        {
            material.DisableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }

        material.renderQueue = state.renderQueue;
    }

    private Quaternion GetCurrentTargetRotation(Quaternion fallbackRotation)
    {
        if (deskOrigin != null)
            return deskOrigin.rotation;
        if (trackerDeskTransform != null)
            return trackerDeskTransform.rotation;

        return fallbackRotation;
    }

    private bool IsLeftRotationPinching()
    {
        if (leftRotationPinchProvider != null)
            return leftRotationPinchProvider.IsPinching;

        return IsHandPinching(leftRotationHand, rotationFinger);
    }

    private bool IsLeftFineRotationPinching()
    {
        return IsHandPinching(leftRotationHand, fineRotationFinger);
    }

    private float GetLeftRotationScale()
    {
        return IsLeftFineRotationPinching() ? fineRotationScale : 1f;
    }

    private static Quaternion ScaleRotation(Quaternion rotation, float scale)
    {
        scale = Mathf.Clamp01(scale);
        return Quaternion.SlerpUnclamped(Quaternion.identity, rotation, scale);
    }

    private bool IsRightConfirmPinching()
    {
        if (rightConfirmPinchProvider != null)
            return rightConfirmPinchProvider.IsPinching;

        return IsHandPinching(rightConfirmHand, confirmFinger);
    }

    private bool IsHandPinching(OVRHand hand, OVRHand.HandFinger finger)
    {
        if (hand == null || !hand.IsTracked)
            return false;

        float strength = hand.GetFingerPinchStrength(finger);
        bool wasPinching = hand == leftRotationHand ? wasLeftRotationPinching : wasRightConfirmPinching;
        float threshold = wasPinching ? pinchReleaseThreshold : pinchStartThreshold;
        return hand.GetFingerIsPinching(finger) || strength >= threshold;
    }

    private static float GetHandYawDegrees(OVRHand hand)
    {
        if (hand == null)
            return 0f;

        Vector3 forward = Vector3.ProjectOnPlane(hand.transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-6f)
            forward = Vector3.ProjectOnPlane(hand.transform.up, Vector3.up);
        if (forward.sqrMagnitude < 1e-6f)
            return hand.transform.eulerAngles.y;

        return Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
    }

    private Quaternion GetLeftRotationAlignmentRotation()
    {
        if (useLeftWristRotation)
        {
            AutoAssignLeftWrist();
            if (leftWristTransform != null)
            {
                lastLeftRotationSource = $"wristTransform:{leftWristTransform.name}";
                return leftWristTransform.rotation;
            }

            if (TryGetSkeletonRootRotation(leftRotationHand, out Quaternion skeletonRootRotation))
            {
                lastLeftRotationSource = $"skeletonRoot:{leftRotationHand.name}";
                return skeletonRootRotation;
            }
        }

        lastLeftRotationSource = leftRotationHand != null ? $"handTransform:{leftRotationHand.name}" : "identity";
        return GetHandRotation(leftRotationHand);
    }

    private static bool TryGetSkeletonRootRotation(OVRHand hand, out Quaternion rotation)
    {
        rotation = Quaternion.identity;
        if (hand == null || !hand.IsTracked)
            return false;

        OVRSkeleton.IOVRSkeletonDataProvider skeletonProvider = hand as OVRSkeleton.IOVRSkeletonDataProvider;
        if (skeletonProvider == null)
            return false;

        OVRSkeleton.SkeletonPoseData poseData = skeletonProvider.GetSkeletonPoseData();
        if (!poseData.IsDataValid || poseData.RootScale <= 0f)
            return false;

        rotation = FromFlippedZQuatf(poseData.RootPose.Orientation);
        return true;
    }

    private static Quaternion FromFlippedZQuatf(OVRPlugin.Quatf value)
    {
        return new Quaternion(-value.x, -value.y, value.z, value.w);
    }

    private static Quaternion GetHandRotation(OVRHand hand)
    {
        return hand != null ? hand.transform.rotation : Quaternion.identity;
    }

    private void AutoAssignTargets()
    {
        if (deskOrigin == null)
        {
            GoGoInteractionController_NoY3 goGo = FindAnyObjectByType<GoGoInteractionController_NoY3>();
            if (goGo != null && goGo.deskOrigin != null)
                deskOrigin = goGo.deskOrigin;
        }

        if (deskOrigin == null)
        {
            GameObject deskOriginObject = GameObject.Find("DeskOrigin");
            if (deskOriginObject != null)
                deskOrigin = deskOriginObject.transform;
        }

        if (trackerDeskTransform == null)
        {
            TrackerToCubeOffsetCalibrator3 tracker = FindAnyObjectByType<TrackerToCubeOffsetCalibrator3>();
            if (tracker != null)
                trackerDeskTransform = tracker.deskTransform;
        }
    }

    private void AutoAssignHands()
    {
        AutoAssignHandsFromPinchProviders();

        if (!autoFindAlignmentHands || (leftRotationHand != null && rightConfirmHand != null))
            return;

        OVRHand[] hands = FindObjectsByType<OVRHand>(FindObjectsSortMode.None);
        if (hands == null)
            return;

        for (int i = 0; i < hands.Length; i++)
        {
            OVRHand hand = hands[i];
            if (hand == null)
                continue;

            if (leftRotationHand == null && IsLikelyHandedness(hand, true))
            {
                leftRotationHand = hand;
                LogAlignmentEvent($"Auto-assigned leftRotationHand={hand.name}");
            }
            else if (rightConfirmHand == null && IsLikelyHandedness(hand, false))
            {
                rightConfirmHand = hand;
                LogAlignmentEvent($"Auto-assigned rightConfirmHand={hand.name}");
            }
        }
    }

    private void AutoAssignHandsFromPinchProviders()
    {
        if (leftRotationHand == null && leftRotationPinchProvider != null)
            leftRotationHand = leftRotationPinchProvider.ovrHand;
        if (rightConfirmHand == null && rightConfirmPinchProvider != null)
            rightConfirmHand = rightConfirmPinchProvider.ovrHand;
    }

    private void AutoAssignPinchProviders()
    {
        if (!autoFindAlignmentHands || (leftRotationPinchProvider != null && rightConfirmPinchProvider != null))
            return;

        PinchProvider[] providers = FindObjectsByType<PinchProvider>(FindObjectsSortMode.None);
        if (providers == null)
            return;

        for (int i = 0; i < providers.Length; i++)
        {
            PinchProvider provider = providers[i];
            if (provider == null || provider.ovrHand == null)
                continue;

            if (leftRotationPinchProvider == null && IsLikelyHandedness(provider.ovrHand, true))
            {
                leftRotationPinchProvider = provider;
                LogAlignmentEvent($"Auto-assigned leftRotationPinchProvider={provider.name} ovrHand={provider.ovrHand.name}");
            }
            else if (rightConfirmPinchProvider == null && IsLikelyHandedness(provider.ovrHand, false))
            {
                rightConfirmPinchProvider = provider;
                LogAlignmentEvent($"Auto-assigned rightConfirmPinchProvider={provider.name} ovrHand={provider.ovrHand.name}");
            }
        }
    }

    private static bool IsLikelyHandedness(OVRHand hand, bool left)
    {
        if (hand == null)
            return false;

        string target = left ? "left" : "right";
        string lowerName = hand.name.ToLowerInvariant();
        if (lowerName.Contains(target))
            return true;

        if (TryObjectContainsText(hand, target))
            return true;

        Component[] components = hand.GetComponents<Component>();
        for (int i = 0; i < components.Length; i++)
        {
            if (TryObjectContainsText(components[i], target))
                return true;
        }

        return false;
    }

    private static bool TryObjectContainsText(object source, string target)
    {
        if (source == null)
            return false;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = source.GetType();
        PropertyInfo handTypeProperty = type.GetProperty("HandType", flags);
        if (handTypeProperty != null &&
            handTypeProperty.GetIndexParameters().Length == 0 &&
            ValueContainsText(handTypeProperty.GetValue(source, null), target))
        {
            return true;
        }

        FieldInfo handTypeField = type.GetField("HandType", flags) ??
                                  type.GetField("_handType", flags) ??
                                  type.GetField("_handedness", flags);
        return ValueContainsText(handTypeField?.GetValue(source), target);
    }

    private static bool ValueContainsText(object value, string target)
    {
        return value != null && value.ToString().ToLowerInvariant().Contains(target);
    }

    private void AutoAssignLeftWrist()
    {
        if (!useLeftWristRotation || leftWristTransform != null || leftRotationHand == null)
            return;

        OVRSkeleton skeleton = leftRotationHand.GetComponent<OVRSkeleton>();
        if (skeleton == null || skeleton.Bones == null)
        {
            LogLeftWristFailure($"AutoAssignLeftWrist failed: skeleton/bones missing on {leftRotationHand.name}");
            return;
        }

        foreach (var bone in skeleton.Bones)
        {
            if (bone.Id == OVRSkeleton.BoneId.Hand_WristRoot && bone.Transform != null)
            {
                leftWristTransform = bone.Transform;
                LogAlignmentEvent($"Auto-assigned left wrist transform={leftWristTransform.name} path={GetTransformPath(leftWristTransform)}");
                return;
            }
        }

        LogLeftWristFailure($"AutoAssignLeftWrist failed: Hand_WristRoot not found on {leftRotationHand.name}");
    }

    private void LogLeftWristFailure(string message)
    {
        if (Time.realtimeSinceStartup < nextLeftWristFailureLogTime)
            return;

        nextLeftWristFailureLogTime = Time.realtimeSinceStartup + 1f;
        LogAlignmentEvent(message);
    }

    private void LogActivePinchFrame(Quaternion deltaRotation, Quaternion scaledDeltaRotation)
    {
        if (!logHandAlignmentDebug || Time.realtimeSinceStartup < nextActivePinchLogTime)
            return;

        nextActivePinchLogTime = Time.realtimeSinceStartup + activePinchLogIntervalSec;
        Quaternion currentRotation = GetLeftRotationAlignmentRotation();
        LogAlignmentEvent(
            $"Left pinch active source={lastLeftRotationSource} current={FormatRotation(currentRotation)} " +
            $"scale={GetLeftRotationScale():0.###} delta={FormatRotation(deltaRotation)} scaledDelta={FormatRotation(scaledDeltaRotation)} " +
            $"handAdjustment={FormatRotation(handRotationAdjustment)} " +
            $"desk={FormatTransform(deskOrigin)} trackerDesk={FormatTransform(trackerDeskTransform)}");
    }

    private void LogAlignmentEvent(string message)
    {
        if (!logHandAlignmentDebug)
            return;

        string line = $"[SpatialAnchorHandAlignment t={Time.realtimeSinceStartup:0.000}] {message}";
        Debug.Log(line);

        if (!writeHandAlignmentLogFile)
            return;

        try
        {
            if (string.IsNullOrEmpty(handAlignmentLogPath))
            {
                string logDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs"));
                Directory.CreateDirectory(logDirectory);
                handAlignmentLogPath = Path.Combine(logDirectory, "SpatialAnchorHandAlignment.log");
                File.WriteAllText(handAlignmentLogPath, $"--- Latest Session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}");
            }

            File.WriteAllText(handAlignmentLogPath, line + Environment.NewLine);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SpatialAnchorHandAlignment] Failed to write log file: {e.Message}");
            writeHandAlignmentLogFile = false;
        }
    }

    private void LogAnchorDeskDiagnostic(string label, Transform anchor)
    {
        if (!logAnchorDeskDiagnostics)
            return;

        Vector3 targetPos = Vector3.zero;
        Quaternion targetRot = Quaternion.identity;
        if (anchor != null)
        {
            targetPos = anchor.TransformPoint(localPositionOffset);
            targetRot = ResolveTargetRotation(anchor);
        }
        string anchorBasis = "live";
        if (TryGetLatchedAnchorPose(out Vector3 latchedPosition, out Quaternion latchedRotation))
        {
            targetPos = latchedPosition + (latchedRotation * localPositionOffset);
            targetRot = ResolveTargetRotation(latchedRotation);
            anchorBasis = "latched";
        }

        string actualOffset = "actualOffset=null";
        if (anchor != null && deskOrigin != null)
        {
            Vector3 actualPosOffset = Quaternion.Inverse(anchor.rotation) * (deskOrigin.position - anchor.position);
            Vector3 actualEulerOffset = (Quaternion.Inverse(anchor.rotation) * deskOrigin.rotation).eulerAngles;
            actualOffset = $"actualOffsetPos={actualPosOffset} actualOffsetEuler={actualEulerOffset}";
        }

        LogAlignmentEvent(
            $"{label} anchor={FormatTransform(anchor)} desk={FormatTransform(deskOrigin)} " +
            $"anchorBasis={anchorBasis} {FormatLatchedAnchorPose()} targetPos=({targetPos.x:0.###},{targetPos.y:0.###},{targetPos.z:0.###}) targetRot={FormatRotation(targetRot)} " +
            $"savedOffsetPos={localPositionOffset} savedOffsetEuler={localEulerOffset} {actualOffset} " +
            $"deskChildren={BuildChildSummary(deskOrigin)}");
    }

    private string BuildSourceDebugString()
    {
        return $"leftProvider={GetObjectName(leftRotationPinchProvider)} rightProvider={GetObjectName(rightConfirmPinchProvider)} " +
               $"leftHand={GetObjectName(leftRotationHand)} rightHand={GetObjectName(rightConfirmHand)} " +
               $"leftWrist={GetObjectName(leftWristTransform)} desk={GetObjectName(deskOrigin)} trackerDesk={GetObjectName(trackerDeskTransform)}";
    }

    private string BuildPinchDebugString()
    {
        return $"leftProviderPinch={GetProviderPinchDebug(leftRotationPinchProvider)} rightProviderPinch={GetProviderPinchDebug(rightConfirmPinchProvider)} " +
               $"leftFinePinch={GetHandFingerPinchDebug(leftRotationHand, fineRotationFinger)} " +
               $"leftHandTracked={GetHandTrackedDebug(leftRotationHand)} rightHandTracked={GetHandTrackedDebug(rightConfirmHand)}";
    }

    private static string GetProviderPinchDebug(PinchProvider provider)
    {
        return provider != null ? $"{provider.name}:{provider.IsPinching}/{provider.PinchStrength:0.###}" : "null";
    }

    private static string GetHandTrackedDebug(OVRHand hand)
    {
        return hand != null ? $"{hand.name}:{hand.IsTracked}" : "null";
    }

    private static string GetHandFingerPinchDebug(OVRHand hand, OVRHand.HandFinger finger)
    {
        if (hand == null)
            return "null";

        return $"{hand.name}/{finger}:{hand.GetFingerIsPinching(finger)}/{hand.GetFingerPinchStrength(finger):0.###}";
    }

    private static string GetObjectName(UnityEngine.Object obj)
    {
        return obj != null ? obj.name : "null";
    }

    private static string BuildChildSummary(Transform transform)
    {
        if (transform == null)
            return "null";

        int count = transform.childCount;
        if (count == 0)
            return "0";

        string summary = count.ToString();
        int maxNames = Mathf.Min(count, 5);
        for (int i = 0; i < maxNames; i++)
            summary += i == 0 ? $"[{transform.GetChild(i).name}" : $",{transform.GetChild(i).name}";
        summary += count > maxNames ? ",...]" : "]";
        return summary;
    }

    private static string FormatRotation(Quaternion rotation)
    {
        Vector3 euler = rotation.eulerAngles;
        return $"quat({rotation.x:0.###},{rotation.y:0.###},{rotation.z:0.###},{rotation.w:0.###}) euler({euler.x:0.###},{euler.y:0.###},{euler.z:0.###})";
    }

    private static string FormatTransform(Transform transform)
    {
        if (transform == null)
            return "null";

        Vector3 position = transform.position;
        return $"{transform.name} pos({position.x:0.###},{position.y:0.###},{position.z:0.###}) rot={FormatRotation(transform.rotation)}";
    }

    private string FormatLatchedAnchorPose()
    {
        if (!hasLatchedConfirmedAnchorPose)
            return "latchedAnchor=null";

        return $"latchedAnchor=pos({latchedAnchorPosition.x:0.###},{latchedAnchorPosition.y:0.###},{latchedAnchorPosition.z:0.###}) rot={FormatRotation(latchedAnchorRotation)}";
    }

    private static string GetTransformPath(Transform transform)
    {
        if (transform == null)
            return "null";

        string path = transform.name;
        Transform parent = transform.parent;
        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }

        return path;
    }
}
