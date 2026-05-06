using System;
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

    [Header("Manual Rotation Alignment")]
    public bool requireManualRotationConfirmation = true;
    [Tooltip("If false, anchor confirmation only translates deskOrigin first and keeps the current desk rotation as the starting angle.")]
    public bool useAnchorRotationAsInitialRotation = false;
    public bool yawOnlyRotationAdjustment = true;
    public float yawAdjustStepDegrees = 1f;
    public float yawAdjustLargeStepDegrees = 5f;

    [Header("Behaviour")]
    public bool followEveryFrame = true;
    public bool applyOnStart = true;

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

    private void Awake()
    {
        if (anchorPlacer == null)
            anchorPlacer = FindAnyObjectByType<ManualSpatialAnchorPlacer>();
        AutoAssignTargets();
    }

    private void Start()
    {
        if (applyOnStart)
            ApplyNow();
    }

    private void LateUpdate()
    {
        if (followEveryFrame)
            ApplyNow();
    }

    [ContextMenu("Anchor Binder/Apply Now")]
    public void ApplyNow()
    {
        AutoAssignTargets();

        Transform anchor = anchorPlacer != null ? anchorPlacer.CurrentAnchorTransform : null;
        if (anchor == null)
            return;

        Vector3 targetPos = anchor.TransformPoint(localPositionOffset);
        Quaternion targetRot = ResolveTargetRotation(anchor);

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
        HasAlignmentState = true;
        IsAlignmentConfirmed = !requireManualRotationConfirmation;
        ApplyNow();

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
        AlignmentConfirmed?.Invoke();
    }

    public void ClearAlignmentState()
    {
        HasAlignmentState = false;
        IsAlignmentConfirmed = false;
        yawAdjustmentDegrees = 0f;
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

    private Quaternion ResolveTargetRotation(Transform anchor)
    {
        Quaternion baseRotation = HasAlignmentState
            ? initialAlignmentRotation
            : anchor.rotation * Quaternion.Euler(localEulerOffset);

        Quaternion adjustment = Quaternion.Euler(0f, yawAdjustmentDegrees, 0f);
        return yawOnlyRotationAdjustment ? adjustment * baseRotation : baseRotation * adjustment;
    }

    private Quaternion GetCurrentTargetRotation(Quaternion fallbackRotation)
    {
        if (deskOrigin != null)
            return deskOrigin.rotation;
        if (trackerDeskTransform != null)
            return trackerDeskTransform.rotation;

        return fallbackRotation;
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
}
