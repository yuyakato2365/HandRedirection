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
    public OVRHand.HandFinger confirmFinger = OVRHand.HandFinger.Index;
    [Range(0f, 1f)] public float pinchStartThreshold = 0.7f;
    [Range(0f, 1f)] public float pinchReleaseThreshold = 0.35f;
    public bool autoFindAlignmentHands = true;
    public bool applyFullLeftHandRotation = true;
    public bool useLeftWristRotation = true;
    public bool invertLeftHandYaw = false;
    public bool requireRightPinchReleaseBeforeConfirm = true;

    [Header("Behaviour")]
    public bool followEveryFrame = true;
    public bool applyOnStart = true;

    [Header("Debug")]
    public bool logHandAlignmentDebug = true;
    public bool writeHandAlignmentLogFile = true;
    public float activePinchLogIntervalSec = 0.25f;

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
    private float nextActivePinchLogTime;
    private float nextLeftWristFailureLogTime;
    private string lastLeftRotationSource = "none";
    private string handAlignmentLogPath;

    private void Awake()
    {
        if (anchorPlacer == null)
            anchorPlacer = FindAnyObjectByType<ManualSpatialAnchorPlacer>();
        AutoAssignTargets();
        AutoAssignPinchProviders();
        AutoAssignHands();
        AutoAssignLeftWrist();
        LogAlignmentEvent($"Awake {BuildSourceDebugString()}");
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
        {
            LogAlignmentEvent("BeginManualRotationAlignment ignored: anchor is null");
            return;
        }

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
        LogAlignmentEvent($"ConfirmManualRotationAlignment finalDesk={FormatTransform(deskOrigin)} handAdjustment={FormatRotation(handRotationAdjustment)} yaw={yawAdjustmentDegrees:0.###}");
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
        AutoAssignPinchProviders();
        AutoAssignLeftWrist();

        bool leftPinching = IsLeftRotationPinching();
        if (leftPinching != wasLeftRotationPinching)
            LogAlignmentEvent($"Left pinch changed {wasLeftRotationPinching} -> {leftPinching} {BuildPinchDebugString()}");

        if (leftPinching && !wasLeftRotationPinching)
        {
            leftPinchStartYawDegrees = GetHandYawDegrees(leftRotationHand);
            yawAdjustmentAtLeftPinchStart = yawAdjustmentDegrees;
            leftPinchStartRotation = GetLeftRotationAlignmentRotation();
            handRotationAdjustmentAtLeftPinchStart = handRotationAdjustment;
            LogAlignmentEvent(
                $"Left pinch start source={lastLeftRotationSource} wrist={FormatTransform(leftWristTransform)} " +
                $"startRot={FormatRotation(leftPinchStartRotation)} carryAdjustment={FormatRotation(handRotationAdjustmentAtLeftPinchStart)}");
        }
        else if (leftPinching)
        {
            if (applyFullLeftHandRotation)
            {
                Quaternion deltaRotation = GetLeftRotationAlignmentRotation() * Quaternion.Inverse(leftPinchStartRotation);
                handRotationAdjustment = deltaRotation * handRotationAdjustmentAtLeftPinchStart;
                LogActivePinchFrame(deltaRotation);
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

    private void LogActivePinchFrame(Quaternion deltaRotation)
    {
        if (!logHandAlignmentDebug || Time.realtimeSinceStartup < nextActivePinchLogTime)
            return;

        nextActivePinchLogTime = Time.realtimeSinceStartup + activePinchLogIntervalSec;
        Quaternion currentRotation = GetLeftRotationAlignmentRotation();
        LogAlignmentEvent(
            $"Left pinch active source={lastLeftRotationSource} current={FormatRotation(currentRotation)} " +
            $"delta={FormatRotation(deltaRotation)} handAdjustment={FormatRotation(handRotationAdjustment)} " +
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

    private string BuildSourceDebugString()
    {
        return $"leftProvider={GetObjectName(leftRotationPinchProvider)} rightProvider={GetObjectName(rightConfirmPinchProvider)} " +
               $"leftHand={GetObjectName(leftRotationHand)} rightHand={GetObjectName(rightConfirmHand)} " +
               $"leftWrist={GetObjectName(leftWristTransform)} desk={GetObjectName(deskOrigin)} trackerDesk={GetObjectName(trackerDeskTransform)}";
    }

    private string BuildPinchDebugString()
    {
        return $"leftProviderPinch={GetProviderPinchDebug(leftRotationPinchProvider)} rightProviderPinch={GetProviderPinchDebug(rightConfirmPinchProvider)} " +
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

    private static string GetObjectName(UnityEngine.Object obj)
    {
        return obj != null ? obj.name : "null";
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
