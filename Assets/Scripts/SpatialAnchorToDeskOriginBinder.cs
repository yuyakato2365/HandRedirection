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

    [Header("Hand Rotation Alignment")]
    public bool enableHandRotationAlignment = true;
    public OVRHand leftRotationHand;
    public OVRHand rightConfirmHand;
    public OVRHand.HandFinger rotationFinger = OVRHand.HandFinger.Index;
    public OVRHand.HandFinger confirmFinger = OVRHand.HandFinger.Index;
    [Range(0f, 1f)] public float pinchStartThreshold = 0.7f;
    [Range(0f, 1f)] public float pinchReleaseThreshold = 0.35f;
    public bool autoFindAlignmentHands = true;
    public bool applyFullLeftHandRotation = true;
    public bool invertLeftHandYaw = false;
    public bool requireRightPinchReleaseBeforeConfirm = true;

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
    private bool wasLeftRotationPinching;
    private bool wasRightConfirmPinching;
    private bool waitingForRightConfirmRelease;
    private float leftPinchStartYawDegrees;
    private float yawAdjustmentAtLeftPinchStart;
    private Quaternion leftPinchStartRotation = Quaternion.identity;
    private Quaternion handRotationAdjustment = Quaternion.identity;
    private Quaternion handRotationAdjustmentAtLeftPinchStart = Quaternion.identity;

    private void Awake()
    {
        if (anchorPlacer == null)
            anchorPlacer = FindAnyObjectByType<ManualSpatialAnchorPlacer>();
        AutoAssignTargets();
        AutoAssignHands();
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

        UpdateHandRotationAlignment();
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
        handRotationAdjustment = Quaternion.identity;
        HasAlignmentState = true;
        IsAlignmentConfirmed = !requireManualRotationConfirmation;
        wasLeftRotationPinching = IsLeftRotationPinching();
        wasRightConfirmPinching = IsRightConfirmPinching();
        waitingForRightConfirmRelease = requireRightPinchReleaseBeforeConfirm;
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
        handRotationAdjustment = Quaternion.identity;
        wasLeftRotationPinching = false;
        wasRightConfirmPinching = false;
        waitingForRightConfirmRelease = false;
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

    private void UpdateHandRotationAlignment()
    {
        if (!enableHandRotationAlignment || !IsAdjustingAlignment)
            return;

        AutoAssignHands();

        bool leftPinching = IsLeftRotationPinching();
        if (leftPinching && !wasLeftRotationPinching)
        {
            leftPinchStartYawDegrees = GetHandYawDegrees(leftRotationHand);
            yawAdjustmentAtLeftPinchStart = yawAdjustmentDegrees;
            leftPinchStartRotation = GetHandRotation(leftRotationHand);
            handRotationAdjustmentAtLeftPinchStart = handRotationAdjustment;
        }
        else if (leftPinching)
        {
            if (applyFullLeftHandRotation)
            {
                Quaternion deltaRotation = GetHandRotation(leftRotationHand) * Quaternion.Inverse(leftPinchStartRotation);
                handRotationAdjustment = deltaRotation * handRotationAdjustmentAtLeftPinchStart;
            }
            else
            {
                float currentYaw = GetHandYawDegrees(leftRotationHand);
                float deltaYaw = Mathf.DeltaAngle(leftPinchStartYawDegrees, currentYaw);
                if (invertLeftHandYaw)
                    deltaYaw = -deltaYaw;

                yawAdjustmentDegrees = yawAdjustmentAtLeftPinchStart + deltaYaw;
            }

            ApplyNow();
            AlignmentChanged?.Invoke();
        }

        wasLeftRotationPinching = leftPinching;

        bool rightPinching = IsRightConfirmPinching();
        if (waitingForRightConfirmRelease)
        {
            if (!rightPinching)
                waitingForRightConfirmRelease = false;
        }
        else if (rightPinching && !wasRightConfirmPinching)
        {
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
        return IsHandPinching(leftRotationHand, rotationFinger);
    }

    private bool IsRightConfirmPinching()
    {
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

            string lowerName = hand.name.ToLowerInvariant();
            if (leftRotationHand == null && lowerName.Contains("left"))
                leftRotationHand = hand;
            else if (rightConfirmHand == null && lowerName.Contains("right"))
                rightConfirmHand = hand;
        }
    }
}
