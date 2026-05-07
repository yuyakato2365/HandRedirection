using System;
using System.IO;
using System.Reflection;
using UnityEngine;

public class SpatialAnchorToDeskOriginBinder : MonoBehaviour
{
    [Header("Source")]
    public ManualSpatialAnchorPlacer anchorPlacer;

    [Header("Targets")]
    [Tooltip("Assign the same Transform used by GoGoInteractionController_NoY3.deskOrigin.")]
    public Transform deskOrigin;

    [Tooltip("Optional: assign TrackerToCubeOffsetCalibrator3.deskTransform if that component is still enabled.")]
    public Transform trackerDeskTransform;

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
    [Tooltip("After desk alignment is confirmed, keep using the anchor pose captured at confirmation/load time instead of following live Spatial Anchor jitter.")]
    public bool useLatchedAnchorPoseAfterConfirmation = true;
    [Tooltip("After alignment is confirmed, periodically verify DeskOrigin still matches the saved Anchor -> DeskOrigin offset. This catches HMD remounts when OVR HMDMounted is not delivered.")]
    public bool correctConfirmedDeskDrift = true;
    public float confirmedDeskDriftCheckIntervalSec = 0.5f;
    public float confirmedDeskPositionDriftThresholdMeters = 0.02f;
    public float confirmedDeskRotationDriftThresholdDegrees = 1f;
    public float confirmedAnchorRelatchPositionThresholdMeters = 0.05f;
    public float confirmedAnchorRelatchRotationThresholdDegrees = 3f;
    public bool applyOnStart = true;

    [Header("Debug")]
    public bool logHandAlignmentDebug = true;
    public bool writeHandAlignmentLogFile = true;
    public float activePinchLogIntervalSec = 0.25f;
    public bool logAnchorDeskDiagnostics = true;

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
    private float nextActivePinchLogTime;
    private float nextLeftWristFailureLogTime;
    private string lastLeftRotationSource = "none";
    private string handAlignmentLogPath;
    private bool subscribedToAnchorRefresh;
    private float nextConfirmedDeskDriftCheckTime;
    private bool hasLatchedConfirmedAnchorPose;
    private Vector3 latchedAnchorPosition;
    private Quaternion latchedAnchorRotation = Quaternion.identity;

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

        UpdateHandRotationAlignment();
    }

    [ContextMenu("Anchor Binder/Apply Now")]
    public void ApplyNow()
    {
        AutoAssignTargets();

        Transform anchor = anchorPlacer != null ? anchorPlacer.CurrentAnchorTransform : null;
        if (anchor == null)
        {
            LogAlignmentEvent("BeginManualRotationAlignment ignored: anchor is null");
            return;
        }

        Vector3 targetPos = anchor.TransformPoint(localPositionOffset);
        Quaternion targetRot = ResolveTargetRotation(anchor);
        if (TryGetLatchedAnchorPose(out Vector3 anchorPosition, out Quaternion anchorRotation))
        {
            targetPos = anchorPosition + (anchorRotation * localPositionOffset);
            targetRot = ResolveTargetRotation(anchorRotation);
        }

        ApplyPose(deskOrigin, targetPos, targetRot);
        ApplyPose(trackerDeskTransform, targetPos, targetRot);
    }

    [ContextMenu("Anchor Binder/Begin Manual Rotation Alignment")]
    public void BeginManualRotationAlignment()
    {
        AutoAssignTargets();

        Transform anchor = anchorPlacer != null ? anchorPlacer.CurrentAnchorTransform : null;
        if (anchor == null)
            return;

        initialAlignmentRotation = useAnchorRotationAsInitialRotation
            ? anchor.rotation * Quaternion.Euler(localEulerOffset)
            : GetCurrentTargetRotation(anchor.rotation * Quaternion.Euler(localEulerOffset));

        yawAdjustmentDegrees = 0f;
        handRotationAdjustment = Quaternion.identity;
        hasLatchedConfirmedAnchorPose = false;
        HasAlignmentState = true;
        IsAlignmentConfirmed = !requireManualRotationConfirmation;
        wasLeftRotationPinching = IsLeftRotationPinching();
        wasLeftFineRotationPinching = IsLeftFineRotationPinching();
        wasRightConfirmPinching = IsRightConfirmPinching();
        waitingForRightConfirmRelease = requireRightPinchReleaseBeforeConfirm;
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
        CaptureLatchedAnchorPose(anchorPlacer != null ? anchorPlacer.CurrentAnchorTransform : null, "ConfirmManualRotationAlignment");
        LogAnchorDeskDiagnostic("ConfirmManualRotationAlignment saved", anchorPlacer != null ? anchorPlacer.CurrentAnchorTransform : null);
        initialAlignmentRotation = GetCurrentTargetRotation(Quaternion.identity);
        yawAdjustmentDegrees = 0f;
        handRotationAdjustment = Quaternion.identity;
        LogAlignmentEvent($"ConfirmManualRotationAlignment finalDesk={FormatTransform(deskOrigin)} savedOffsetPos={localPositionOffset} savedOffsetEuler={localEulerOffset} handAdjustment={FormatRotation(handRotationAdjustment)} yaw={yawAdjustmentDegrees:0.###}");
        AlignmentConfirmed?.Invoke();
    }

    public void ClearAlignmentState()
    {
        HasAlignmentState = false;
        IsAlignmentConfirmed = false;
        yawAdjustmentDegrees = 0f;
        handRotationAdjustment = Quaternion.identity;
        hasLatchedConfirmedAnchorPose = false;
        wasLeftRotationPinching = false;
        wasLeftFineRotationPinching = false;
        wasRightConfirmPinching = false;
        waitingForRightConfirmRelease = false;
        LogAlignmentEvent("ClearAlignmentState");
        AlignmentCleared?.Invoke();
    }

    [ContextMenu("Anchor Binder/Capture Current Desk As Offset")]
    public void CaptureCurrentDeskAsOffset()
    {
        Transform anchor = anchorPlacer != null ? anchorPlacer.CurrentAnchorTransform : null;
        Transform target = deskOrigin != null ? deskOrigin : trackerDeskTransform;
        if (anchor == null || target == null)
            return;

        localPositionOffset = Quaternion.Inverse(anchor.rotation) * (target.position - anchor.position);
        localEulerOffset = (Quaternion.Inverse(anchor.rotation) * target.rotation).eulerAngles;
        LogAlignmentEvent($"CaptureCurrentDeskAsOffset anchor={FormatTransform(anchor)} target={FormatTransform(target)} savedOffsetPos={localPositionOffset} savedOffsetEuler={localEulerOffset}");
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
        ApplySavedOffsetAsConfirmed(true);
    }

    private void ApplySavedOffsetAsConfirmed(bool refreshLatchedAnchorPose)
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
        if (refreshLatchedAnchorPose)
            CaptureLatchedAnchorPose(anchor, "ApplySavedOffsetAsConfirmed");
        LogAlignmentEvent($"ApplySavedOffsetAsConfirmed begin anchor={FormatTransform(anchor)} savedOffsetPos={localPositionOffset} savedOffsetEuler={localEulerOffset}");
        LogAnchorDeskDiagnostic("ApplySavedOffsetAsConfirmed before", anchor);
        ApplyNow();
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

    private static void ApplyPose(Transform target, Vector3 position, Quaternion rotation)
    {
        if (target == null)
            return;

        target.SetPositionAndRotation(position, rotation);
    }

    private void EnsureAlignmentStarted()
    {
        if (!HasAlignmentState)
            BeginManualRotationAlignment();
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
        subscribedToAnchorRefresh = true;
    }

    private void UnsubscribeAnchorRefresh()
    {
        if (!subscribedToAnchorRefresh)
            return;

        if (anchorPlacer != null)
            anchorPlacer.SavedAnchorRefreshed -= OnSavedAnchorRefreshed;
        subscribedToAnchorRefresh = false;
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
        ApplySavedOffsetAsConfirmed();
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
        ApplySavedOffsetAsConfirmed(false);
        LogAnchorDeskDiagnostic("ConfirmedDeskDrift after", anchor);
    }

    private bool TryRelatchConfirmedAnchorAfterLargeMove(Transform anchor)
    {
        if (anchor == null || !TryGetLatchedAnchorPose(out Vector3 latchedPosition, out Quaternion latchedRotation))
            return false;

        float anchorPositionDelta = Vector3.Distance(anchor.position, latchedPosition);
        float anchorRotationDelta = Quaternion.Angle(anchor.rotation, latchedRotation);
        if (anchorPositionDelta < confirmedAnchorRelatchPositionThresholdMeters &&
            anchorRotationDelta < confirmedAnchorRelatchRotationThresholdDegrees)
        {
            return false;
        }

        LogAlignmentEvent(
            $"ConfirmedAnchorRelatch detected posDelta={anchorPositionDelta:0.###}m rotDelta={anchorRotationDelta:0.###}deg " +
            $"old={FormatLatchedAnchorPose()} new={FormatTransform(anchor)}");
        CaptureLatchedAnchorPose(anchor, "ConfirmedAnchorRelatch");
        ApplySavedOffsetAsConfirmed(false);
        LogAnchorDeskDiagnostic("ConfirmedAnchorRelatch after", anchor);
        return true;
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

        latchedAnchorPosition = anchor.position;
        latchedAnchorRotation = anchor.rotation;
        hasLatchedConfirmedAnchorPose = true;
        LogAlignmentEvent($"LatchedAnchorPose reason={reason} {FormatLatchedAnchorPose()} liveAnchor={FormatTransform(anchor)}");
    }

    private bool TryGetLatchedAnchorPose(out Vector3 position, out Quaternion rotation)
    {
        position = latchedAnchorPosition;
        rotation = latchedAnchorRotation;
        return useLatchedAnchorPoseAfterConfirmation && IsAlignmentConfirmed && hasLatchedConfirmedAnchorPose;
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
                File.AppendAllText(handAlignmentLogPath, $"{Environment.NewLine}--- Session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}");
            }

            File.AppendAllText(handAlignmentLogPath, line + Environment.NewLine);
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
