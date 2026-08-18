using System.Collections.Generic;
using System;
using System.Reflection;
using UnityEngine;
using Oculus.Interaction.Input;

/// <summary>
/// Drives a humanoid/avatar arm and fingers from Meta/OVR hand tracking.
/// This is intended as a first-pass exhibition driver: arm position is solved
/// with a lightweight two-bone IK, while finger shape is copied from OVRSkeleton.
/// </summary>
public class AvatarHandTrackingDriver : MonoBehaviour
{
    [System.Serializable]
    public class HandRig
    {
        public string label = "Left";
        public bool isLeft = true;

        [Header("OVR Source")]
        public OVRHand ovrHand;
        public OVRSkeleton ovrSkeleton;
        [Tooltip("OVRLeftHandDataSource / OVRRightHandDataSource or another DataSource<HandDataAsset>.")]
        public MonoBehaviour handDataSource;
        public Transform wristSourceOverride;
        [Tooltip("Optional rotation-only source. Use this when the redirected wrist transform only changes position.")]
        public Transform wristRotationSourceOverride;

        [Header("Avatar Arm")]
        public Transform shoulder;
        public Transform upperArm;
        public Transform lowerArm;
        public Transform hand;
        public Transform poleHint;
        [Tooltip("Used when Pole Hint is empty. Direction is expressed in avatar root local space.")]
        public Vector3 poleDirectionLocal = new Vector3(0f, -1f, -0.25f);
        [Tooltip("Mirror this hand's pole X direction. Useful when only one elbow bends to the wrong side.")]
        public bool mirrorPoleX;
        [Tooltip("Additional pole X in avatar-root local space. Negative usually pushes the left elbow outward, positive pushes the right elbow outward.")]
        public float poleSideOffsetX;
        [Tooltip("Let the elbow/arm roll follow the wrist rotation instead of using only the fixed pole direction.")]
        public bool useWristRotationForPole = true;
        [Tooltip("Pole direction expressed in the wrist source local space. Change this if the elbow rolls the wrong way.")]
        public Vector3 wristPoleDirectionLocal = new Vector3(0f, -1f, 0f);
        [Range(0f, 1f)] public float wristPoleWeight = 0.65f;
        [Tooltip("Roll the forearm around its own length axis to follow wrist rotation.")]
        public bool twistForearmFromWrist = true;
        [Tooltip("Reference direction on the forearm used to measure twist.")]
        public Vector3 forearmTwistReferenceLocal = Vector3.up;
        [Tooltip("Reference direction on the wrist used to drive forearm twist.")]
        public Vector3 wristTwistReferenceLocal = Vector3.up;
        [Range(0f, 1f)] public float forearmTwistWeight = 0.75f;
        public float forearmTwistDirection = 1f;
        public float forearmTwistOffsetDegrees = 0f;
        public float maxForearmTwistDegrees = 80f;
        public float maxForearmTwistChangePerSecond = 240f;
        [Range(0f, 0.99f)] public float forearmTwistSmoothing = 0.35f;
        [Tooltip("Extra reach multiplier for this hand. Use this only to compensate left/right source range mismatch.")]
        public float reachScale = 1f;
        [Range(0.05f, 0.95f)] public float elbowPositionRatio = 0.5f;
        [Tooltip("Optional bend amount away from the shoulder-to-hand line. Keep 0 for a straight telescopic/robot arm.")]
        public float elbowBendOffset = 0f;
        [Tooltip("Use wrist rotation and captured lower-arm length to estimate the elbow position.")]
        public bool useWristRotationEstimatedElbow = true;
        [Tooltip("How much the wrist-rotation elbow estimate affects the actual avatar arm. Keep 0 to avoid wrist-bend driven arm drift.")]
        [Range(0f, 1f)] public float wristRotationElbowArmWeight = 0.3f;
        [Tooltip("Maximum meters the wrist-rotation elbow estimate can pull the actual arm elbow away from the natural IK elbow.")]
        public float maxWristRotationElbowArmOffset = 0.2f;
        [Tooltip("Optional scene object moved to the wrist-rotation estimated elbow each frame.")]
        public Transform estimatedElbowVisual;
        [Tooltip("Captured hand-local direction from wrist/hand to elbow. Use Capture Arm Bind Pose to refresh.")]
        public Vector3 wristToElbowLocalDirection = Vector3.back;
        [Tooltip("Captured lower-arm length used by the wrist-rotation elbow estimate.")]
        public float wristToElbowLength = 0.25f;
        public Vector3 estimatedElbowPosition;
        public bool estimatedElbowValid;

        [Header("Avatar Fingers")]
        public Transform thumbProximal;
        public Transform thumbIntermediate;
        public Transform thumbDistal;
        public Transform indexProximal;
        public Transform indexIntermediate;
        public Transform indexDistal;
        public Transform middleProximal;
        public Transform middleIntermediate;
        public Transform middleDistal;
        public Transform ringProximal;
        public Transform ringIntermediate;
        public Transform ringDistal;
        public Transform littleProximal;
        public Transform littleIntermediate;
        public Transform littleDistal;

        [Header("Offsets")]
        public Vector3 wristPositionOffset;
        public Vector3 wristEulerOffset;
        public bool useCalibratedWristRotationOffset = true;
        public Quaternion calibratedWristRotationOffset = Quaternion.identity;
        [Range(0f, 1f)] public float positionWeight = 1f;
        [Range(0f, 1f)] public float rotationWeight = 1f;
        [Range(0f, 1f)] public float fingerWeight = 1f;
        [Range(0f, 1f)] public float thumbOpenCurl = 0f;
        [Range(0f, 1f)] public float thumbClosedCurl = 0.75f;
        [Range(0f, 1f)] public float fingerOpenCurl = 0f;
        [Range(0f, 1f)] public float fingerClosedCurl = 0.9f;
        [Tooltip("Extra extension applied when the tracked hand is open. Increase this when the avatar's bind pose is still slightly curled.")]
        public float thumbOpenExtensionDegrees = 22f;
        public float fingerOpenExtensionDegrees = 18f;
        [Tooltip("Extra curl applied from the other four fingers. Keep 0 when thumb movement incorrectly pulls the fingers.")]
        [Range(0f, 1f)] public float fistCurlBoost = 0f;
        [Range(0.1f, 3f)] public float fingerCurlResponse = 0.45f;
        [Range(0f, 1f)] public float distalCurlMultiplier = 1.15f;
        [Tooltip("Experimental: drive finger bones from tracked wrist-relative segment delta rotations. Keep off unless explicitly testing full pose copy.")]
        public bool useJointPoseSegmentDeltaRotations = false;
        [Range(0f, 1f)] public float segmentDeltaRotationWeight = 1f;
        [Range(1f, 180f)] public float segmentDeltaMaxDegreesPerFrame = 90f;
        [Tooltip("Flip all copied segment delta rotations. Set to -1 when all fingers bend opposite to the real hand.")]
        public float segmentDeltaRotationDirection = -1f;
        [Tooltip("Capture the first valid tracked pose as the neutral segment pose for delta rotations.")]
        public bool autoCalibrateSegmentDeltaOpenPose = true;
        [Tooltip("Flip source wrist-local delta axes if the copied motion is mirrored.")]
        public Vector3 segmentDeltaAxisSign = Vector3.one;
        [Tooltip("Debug: first segment side-to-side deltas for index/middle/ring/little in tracked wrist-local space.")]
        public Vector4 segmentDeltaFourFingerSpreadDebug;
        public string segmentDeltaDebug;
        [Tooltip("Use HandDataAsset.JointPoses to compute each finger's own curl. This is the independent-finger path.")]
        public bool useJointPoseFingerCurl = true;
        [Tooltip("Add side-to-side spread only to the index/middle/ring/little proximal bones from JointPoses.")]
        public bool useJointPoseProximalSpread = true;
        [Range(0f, 1f)] public float proximalSpreadWeight = 0.7f;
        [Tooltip("Fallback local axis used only when palm-normal spread axis is unavailable.")]
        public Vector3 proximalSpreadAxis = Vector3.up;
        [Tooltip("Flip the wrist-fixed palm-normal axis used to rotate finger spread.")]
        public bool invertProximalSpreadPalmAxis = false;
        [Tooltip("Flip all four-finger spread input when opening/closing moves backward.")]
        public float proximalSpreadDirection = 1f;
        [Tooltip("Flip only the left hand's four-finger spread input.")]
        public bool mirrorLeftProximalSpread = true;
        [Tooltip("Flip only the right hand's four-finger spread input.")]
        public bool mirrorRightProximalSpread = true;
        [Tooltip("Capture the first valid tracked pose as the neutral four-finger spread pose.")]
        public bool autoCalibrateProximalSpreadOpenPose = true;
        [Range(0f, 0.5f)] public float proximalSpreadDeadZone = 0.02f;
        [Range(1f, 30f)] public float proximalSpreadInputScale = 10f;
        [Tooltip("Per-finger multiplier for index/middle/ring/little spread input after global scaling.")]
        public Vector4 proximalSpreadFingerScale = Vector4.one;
        [Tooltip("Per-finger offset for index/middle/ring/little spread input after scaling. Use small values like +/-0.1.")]
        public Vector4 proximalSpreadInputOffset = Vector4.zero;
        public float indexProximalSpreadDegrees = 25f;
        public float middleProximalSpreadDegrees = 10f;
        public float ringProximalSpreadDegrees = 12f;
        public float littleProximalSpreadDegrees = 25f;
        public Vector4 proximalSpreadInput;
        public Vector3 proximalSpreadPalmNormal;
        public string proximalSpreadDebug;
        [Tooltip("Capture the first valid tracked hand pose as the open-hand reference for JointPoses curl.")]
        public bool autoCalibrateJointCurlOpenPose = true;
        public float jointCurlOpenAngle = 8f;
        public float jointCurlClosedAngle = 80f;
        [Tooltip("How many degrees away from the open-hand reference should count as fully curled.")]
        public float jointCurlClosedDeltaAngle = 55f;
        [Range(0.1f, 3f)] public float jointCurlResponse = 0.85f;
        [Tooltip("Flip this when fingers bend backward. TonaThas may need -1 depending on the imported bind pose.")]
        public float fingerCurlDirection = 1f;
        [Tooltip("Curl axis in each avatar finger bone's local space. Try X first, then Y or Z if the bend axis is wrong.")]
        public Vector3 fingerCurlAxis = Vector3.right;
        [Tooltip("Primary thumb curl axis in each thumb bone's local space.")]
        public Vector3 thumbCurlAxis = Vector3.right;
        [Tooltip("Secondary thumb opposition axis. This adds the out-of-palm motion missing from a single-axis curl.")]
        public Vector3 thumbOppositionAxis = Vector3.forward;
        [Tooltip("Third thumb axis for sideways spread/abduction in the thumb root.")]
        public Vector3 thumbSpreadAxis = Vector3.up;
        public float thumbCurlDegrees = 68f;
        public float thumbOppositionDegrees = 38f;
        public float thumbSpreadDegrees = 35f;
        public float thumbCurlOffsetDegrees = 0f;
        public float thumbOppositionOffsetDegrees = 0f;
        public float thumbSpreadOffsetDegrees = 0f;
        [Range(0f, 0.5f)] public float thumbBallDeadZone = 0.04f;
        [Tooltip("Flip only the thumb spread input from JointPoses.")]
        public float thumbSpreadDirection = 1f;
        [Tooltip("Flip only the thumb out-of-palm/opposition input from JointPoses.")]
        public float thumbOutOfPalmDirection = 1f;
        [Tooltip("Flip only the procedural thumb curl direction, separate from the other fingers.")]
        public float thumbCurlDirection = 1f;
        [Tooltip("Use the tracked thumb direction in palm space, not only thumb curl, to approximate a ball joint.")]
        public bool useThumbBallFromJointPose = true;
        [Tooltip("Mirror the open-hand extension on the right hand. Turn on only if the right hand opens in the wrong direction.")]
        public bool mirrorRightOpenExtension = false;
        [Tooltip("Mirror the thumb opposition on the right hand. Turn on only if the right thumb opposition moves in the wrong direction.")]
        public bool mirrorRightThumbOpposition = false;

        [Header("Debug")]
        public bool sourceTracked;
        public string wristSourceName;
        public float wristTargetDistance;
        public float shoulderToWristTargetDistance;
        public Vector2 thumbStrength;
        public Vector2 indexStrength;
        public Vector2 middleStrength;
        public Vector2 ringStrength;
        public Vector2 littleStrength;
        public Vector3 thumbBallInput;
        public string fingerInputSource;
        public string jointCurlDebug;
    }

    [Header("Avatar Root")]
    public Transform avatarRoot;

    [Header("Hands")]
    public HandRig leftHand = new HandRig { label = "Left", isLeft = true };
    public HandRig rightHand = new HandRig { label = "Right", isLeft = false };

    [Header("Behaviour")]
    public bool autoFindAvatarBones = true;
    public bool autoFindOvrSources = true;
    public bool autoUseHandRedirectionSources = true;
    public bool captureFingerOffsetsOnStart = true;
    public bool solveArmIk = true;
    public bool driveWristRotation = true;
    public bool driveFingerRotations = true;
    [Tooltip("Reset upper arm, lower arm, and hand local transforms to the captured T-pose/bind pose before solving IK each frame.")]
    public bool resetArmToBindPoseBeforeSolve = true;
    [Tooltip("Use HandDataAsset/OVRHand pinch strength to synthesize finger curl when OVRSkeleton is not present.")]
    public bool usePinchStrengthFingerFallback = true;
    public bool allowWristOverrideWithoutTracking = true;
    [Tooltip("For redirected/long-arm use: force the avatar hand bone to the redirected target even beyond the original arm reach.")]
    public bool matchWristTargetExactly = true;
    [Tooltip("Best for long-arm redirection: place elbow/forearm on the line between shoulder and redirected wrist instead of preserving original arm length.")]
    public bool placeArmBetweenShoulderAndWrist = false;
    [Tooltip("Use a natural two-bone IK solve, but stretch upper/lower arm lengths when the redirected hand is beyond the original reach.")]
    public bool stretchArmForRedirectedReach = true;
    [Range(0f, 0.45f)] public float ikBendAmount = 0.18f;
    [Tooltip("Scales each hand's Elbow Bend Offset with current shoulder-to-wrist distance.")]
    public bool scaleElbowBendWithReach = true;
    [Range(0f, 0.5f)] public float elbowBendReachRatio = 0.12f;
    public float maxElbowBendOffset = 0.35f;
    public float maxIkReachScale = 0.98f;

    struct JointPoseLayout
    {
        public string name;
        public int wrist;
        public int thumb0;
        public int thumb1;
        public int thumb2;
        public int thumb3;
        public int thumbTip;
        public int indexBase;
        public int index2;
        public int index3;
        public int indexTip;
        public int middleBase;
        public int middle2;
        public int middle3;
        public int middleTip;
        public int ringBase;
        public int ring2;
        public int ring3;
        public int ringTip;
        public int littleBase;
        public int little2;
        public int little3;
        public int littleTip;
    }

    readonly Dictionary<Transform, Quaternion> _sourceBindRotations = new Dictionary<Transform, Quaternion>();
    readonly Dictionary<Transform, Quaternion> _targetBindRotations = new Dictionary<Transform, Quaternion>();
    readonly Dictionary<Transform, Transform> _fingerMap = new Dictionary<Transform, Transform>();
    readonly Dictionary<HandRig, float[]> _jointOpenCurlAngles = new Dictionary<HandRig, float[]>();
    readonly Dictionary<HandRig, Vector3[]> _jointOpenSegmentDirections = new Dictionary<HandRig, Vector3[]>();
    readonly Dictionary<HandRig, Vector3> _thumbOpenPalmComponents = new Dictionary<HandRig, Vector3>();
    readonly Dictionary<HandRig, Vector4> _openProximalSpreadComponents = new Dictionary<HandRig, Vector4>();
    readonly Dictionary<HandRig, Vector3> _openProximalSpreadPalmRights = new Dictionary<HandRig, Vector3>();
    readonly Dictionary<HandRig, Vector3> _openProximalSpreadPalmNormals = new Dictionary<HandRig, Vector3>();
    readonly Dictionary<HandRig, Vector3> _avatarProximalSpreadAxisLocal = new Dictionary<HandRig, Vector3>();
    readonly Dictionary<HandRig, Vector3> _forearmNeutralWristRefs = new Dictionary<HandRig, Vector3>();
    readonly Dictionary<HandRig, Quaternion> _lastForearmTwists = new Dictionary<HandRig, Quaternion>();
    readonly Dictionary<HandRig, float> _lastForearmTwistAngles = new Dictionary<HandRig, float>();
    readonly Dictionary<Transform, Vector3> _armBindLocalPositions = new Dictionary<Transform, Vector3>();
    readonly Dictionary<Transform, Quaternion> _armBindLocalRotations = new Dictionary<Transform, Quaternion>();

    void Reset()
    {
        avatarRoot = transform;
    }

    void Awake()
    {
        if (avatarRoot == null)
            avatarRoot = transform;

        NormalizeOpenCurlDefaults(leftHand);
        NormalizeOpenCurlDefaults(rightHand);

        if (autoFindAvatarBones)
        {
            AutoFindAvatarBones(leftHand);
            AutoFindAvatarBones(rightHand);
        }

        if (autoFindOvrSources)
            AutoFindOvrSources();

        if (autoUseHandRedirectionSources)
            AutoFindHandRedirectionSources();
    }

    void OnValidate()
    {
        if (avatarRoot == null)
            avatarRoot = transform;

        if (autoFindAvatarBones && avatarRoot != null)
        {
            AutoFindAvatarBones(leftHand);
            AutoFindAvatarBones(rightHand);
        }

        NormalizeOpenCurlDefaults(leftHand);
        NormalizeOpenCurlDefaults(rightHand);
    }

    void Start()
    {
        CaptureArmBindPose();

        if (captureFingerOffsetsOnStart)
            CaptureFingerBindPose();
    }

    void NormalizeOpenCurlDefaults(HandRig rig)
    {
        if (rig == null)
            return;

        if (Mathf.Approximately(rig.thumbOpenCurl, 0.15f))
            rig.thumbOpenCurl = 0f;
        if (Mathf.Approximately(rig.fingerOpenCurl, 0.05f))
            rig.fingerOpenCurl = 0f;
        rig.fistCurlBoost = 0f;
        rig.mirrorRightOpenExtension = false;
        rig.mirrorRightThumbOpposition = false;
        if (rig.thumbSpreadAxis == Vector3.zero)
            rig.thumbSpreadAxis = Vector3.up;
        rig.thumbSpreadDirection = NormalizeSign(rig.thumbSpreadDirection);
        rig.thumbOutOfPalmDirection = NormalizeSign(rig.thumbOutOfPalmDirection);
        rig.thumbCurlDirection = NormalizeSign(rig.thumbCurlDirection);
        rig.segmentDeltaRotationDirection = NormalizeSign(rig.segmentDeltaRotationDirection);
        rig.segmentDeltaAxisSign = NormalizeAxisSigns(rig.segmentDeltaAxisSign);
        rig.proximalSpreadDirection = NormalizeSign(rig.proximalSpreadDirection);
        if (rig.proximalSpreadAxis == Vector3.zero)
            rig.proximalSpreadAxis = Vector3.up;
        if (rig.proximalSpreadFingerScale == Vector4.zero)
            rig.proximalSpreadFingerScale = Vector4.one;
        rig.forearmTwistDirection = NormalizeSign(rig.forearmTwistDirection);
        if (rig.isLeft && Mathf.Approximately(rig.poleDirectionLocal.x, 0f) && Mathf.Approximately(rig.poleSideOffsetX, 0f))
            rig.poleSideOffsetX = -0.25f;
        if (!rig.isLeft && Mathf.Approximately(rig.reachScale, 0f))
            rig.reachScale = 1f;
        if (rig.isLeft && Mathf.Approximately(rig.reachScale, 0f))
            rig.reachScale = 1f;
    }

    static float NormalizeSign(float value)
    {
        return value < 0f ? -1f : 1f;
    }

    static Vector3 NormalizeAxisSigns(Vector3 signs)
    {
        return new Vector3(
            NormalizeSign(signs.x),
            NormalizeSign(signs.y),
            NormalizeSign(signs.z));
    }

    void LateUpdate()
    {
        DriveHand(leftHand);
        DriveHand(rightHand);
    }

    [ContextMenu("Avatar Hand Driver/Auto Find Avatar Bones")]
    public void AutoFindAvatarBones()
    {
        AutoFindAvatarBones(leftHand);
        AutoFindAvatarBones(rightHand);
    }

    [ContextMenu("Avatar Hand Driver/Auto Find OVR Sources")]
    public void AutoFindOvrSources()
    {
        OVRHand[] hands = FindObjectsByType<OVRHand>(FindObjectsSortMode.None);
        foreach (OVRHand hand in hands)
        {
            bool isLeft = IsLikelyHandedness(hand, true);
            bool isRight = IsLikelyHandedness(hand, false);
            HandRig rig = isLeft ? leftHand : isRight ? rightHand : null;
            if (rig == null || rig.ovrHand != null)
                continue;

            rig.ovrHand = hand;
            rig.handDataSource = hand as MonoBehaviour;
            rig.ovrSkeleton = FindSkeletonForHand(hand);
        }

        OVRSkeleton[] skeletons = FindObjectsByType<OVRSkeleton>(FindObjectsSortMode.None);
        foreach (OVRSkeleton skeleton in skeletons)
        {
            bool isLeft = IsLikelyHandedness(skeleton, true);
            bool isRight = IsLikelyHandedness(skeleton, false);
            HandRig rig = isLeft ? leftHand : isRight ? rightHand : null;
            if (rig != null && rig.ovrSkeleton == null)
                rig.ovrSkeleton = skeleton;
        }

        if (leftHand.ovrHand == null || rightHand.ovrHand == null)
            AutoFindOvrHandsFromRedirectionOriginals();

        if (leftHand.ovrHand != null && leftHand.ovrSkeleton == null)
            leftHand.ovrSkeleton = FindSkeletonForHand(leftHand.ovrHand);
        if (rightHand.ovrHand != null && rightHand.ovrSkeleton == null)
            rightHand.ovrSkeleton = FindSkeletonForHand(rightHand.ovrHand);

        AutoFindHandDataSources();
    }

    [ContextMenu("Avatar Hand Driver/Auto Find Hand Data Sources")]
    public void AutoFindHandDataSources()
    {
        MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (MonoBehaviour behaviour in behaviours)
        {
            if (behaviour == null)
                continue;

            if (!IsHandDataSourceLike(behaviour))
                continue;

            if (leftHand.handDataSource == null && IsLikelyHandedness(behaviour, true))
                leftHand.handDataSource = behaviour;
            else if (rightHand.handDataSource == null && IsLikelyHandedness(behaviour, false))
                rightHand.handDataSource = behaviour;
        }
    }

    [ContextMenu("Avatar Hand Driver/Auto Use Hand Redirection Sources")]
    public void AutoFindHandRedirectionSources()
    {
        MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (MonoBehaviour behaviour in behaviours)
        {
            if (behaviour == null)
                continue;

            Transform left = GetTransformField(behaviour, "leftHandRedirector");
            Transform right = GetTransformField(behaviour, "rightHandRedirector");
            if (left == null && right == null)
                continue;

            if (left != null)
                leftHand.wristSourceOverride = left;
            if (right != null)
                rightHand.wristSourceOverride = right;

            Transform leftOriginal = GetTransformField(behaviour, "leftHandOriginal");
            Transform rightOriginal = GetTransformField(behaviour, "rightHandOriginal");
            if (leftOriginal != null)
                leftHand.wristRotationSourceOverride = leftOriginal;
            if (rightOriginal != null)
                rightHand.wristRotationSourceOverride = rightOriginal;

            return;
        }
    }

    void AutoFindOvrHandsFromRedirectionOriginals()
    {
        MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (MonoBehaviour behaviour in behaviours)
        {
            if (behaviour == null)
                continue;

            Transform leftOriginal = GetTransformField(behaviour, "leftHandOriginal");
            Transform rightOriginal = GetTransformField(behaviour, "rightHandOriginal");

            if (leftHand.ovrHand == null && leftOriginal != null)
                leftHand.ovrHand = leftOriginal.GetComponentInParent<OVRHand>() ?? leftOriginal.GetComponentInChildren<OVRHand>();
            if (rightHand.ovrHand == null && rightOriginal != null)
                rightHand.ovrHand = rightOriginal.GetComponentInParent<OVRHand>() ?? rightOriginal.GetComponentInChildren<OVRHand>();
        }
    }

    [ContextMenu("Avatar Hand Driver/Capture Finger Bind Pose")]
    public void CaptureFingerBindPose()
    {
        _sourceBindRotations.Clear();
        _targetBindRotations.Clear();
        _fingerMap.Clear();
        _jointOpenSegmentDirections.Clear();
        _thumbOpenPalmComponents.Clear();
        _openProximalSpreadComponents.Clear();
        _openProximalSpreadPalmRights.Clear();
        _openProximalSpreadPalmNormals.Clear();
        _avatarProximalSpreadAxisLocal.Clear();

        CaptureFingerBindPose(leftHand);
        CaptureFingerBindPose(rightHand);
    }

    [ContextMenu("Avatar Hand Driver/Capture Arm Bind Pose")]
    public void CaptureArmBindPose()
    {
        _lastForearmTwists.Clear();
        _lastForearmTwistAngles.Clear();
        _forearmNeutralWristRefs.Clear();
        CaptureArmBindPose(leftHand);
        CaptureArmBindPose(rightHand);
    }

    void CaptureArmBindPose(HandRig rig)
    {
        if (rig == null)
            return;

        AddArmBindTransform(rig.upperArm);
        AddArmBindTransform(rig.lowerArm);
        AddArmBindTransform(rig.hand);
        CaptureWristRotationElbowReference(rig);
    }

    void AddArmBindTransform(Transform bone)
    {
        if (bone == null)
            return;

        _armBindLocalPositions[bone] = bone.localPosition;
        _armBindLocalRotations[bone] = bone.localRotation;
    }

    void CaptureWristRotationElbowReference(HandRig rig)
    {
        if (rig == null || rig.hand == null || rig.lowerArm == null)
            return;

        Vector3 wristToElbow = rig.lowerArm.position - rig.hand.position;
        if (wristToElbow.sqrMagnitude < 1e-8f)
            return;

        rig.wristToElbowLength = wristToElbow.magnitude;
        rig.wristToElbowLocalDirection = Quaternion.Inverse(rig.hand.rotation) * wristToElbow.normalized;
    }

    void ResetArmToBindPose(HandRig rig)
    {
        if (rig == null || !resetArmToBindPoseBeforeSolve)
            return;

        ResetArmBoneToBindPose(rig.upperArm);
        ResetArmBoneToBindPose(rig.lowerArm);
        ResetArmBoneToBindPose(rig.hand);
        _lastForearmTwists.Remove(rig);
    }

    void ResetArmBoneToBindPose(Transform bone)
    {
        if (bone == null)
            return;

        if (_armBindLocalPositions.TryGetValue(bone, out Vector3 localPosition))
            bone.localPosition = localPosition;
        if (_armBindLocalRotations.TryGetValue(bone, out Quaternion localRotation))
            bone.localRotation = localRotation;
    }

    [ContextMenu("Avatar Hand Driver/Capture Source Open Finger Curl")]
    public void CaptureSourceOpenFingerCurl()
    {
        CaptureSourceOpenFingerCurl(leftHand);
        CaptureSourceOpenFingerCurl(rightHand);
    }

    void CaptureSourceOpenFingerCurl(HandRig rig)
    {
        if (rig == null)
            return;

        Pose[] jointPoses = GetJointPosesFromHandDataSource(rig.handDataSource);
        if (!TryGetJointPoseLayout(jointPoses, out JointPoseLayout layout))
            return;

        float[] openAngles =
        {
            ComputeCurlAngleFromJointPoseChain(jointPoses, layout.thumb0, layout.thumb1, layout.thumb2, layout.thumb3, layout.thumbTip),
            ComputeCurlAngleFromJointPoseChain(jointPoses, layout.indexBase, layout.index2, layout.index3, layout.indexTip),
            ComputeCurlAngleFromJointPoseChain(jointPoses, layout.middleBase, layout.middle2, layout.middle3, layout.middleTip),
            ComputeCurlAngleFromJointPoseChain(jointPoses, layout.ringBase, layout.ring2, layout.ring3, layout.ringTip),
            ComputeCurlAngleFromJointPoseChain(jointPoses, layout.littleBase, layout.little2, layout.little3, layout.littleTip)
        };

        _jointOpenCurlAngles[rig] = openAngles;
        if (TryGetThumbPalmComponents(jointPoses, out Vector3 thumbOpen))
            _thumbOpenPalmComponents[rig] = thumbOpen;
        if (TryGetProximalSpreadComponents(rig, jointPoses, out Vector4 proximalSpreadOpen, out Vector3 proximalPalmRight, out Vector3 proximalPalmNormal, out _))
        {
            _openProximalSpreadComponents[rig] = proximalSpreadOpen;
            _openProximalSpreadPalmRights[rig] = proximalPalmRight;
            _openProximalSpreadPalmNormals[rig] = proximalPalmNormal;
        }
        if (TryGetWristLocalSegmentDirections(jointPoses, out Vector3[] segmentDirections))
            _jointOpenSegmentDirections[rig] = segmentDirections;
        rig.jointCurlDebug =
            $"captured {layout.name} open {openAngles[0]:0}/{openAngles[1]:0}/{openAngles[2]:0}/{openAngles[3]:0}/{openAngles[4]:0} " +
            $"thumbOpen {thumbOpen.x:0.00}/{thumbOpen.y:0.00}/{thumbOpen.z:0.00} " +
            $"spreadOpen {proximalSpreadOpen.x:0.00}/{proximalSpreadOpen.y:0.00}/{proximalSpreadOpen.z:0.00}/{proximalSpreadOpen.w:0.00}";
    }

    [ContextMenu("Avatar Hand Driver/Capture Wrist Rotation Offsets")]
    public void CaptureWristRotationOffsets()
    {
        CaptureWristRotationOffset(leftHand);
        CaptureWristRotationOffset(rightHand);
    }

    void DriveHand(HandRig rig)
    {
        if (rig == null || rig.hand == null)
            return;

        Transform wristSource = GetWristSource(rig);
        if (wristSource == null)
        {
            rig.sourceTracked = false;
            rig.wristSourceName = "None";
            return;
        }

        rig.sourceTracked = IsTracked(rig);
        if (!rig.sourceTracked && !(allowWristOverrideWithoutTracking && rig.wristSourceOverride != null))
            return;

        rig.wristSourceName = wristSource.name;
        ResetArmToBindPose(rig);

        Vector3 targetPosition = wristSource.position + wristSource.rotation * rig.wristPositionOffset;
        if (!Mathf.Approximately(rig.reachScale, 1f) && rig.upperArm != null)
            targetPosition = rig.upperArm.position + (targetPosition - rig.upperArm.position) * Mathf.Max(0.01f, rig.reachScale);
        Quaternion targetRotation = GetWristRotation(rig, wristSource);
        if (rig.useCalibratedWristRotationOffset)
            targetRotation *= rig.calibratedWristRotationOffset;
        targetRotation *= Quaternion.Euler(rig.wristEulerOffset);
        rig.wristTargetDistance = Vector3.Distance(rig.hand.position, targetPosition);
        Transform shoulderForDebug = rig.shoulder != null ? rig.shoulder : rig.upperArm;
        rig.shoulderToWristTargetDistance = shoulderForDebug != null
            ? Vector3.Distance(shoulderForDebug.position, targetPosition)
            : 0f;
        UpdateWristRotationEstimatedElbow(rig, targetPosition, targetRotation);

        if (solveArmIk && rig.upperArm != null && rig.lowerArm != null)
        {
            if (placeArmBetweenShoulderAndWrist)
                PlaceArmOnShoulderToWristSegment(rig, targetPosition, targetRotation);
            else
                SolveTwoBoneIk(rig, targetPosition, targetRotation);
        }
        else
            rig.hand.position = Vector3.Lerp(rig.hand.position, targetPosition, rig.positionWeight);

        ApplyForearmTwistFromWrist(rig, targetRotation);

        if (driveWristRotation)
            rig.hand.rotation = Quaternion.Slerp(rig.hand.rotation, targetRotation, rig.rotationWeight);

        if (driveFingerRotations)
            DriveFingers(rig);
    }

    public bool TryGetEstimatedElbow(bool left, out Vector3 elbowPosition)
    {
        HandRig rig = left ? leftHand : rightHand;
        elbowPosition = rig != null ? rig.estimatedElbowPosition : Vector3.zero;
        return rig != null && rig.estimatedElbowValid;
    }

    void UpdateWristRotationEstimatedElbow(HandRig rig, Vector3 wristPosition, Quaternion wristRotation)
    {
        if (rig == null)
            return;

        rig.estimatedElbowValid = false;
        if (!rig.useWristRotationEstimatedElbow)
            return;

        Vector3 localDirection = rig.wristToElbowLocalDirection;
        if (localDirection.sqrMagnitude < 1e-8f)
            localDirection = Vector3.back;

        float length = rig.wristToElbowLength;
        if (length <= 0.001f && rig.lowerArm != null && rig.hand != null)
            length = Vector3.Distance(rig.lowerArm.position, rig.hand.position);

        if (length <= 0.001f)
            return;

        Vector3 elbowPosition = wristPosition + wristRotation * localDirection.normalized * length;
        rig.estimatedElbowPosition = elbowPosition;
        rig.estimatedElbowValid = true;

        if (rig.estimatedElbowVisual != null)
            rig.estimatedElbowVisual.position = elbowPosition;
    }

    bool IsTracked(HandRig rig)
    {
        if (rig.ovrHand != null)
            return rig.ovrHand.IsTracked;

        return rig.ovrSkeleton != null && rig.ovrSkeleton.IsInitialized && rig.ovrSkeleton.Bones != null;
    }

    Transform GetWristSource(HandRig rig)
    {
        if (rig.wristSourceOverride != null)
            return rig.wristSourceOverride;

        Transform wrist = FindOvrBone(rig.ovrSkeleton, OVRSkeleton.BoneId.Hand_WristRoot);
        if (wrist != null)
            return wrist;

        return rig.ovrHand != null ? rig.ovrHand.transform : null;
    }

    Quaternion GetWristRotation(HandRig rig, Transform positionSource)
    {
        if (rig.wristRotationSourceOverride != null)
            return rig.wristRotationSourceOverride.rotation;

        Transform wrist = FindOvrBone(rig.ovrSkeleton, OVRSkeleton.BoneId.Hand_WristRoot);
        if (wrist != null)
            return wrist.rotation;

        if (rig.ovrHand != null)
            return rig.ovrHand.transform.rotation;

        return positionSource != null ? positionSource.rotation : Quaternion.identity;
    }

    void SolveTwoBoneIk(HandRig rig, Vector3 targetPosition, Quaternion targetRotation)
    {
        Vector3 root = rig.upperArm.position;
        Vector3 mid = rig.lowerArm.position;
        Vector3 end = rig.hand.position;

        Vector3 rootToTarget = targetPosition - root;
        if (rootToTarget.sqrMagnitude < 1e-8f)
            return;

        float upperLength = Mathf.Max(0.001f, Vector3.Distance(root, mid));
        float lowerLength = Mathf.Max(0.001f, Vector3.Distance(mid, end));
        float originalTotal = upperLength + lowerLength;
        float rawDistance = rootToTarget.magnitude;
        float distance = rawDistance;

        if (!stretchArmForRedirectedReach)
            distance = Mathf.Min(rawDistance, originalTotal * Mathf.Clamp01(maxIkReachScale));
        else if (rawDistance > originalTotal * maxIkReachScale)
        {
            float stretch = rawDistance / Mathf.Max(0.001f, originalTotal * maxIkReachScale);
            upperLength *= stretch;
            lowerLength *= stretch;
            distance = rawDistance;
        }

        Vector3 targetDir = rootToTarget.normalized;

        Vector3 poleDir = GetPoleDirection(rig, root, targetDir, targetRotation);
        float cosAngle = Mathf.Clamp(
            (upperLength * upperLength + distance * distance - lowerLength * lowerLength) /
            (2f * upperLength * distance),
            -1f,
            1f);
        float projected = Mathf.Cos(Mathf.Acos(cosAngle)) * upperLength;
        float height = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - projected * projected));
        float minVisualBend = distance * Mathf.Clamp(ikBendAmount, 0f, 0.45f);
        height = Mathf.Max(height, minVisualBend);

        Vector3 desiredMid = root + targetDir * projected + poleDir * height;
        Vector3 desiredEnd = root + targetDir * distance;
        desiredMid = BlendWristRotationElbowForArm(rig, desiredMid);

        Quaternion upperDelta = Quaternion.FromToRotation(mid - root, desiredMid - root);
        rig.upperArm.rotation = upperDelta * rig.upperArm.rotation;

        Quaternion lowerDelta = Quaternion.FromToRotation(end - rig.lowerArm.position, desiredEnd - rig.lowerArm.position);
        rig.lowerArm.rotation = lowerDelta * rig.lowerArm.rotation;

        Vector3 finalEnd = matchWristTargetExactly ? targetPosition : desiredEnd;
        rig.hand.position = Vector3.Lerp(rig.hand.position, finalEnd, rig.positionWeight);
    }

    void PlaceArmOnShoulderToWristSegment(HandRig rig, Vector3 targetPosition, Quaternion targetRotation)
    {
        Transform shoulderTransform = rig.shoulder != null ? rig.shoulder : rig.upperArm;
        Vector3 shoulderPosition = shoulderTransform.position;
        Vector3 shoulderToHand = targetPosition - shoulderPosition;
        if (shoulderToHand.sqrMagnitude < 1e-8f)
            return;

        Vector3 armDirection = shoulderToHand.normalized;
        Vector3 elbowPosition = Vector3.Lerp(
            shoulderPosition,
            targetPosition,
            Mathf.Clamp(rig.elbowPositionRatio, 0.05f, 0.95f));

        float bendOffset = rig.elbowBendOffset;
        if (scaleElbowBendWithReach)
            bendOffset += Mathf.Min(maxElbowBendOffset, shoulderToHand.magnitude * elbowBendReachRatio);

        if (Mathf.Abs(bendOffset) > 0.0001f)
            elbowPosition += GetPoleDirection(rig, shoulderPosition, armDirection, targetRotation) * bendOffset;
        elbowPosition = BlendWristRotationElbowForArm(rig, elbowPosition);

        RotateBoneToward(rig.upperArm, rig.lowerArm.position, elbowPosition);
        rig.lowerArm.position = Vector3.Lerp(rig.lowerArm.position, elbowPosition, rig.positionWeight);

        RotateBoneToward(rig.lowerArm, rig.hand.position, targetPosition);
        rig.hand.position = Vector3.Lerp(rig.hand.position, targetPosition, rig.positionWeight);
    }

    Vector3 BlendWristRotationElbowForArm(HandRig rig, Vector3 naturalElbowPosition)
    {
        if (rig == null || !rig.useWristRotationEstimatedElbow || !rig.estimatedElbowValid)
            return naturalElbowPosition;

        float weight = Mathf.Clamp01(rig.wristRotationElbowArmWeight);
        if (weight <= 0f)
            return naturalElbowPosition;

        Vector3 offset = rig.estimatedElbowPosition - naturalElbowPosition;
        float maxOffset = Mathf.Max(0f, rig.maxWristRotationElbowArmOffset);
        if (maxOffset > 0f && offset.magnitude > maxOffset)
            offset = offset.normalized * maxOffset;

        return Vector3.Lerp(naturalElbowPosition, naturalElbowPosition + offset, weight);
    }

    static void RotateBoneToward(Transform bone, Vector3 currentChildPosition, Vector3 desiredChildPosition)
    {
        Vector3 current = currentChildPosition - bone.position;
        Vector3 desired = desiredChildPosition - bone.position;
        if (current.sqrMagnitude < 1e-8f || desired.sqrMagnitude < 1e-8f)
            return;

        bone.rotation = Quaternion.FromToRotation(current, desired) * bone.rotation;
    }

    void ApplyForearmTwistFromWrist(HandRig rig, Quaternion targetWristRotation)
    {
        if (rig == null || !rig.twistForearmFromWrist || rig.lowerArm == null || rig.hand == null)
            return;

        Quaternion lastTwist = Quaternion.identity;
        bool hadLastTwist = _lastForearmTwists.TryGetValue(rig, out lastTwist);
        if (hadLastTwist)
            rig.lowerArm.rotation = Quaternion.Inverse(lastTwist) * rig.lowerArm.rotation;

        Vector3 twistAxis = rig.hand.position - rig.lowerArm.position;
        if (twistAxis.sqrMagnitude < 1e-8f)
        {
            if (hadLastTwist)
                rig.lowerArm.rotation = lastTwist * rig.lowerArm.rotation;
            return;
        }
        twistAxis.Normalize();

        Vector3 wristRefLocal = rig.wristTwistReferenceLocal.sqrMagnitude > 1e-6f
            ? rig.wristTwistReferenceLocal.normalized
            : Vector3.up;

        Vector3 targetRef = Vector3.ProjectOnPlane(targetWristRotation * wristRefLocal, twistAxis);
        if (targetRef.sqrMagnitude < 1e-8f)
        {
            if (hadLastTwist)
                rig.lowerArm.rotation = lastTwist * rig.lowerArm.rotation;
            return;
        }

        targetRef.Normalize();
        if (!_forearmNeutralWristRefs.TryGetValue(rig, out Vector3 neutralRef) || neutralRef.sqrMagnitude < 1e-8f)
        {
            neutralRef = targetRef;
            _forearmNeutralWristRefs[rig] = neutralRef;
        }

        neutralRef = Vector3.ProjectOnPlane(neutralRef, twistAxis);
        if (neutralRef.sqrMagnitude < 1e-8f)
        {
            _forearmNeutralWristRefs[rig] = targetRef;
            if (hadLastTwist)
                rig.lowerArm.rotation = lastTwist * rig.lowerArm.rotation;
            return;
        }
        neutralRef.Normalize();

        float targetAngle = Vector3.SignedAngle(neutralRef, targetRef, twistAxis) * rig.forearmTwistDirection + rig.forearmTwistOffsetDegrees;
        targetAngle = Mathf.Clamp(targetAngle, -Mathf.Abs(rig.maxForearmTwistDegrees), Mathf.Abs(rig.maxForearmTwistDegrees));
        targetAngle *= rig.forearmTwistWeight;

        _lastForearmTwistAngles.TryGetValue(rig, out float previousAngle);
        float maxStep = Mathf.Max(0f, rig.maxForearmTwistChangePerSecond) * Time.deltaTime;
        float steppedAngle = maxStep > 0f
            ? Mathf.MoveTowardsAngle(previousAngle, targetAngle, maxStep)
            : targetAngle;
        float smoothing = Mathf.Clamp01(rig.forearmTwistSmoothing);
        float angle = Mathf.LerpAngle(steppedAngle, previousAngle, smoothing);

        Quaternion twist = Quaternion.AngleAxis(angle, twistAxis);
        rig.lowerArm.rotation = twist * rig.lowerArm.rotation;
        _lastForearmTwists[rig] = twist;
        _lastForearmTwistAngles[rig] = angle;
    }

    Vector3 GetPoleDirection(HandRig rig, Vector3 root, Vector3 targetDir, Quaternion targetRotation)
    {
        Vector3 poleLocal = GetPoleDirectionLocal(rig);
        Vector3 pole = rig.poleHint != null
            ? rig.poleHint.position - root
            : avatarRoot.TransformDirection(poleLocal);
        if (rig.poleHint == null && rig.mirrorPoleX)
            pole = avatarRoot.TransformDirection(new Vector3(-poleLocal.x, poleLocal.y, poleLocal.z));

        if (rig.useWristRotationForPole && rig.poleHint == null)
        {
            Vector3 wristPoleLocal = rig.wristPoleDirectionLocal.sqrMagnitude > 1e-6f
                ? rig.wristPoleDirectionLocal.normalized
                : Vector3.down;
            Vector3 wristPole = targetRotation * wristPoleLocal;
            pole = Vector3.Slerp(pole.normalized, wristPole.normalized, rig.wristPoleWeight);
        }

        Vector3 projected = Vector3.ProjectOnPlane(pole, targetDir);
        if (projected.sqrMagnitude < 1e-6f)
            projected = Vector3.ProjectOnPlane(Vector3.up, targetDir);
        if (projected.sqrMagnitude < 1e-6f)
            projected = Vector3.ProjectOnPlane(Vector3.right, targetDir);

        return projected.normalized;
    }

    Vector3 GetPoleDirectionLocal(HandRig rig)
    {
        Vector3 local = rig.poleDirectionLocal;
        local.x += rig.poleSideOffsetX;
        return local;
    }

    void DriveFingers(HandRig rig)
    {
        if (rig.useJointPoseFingerCurl && DriveFingersFromJointPoses(rig))
            return;

        if (rig.useJointPoseSegmentDeltaRotations && DriveFingersFromJointPoseSegmentDeltas(rig))
            return;

        if (rig.ovrSkeleton == null || rig.ovrSkeleton.Bones == null)
        {
            if (usePinchStrengthFingerFallback)
                DriveFingersFromPinchStrength(rig);
            return;
        }

        CopyFinger(rig, OVRSkeleton.BoneId.Hand_Thumb1, rig.thumbProximal);
        CopyFinger(rig, OVRSkeleton.BoneId.Hand_Thumb2, rig.thumbIntermediate);
        CopyFinger(rig, OVRSkeleton.BoneId.Hand_Thumb3, rig.thumbDistal);
        CopyFinger(rig, OVRSkeleton.BoneId.Hand_Index1, rig.indexProximal);
        CopyFinger(rig, OVRSkeleton.BoneId.Hand_Index2, rig.indexIntermediate);
        CopyFinger(rig, OVRSkeleton.BoneId.Hand_Index3, rig.indexDistal);
        CopyFinger(rig, OVRSkeleton.BoneId.Hand_Middle1, rig.middleProximal);
        CopyFinger(rig, OVRSkeleton.BoneId.Hand_Middle2, rig.middleIntermediate);
        CopyFinger(rig, OVRSkeleton.BoneId.Hand_Middle3, rig.middleDistal);
        CopyFinger(rig, OVRSkeleton.BoneId.Hand_Ring1, rig.ringProximal);
        CopyFinger(rig, OVRSkeleton.BoneId.Hand_Ring2, rig.ringIntermediate);
        CopyFinger(rig, OVRSkeleton.BoneId.Hand_Ring3, rig.ringDistal);
        CopyFinger(rig, OVRSkeleton.BoneId.Hand_Pinky1, rig.littleProximal);
        CopyFinger(rig, OVRSkeleton.BoneId.Hand_Pinky2, rig.littleIntermediate);
        CopyFinger(rig, OVRSkeleton.BoneId.Hand_Pinky3, rig.littleDistal);
    }

    bool DriveFingersFromJointPoses(HandRig rig)
    {
        if (!TryGetFingerCurlsFromJointPoses(rig, out float thumb, out float index, out float middle, out float ring, out float little))
            return false;

        ApplyFingerCurlValues(rig, thumb, index, middle, ring, little, "HandDataAsset.JointPoses");
        return true;
    }

    bool DriveFingersFromJointPoseSegmentDeltas(HandRig rig)
    {
        Pose[] jointPoses = GetJointPosesFromHandDataSource(rig.handDataSource);
        if (jointPoses == null || jointPoses.Length < 26)
        {
            rig.segmentDeltaDebug = jointPoses == null ? "segment delta: JointPoses null" : $"segment delta: JointPoses length {jointPoses.Length}";
            return false;
        }

        if (!TryGetWristLocalSegmentDirections(jointPoses, out Vector3[] currentDirections))
        {
            rig.segmentDeltaDebug = "segment delta: source directions unavailable";
            return false;
        }

        Vector3[] openDirections = GetOrCaptureOpenSegmentDirections(rig, currentDirections);
        if (openDirections == null || openDirections.Length != currentDirections.Length)
        {
            rig.segmentDeltaDebug = "segment delta: open directions unavailable";
            return false;
        }
        UpdateSegmentDeltaFourFingerSpreadDebug(rig, openDirections, currentDirections);

        ApplySegmentDeltaChain(rig, openDirections, currentDirections, 0, rig.thumbProximal, rig.thumbIntermediate, rig.thumbDistal);
        ApplySegmentDeltaChain(rig, openDirections, currentDirections, 3, rig.indexProximal, rig.indexIntermediate, rig.indexDistal);
        ApplySegmentDeltaChain(rig, openDirections, currentDirections, 6, rig.middleProximal, rig.middleIntermediate, rig.middleDistal);
        ApplySegmentDeltaChain(rig, openDirections, currentDirections, 9, rig.ringProximal, rig.ringIntermediate, rig.ringDistal);
        ApplySegmentDeltaChain(rig, openDirections, currentDirections, 12, rig.littleProximal, rig.littleIntermediate, rig.littleDistal);

        rig.fingerInputSource = "HandDataAsset.SegmentDelta";
        rig.segmentDeltaDebug = "segment delta applied";
        return true;
    }

    void CopyFinger(HandRig rig, OVRSkeleton.BoneId sourceBoneId, Transform target)
    {
        if (target == null)
            return;

        Transform source = FindOvrBone(rig.ovrSkeleton, sourceBoneId);
        if (source == null)
            return;

        if (!_sourceBindRotations.TryGetValue(source, out Quaternion sourceBind) ||
            !_targetBindRotations.TryGetValue(target, out Quaternion targetBind))
        {
            sourceBind = source.localRotation;
            targetBind = target.localRotation;
            _sourceBindRotations[source] = sourceBind;
            _targetBindRotations[target] = targetBind;
        }

        Quaternion delta = Quaternion.Inverse(sourceBind) * source.localRotation;
        Quaternion desired = targetBind * delta;
        target.localRotation = Quaternion.Slerp(target.localRotation, desired, rig.fingerWeight);
    }

    void DriveFingersFromPinchStrength(HandRig rig)
    {
        rig.proximalSpreadInput = Vector4.zero;

        float thumb = 0f;
        float index = 0f;
        float middle = 0f;
        float ring = 0f;
        float little = 0f;

        thumb = GetFingerStrength(rig, OVRHand.HandFinger.Thumb);
        index = GetFingerStrength(rig, OVRHand.HandFinger.Index);
        middle = GetFingerStrength(rig, OVRHand.HandFinger.Middle);
        ring = GetFingerStrength(rig, OVRHand.HandFinger.Ring);
        little = GetFingerStrength(rig, OVRHand.HandFinger.Pinky);

        ApplyFingerCurlValues(rig, thumb, index, middle, ring, little, "PinchStrength fallback");
    }

    static readonly int[,] SegmentDeltaSourcePairs =
    {
        { 2, 3 }, { 3, 4 }, { 4, 5 },
        { 6, 7 }, { 7, 8 }, { 8, 9 },
        { 11, 12 }, { 12, 13 }, { 13, 14 },
        { 16, 17 }, { 17, 18 }, { 18, 19 },
        { 21, 22 }, { 22, 23 }, { 23, 24 }
    };

    bool TryGetWristLocalSegmentDirections(Pose[] jointPoses, out Vector3[] directions)
    {
        directions = null;
        if (jointPoses == null || jointPoses.Length < 26)
            return false;

        Quaternion wristRotation = jointPoses[1].rotation;
        directions = new Vector3[SegmentDeltaSourcePairs.GetLength(0)];
        for (int i = 0; i < directions.Length; i++)
        {
            int from = SegmentDeltaSourcePairs[i, 0];
            int to = SegmentDeltaSourcePairs[i, 1];
            Vector3 worldDirection = jointPoses[to].position - jointPoses[from].position;
            if (worldDirection.sqrMagnitude < 1e-8f)
                return false;

            Vector3 wristLocal = Quaternion.Inverse(wristRotation) * worldDirection.normalized;
            if (wristLocal.sqrMagnitude < 1e-8f)
                return false;

            directions[i] = wristLocal.normalized;
        }

        return true;
    }

    Vector3[] GetOrCaptureOpenSegmentDirections(HandRig rig, Vector3[] currentDirections)
    {
        if (_jointOpenSegmentDirections.TryGetValue(rig, out Vector3[] openDirections) &&
            openDirections != null &&
            openDirections.Length == currentDirections.Length)
        {
            return openDirections;
        }

        if (!rig.autoCalibrateSegmentDeltaOpenPose || currentDirections == null)
            return null;

        openDirections = new Vector3[currentDirections.Length];
        Array.Copy(currentDirections, openDirections, currentDirections.Length);
        _jointOpenSegmentDirections[rig] = openDirections;
        return openDirections;
    }

    void UpdateSegmentDeltaFourFingerSpreadDebug(HandRig rig, Vector3[] openDirections, Vector3[] currentDirections)
    {
        if (rig == null || openDirections == null || currentDirections == null || currentDirections.Length < 13)
            return;

        Vector3 axisSign = NormalizeAxisSigns(rig.segmentDeltaAxisSign);
        rig.segmentDeltaFourFingerSpreadDebug = new Vector4(
            Vector3.Scale(currentDirections[3], axisSign).x - Vector3.Scale(openDirections[3], axisSign).x,
            Vector3.Scale(currentDirections[6], axisSign).x - Vector3.Scale(openDirections[6], axisSign).x,
            Vector3.Scale(currentDirections[9], axisSign).x - Vector3.Scale(openDirections[9], axisSign).x,
            Vector3.Scale(currentDirections[12], axisSign).x - Vector3.Scale(openDirections[12], axisSign).x);
    }

    void ApplySegmentDeltaChain(
        HandRig rig,
        Vector3[] openDirections,
        Vector3[] currentDirections,
        int startIndex,
        Transform proximal,
        Transform intermediate,
        Transform distal)
    {
        ApplySegmentDeltaToBone(rig, openDirections[startIndex], currentDirections[startIndex], proximal);
        ApplySegmentDeltaToBone(rig, openDirections[startIndex + 1], currentDirections[startIndex + 1], intermediate);
        ApplySegmentDeltaToBone(rig, openDirections[startIndex + 2], currentDirections[startIndex + 2], distal);
    }

    void ApplySegmentDeltaToBone(HandRig rig, Vector3 openDirection, Vector3 currentDirection, Transform bone)
    {
        if (bone == null)
            return;

        if (!_targetBindRotations.TryGetValue(bone, out Quaternion bindRotation))
        {
            bindRotation = bone.localRotation;
            _targetBindRotations[bone] = bindRotation;
        }

        Vector3 axisSign = NormalizeAxisSigns(rig.segmentDeltaAxisSign);
        Vector3 open = Vector3.Scale(openDirection, axisSign).normalized;
        Vector3 current = Vector3.Scale(currentDirection, axisSign).normalized;
        Quaternion delta = Quaternion.FromToRotation(open, current);
        delta.ToAngleAxis(out float angle, out Vector3 axis);
        if (float.IsNaN(axis.x) || axis.sqrMagnitude < 1e-8f)
        {
            bone.localRotation = Quaternion.Slerp(bone.localRotation, bindRotation, rig.segmentDeltaRotationWeight);
            return;
        }

        if (angle > 180f)
            angle -= 360f;
        angle = Mathf.Clamp(angle, -rig.segmentDeltaMaxDegreesPerFrame, rig.segmentDeltaMaxDegreesPerFrame) * rig.segmentDeltaRotationDirection;
        Quaternion limitedDelta = Quaternion.AngleAxis(angle, axis.normalized);
        Quaternion handRotation = rig.hand != null ? rig.hand.rotation : Quaternion.identity;
        Quaternion parentRotation = bone.parent != null ? bone.parent.rotation : Quaternion.identity;
        Quaternion worldDelta = handRotation * limitedDelta * Quaternion.Inverse(handRotation);
        Quaternion parentLocalDelta = Quaternion.Inverse(parentRotation) * worldDelta * parentRotation;
        Quaternion desired = parentLocalDelta * bindRotation;
        bone.localRotation = Quaternion.Slerp(bone.localRotation, desired, rig.segmentDeltaRotationWeight);
    }

    void ApplyFingerCurlValues(HandRig rig, float thumb, float index, float middle, float ring, float little, string inputSource)
    {
        rig.fingerInputSource = inputSource;
        float fist = 0f;

        rig.thumbStrength = new Vector2(thumb, Mathf.Lerp(rig.thumbOpenCurl, rig.thumbClosedCurl, thumb));
        float indexCurl = ComputeFingerCurl(rig, index, fist);
        float middleCurl = ComputeFingerCurl(rig, middle, fist);
        float ringCurl = ComputeFingerCurl(rig, ring, fist);
        float littleCurl = ComputeFingerCurl(rig, little, fist);
        rig.indexStrength = new Vector2(index, indexCurl);
        rig.middleStrength = new Vector2(middle, middleCurl);
        rig.ringStrength = new Vector2(ring, ringCurl);
        rig.littleStrength = new Vector2(little, littleCurl);

        float indexSpreadGate = ComputeOpenHandSpreadGate(index);
        float middleSpreadGate = ComputeOpenHandSpreadGate(middle);
        float ringSpreadGate = ComputeOpenHandSpreadGate(ring);
        float littleSpreadGate = ComputeOpenHandSpreadGate(little);

        ApplyThumbCurl(rig, Mathf.Lerp(rig.thumbOpenCurl, rig.thumbClosedCurl, thumb), rig.fingerWeight);
        ApplyFingerCurl(rig, rig.indexProximal, rig.indexIntermediate, rig.indexDistal,
            indexCurl, rig.fingerWeight, rig.fingerOpenExtensionDegrees,
            rig.proximalSpreadInput.x * rig.indexProximalSpreadDegrees * indexSpreadGate);
        ApplyFingerCurl(rig, rig.middleProximal, rig.middleIntermediate, rig.middleDistal,
            middleCurl, rig.fingerWeight, rig.fingerOpenExtensionDegrees,
            rig.proximalSpreadInput.y * rig.middleProximalSpreadDegrees * middleSpreadGate);
        ApplyFingerCurl(rig, rig.ringProximal, rig.ringIntermediate, rig.ringDistal,
            ringCurl, rig.fingerWeight, rig.fingerOpenExtensionDegrees,
            rig.proximalSpreadInput.z * rig.ringProximalSpreadDegrees * ringSpreadGate);
        ApplyFingerCurl(rig, rig.littleProximal, rig.littleIntermediate, rig.littleDistal,
            littleCurl, rig.fingerWeight, rig.fingerOpenExtensionDegrees,
            rig.proximalSpreadInput.w * rig.littleProximalSpreadDegrees * littleSpreadGate);
    }

    static float ComputeOpenHandSpreadGate(float fingerStrength)
    {
        float closed = Mathf.Clamp01(fingerStrength);
        float smoothClosed = closed * closed * (3f - 2f * closed);
        return 1f - smoothClosed;
    }

    float ComputeFingerCurl(HandRig rig, float fingerStrength, float fistStrength)
    {
        float boosted = Mathf.Clamp01(fingerStrength + fistStrength * rig.fistCurlBoost);
        boosted = Mathf.Pow(boosted, Mathf.Max(0.1f, rig.fingerCurlResponse));
        return Mathf.Lerp(rig.fingerOpenCurl, rig.fingerClosedCurl, boosted);
    }

    bool TryGetFingerCurlsFromJointPoses(
        HandRig rig,
        out float thumb,
        out float index,
        out float middle,
        out float ring,
        out float little)
    {
        thumb = index = middle = ring = little = 0f;

        Pose[] jointPoses = GetJointPosesFromHandDataSource(rig.handDataSource);
        if (!TryGetJointPoseLayout(jointPoses, out JointPoseLayout layout))
            return false;

        float[] rawAngles = new float[5];
        rawAngles[0] = ComputeCurlAngleFromJointPoseChain(jointPoses, layout.thumb0, layout.thumb1, layout.thumb2, layout.thumb3, layout.thumbTip);
        rawAngles[1] = ComputeCurlAngleFromJointPoseChain(jointPoses, layout.indexBase, layout.index2, layout.index3, layout.indexTip);
        rawAngles[2] = ComputeCurlAngleFromJointPoseChain(jointPoses, layout.middleBase, layout.middle2, layout.middle3, layout.middleTip);
        rawAngles[3] = ComputeCurlAngleFromJointPoseChain(jointPoses, layout.ringBase, layout.ring2, layout.ring3, layout.ringTip);
        rawAngles[4] = ComputeCurlAngleFromJointPoseChain(jointPoses, layout.littleBase, layout.little2, layout.little3, layout.littleTip);

        float[] openAngles = GetOrCaptureOpenCurlAngles(rig, rawAngles);
        thumb = NormalizeJointCurl(rig, rawAngles[0], openAngles[0]);
        index = NormalizeJointCurl(rig, rawAngles[1], openAngles[1]);
        middle = NormalizeJointCurl(rig, rawAngles[2], openAngles[2]);
        ring = NormalizeJointCurl(rig, rawAngles[3], openAngles[3]);
        little = NormalizeJointCurl(rig, rawAngles[4], openAngles[4]);
        rig.thumbBallInput = rig.useThumbBallFromJointPose
            ? ComputeThumbBallInput(rig, jointPoses)
            : Vector3.zero;
        rig.proximalSpreadInput = rig.useJointPoseProximalSpread
            ? ComputeProximalSpreadInput(rig, jointPoses)
            : Vector4.zero;

        rig.jointCurlDebug =
            $"{layout.name} raw {rawAngles[0]:0}/{rawAngles[1]:0}/{rawAngles[2]:0}/{rawAngles[3]:0}/{rawAngles[4]:0} " +
            $"open {openAngles[0]:0}/{openAngles[1]:0}/{openAngles[2]:0}/{openAngles[3]:0}/{openAngles[4]:0} " +
            $"thumbBall {rig.thumbBallInput.x:0.00}/{rig.thumbBallInput.y:0.00}/{rig.thumbBallInput.z:0.00} " +
            $"spread {rig.proximalSpreadInput.x:0.00}/{rig.proximalSpreadInput.y:0.00}/{rig.proximalSpreadInput.z:0.00}/{rig.proximalSpreadInput.w:0.00}";

        return true;
    }

    static bool TryGetJointPoseLayout(Pose[] poses, out JointPoseLayout layout)
    {
        layout = default;
        if (poses == null)
            return false;

        if (poses.Length >= 26)
        {
            layout = new JointPoseLayout
            {
                name = "OpenXR26",
                wrist = 1,
                thumb0 = 2,
                thumb1 = 3,
                thumb2 = 4,
                thumb3 = 5,
                thumbTip = 5,
                indexBase = 7,
                index2 = 8,
                index3 = 9,
                indexTip = 10,
                middleBase = 12,
                middle2 = 13,
                middle3 = 14,
                middleTip = 15,
                ringBase = 17,
                ring2 = 18,
                ring3 = 19,
                ringTip = 20,
                littleBase = 22,
                little2 = 23,
                little3 = 24,
                littleTip = 25
            };
            return true;
        }

        if (poses.Length >= 24)
        {
            layout = new JointPoseLayout
            {
                name = "OVR24",
                wrist = 0,
                thumb0 = 2,
                thumb1 = 3,
                thumb2 = 4,
                thumb3 = 5,
                thumbTip = 19,
                indexBase = 6,
                index2 = 7,
                index3 = 8,
                indexTip = 20,
                middleBase = 9,
                middle2 = 10,
                middle3 = 11,
                middleTip = 21,
                ringBase = 12,
                ring2 = 13,
                ring3 = 14,
                ringTip = 22,
                littleBase = 16,
                little2 = 17,
                little3 = 18,
                littleTip = 23
            };
            return true;
        }

        return false;
    }

    Vector4 ComputeProximalSpreadInput(HandRig rig, Pose[] poses)
    {
        if (!TryGetProximalSpreadComponents(rig, poses, out Vector4 current, out Vector3 palmRight, out Vector3 palmNormal, out string layoutName))
        {
            rig.proximalSpreadDebug = "spread: source unavailable";
            rig.proximalSpreadPalmNormal = Vector3.zero;
            return Vector4.zero;
        }
        rig.proximalSpreadPalmNormal = palmNormal;

        if (!_openProximalSpreadComponents.TryGetValue(rig, out Vector4 open))
        {
            open = rig.autoCalibrateProximalSpreadOpenPose ? current : Vector4.zero;
            _openProximalSpreadComponents[rig] = open;
            _openProximalSpreadPalmRights[rig] = palmRight;
            _openProximalSpreadPalmNormals[rig] = palmNormal;
        }

        float handSign =
            (rig.isLeft && rig.mirrorLeftProximalSpread) ||
            (!rig.isLeft && rig.mirrorRightProximalSpread)
                ? -1f
                : 1f;
        Vector4 delta = (current - open) * rig.proximalSpreadDirection * handSign;
        float scale = Mathf.Max(1f, rig.proximalSpreadInputScale);
        Vector4 fingerScale = NormalizeSpreadFingerScale(rig.proximalSpreadFingerScale);
        delta.x = ApplyDeadZone(Mathf.Clamp(delta.x * scale * fingerScale.x + rig.proximalSpreadInputOffset.x, -1f, 1f), rig.proximalSpreadDeadZone);
        delta.y = ApplyDeadZone(Mathf.Clamp(delta.y * scale * fingerScale.y + rig.proximalSpreadInputOffset.y, -1f, 1f), rig.proximalSpreadDeadZone);
        delta.z = ApplyDeadZone(Mathf.Clamp(delta.z * scale * fingerScale.z + rig.proximalSpreadInputOffset.z, -1f, 1f), rig.proximalSpreadDeadZone);
        delta.w = ApplyDeadZone(Mathf.Clamp(delta.w * scale * fingerScale.w + rig.proximalSpreadInputOffset.w, -1f, 1f), rig.proximalSpreadDeadZone);

        rig.proximalSpreadDebug =
            $"{layoutName} current {current.x:0.00}/{current.y:0.00}/{current.z:0.00}/{current.w:0.00} " +
            $"open {open.x:0.00}/{open.y:0.00}/{open.z:0.00}/{open.w:0.00} " +
            $"axis {rig.proximalSpreadPalmNormal.x:0.00}/{rig.proximalSpreadPalmNormal.y:0.00}/{rig.proximalSpreadPalmNormal.z:0.00} " +
            $"sign {handSign:0}";
        return delta;
    }

    Vector3 GetWristFixedProximalSpreadPalmNormal(HandRig rig, Vector3 currentPalmNormal)
    {
        if (rig == null)
            return currentPalmNormal;

        if (!_openProximalSpreadPalmNormals.TryGetValue(rig, out Vector3 fixedPalmNormal) ||
            fixedPalmNormal.sqrMagnitude < 1e-6f)
        {
            fixedPalmNormal = currentPalmNormal;
            _openProximalSpreadPalmNormals[rig] = fixedPalmNormal;
        }

        if (fixedPalmNormal.sqrMagnitude < 1e-6f)
            fixedPalmNormal = rig.proximalSpreadAxis.sqrMagnitude > 1e-6f
                ? rig.proximalSpreadAxis.normalized
                : Vector3.up;
        else
            fixedPalmNormal.Normalize();

        return rig.invertProximalSpreadPalmAxis ? -fixedPalmNormal : fixedPalmNormal;
    }

    static Vector4 NormalizeSpreadFingerScale(Vector4 scale)
    {
        if (scale == Vector4.zero)
            return Vector4.one;

        scale.x = Mathf.Approximately(scale.x, 0f) ? 1f : scale.x;
        scale.y = Mathf.Approximately(scale.y, 0f) ? 1f : scale.y;
        scale.z = Mathf.Approximately(scale.z, 0f) ? 1f : scale.z;
        scale.w = Mathf.Approximately(scale.w, 0f) ? 1f : scale.w;
        return scale;
    }

    bool TryGetProximalSpreadComponents(Pose[] poses, out Vector4 components)
    {
        return TryGetProximalSpreadComponents(null, poses, out components, out _, out _, out _);
    }

    bool TryGetProximalSpreadComponents(
        HandRig rig,
        Pose[] poses,
        out Vector4 components,
        out Vector3 palmRight,
        out Vector3 palmNormal,
        out string layoutName)
    {
        components = Vector4.zero;
        palmRight = Vector3.zero;
        palmNormal = Vector3.zero;
        layoutName = "";
        if (!TryGetJointPoseLayout(poses, out JointPoseLayout layout))
            return false;
        layoutName = layout.name;

        Vector3 wrist = poses[layout.wrist].position;
        Vector3 indexBase = poses[layout.indexBase].position;
        Vector3 middleBase = poses[layout.middleBase].position;
        Vector3 littleBase = poses[layout.littleBase].position;

        palmRight = indexBase - littleBase;
        Vector3 palmForward = middleBase - wrist;
        if (palmRight.sqrMagnitude < 1e-8f || palmForward.sqrMagnitude < 1e-8f)
            return false;

        palmRight.Normalize();
        palmForward = Vector3.ProjectOnPlane(palmForward, palmRight);
        if (palmForward.sqrMagnitude < 1e-8f)
            return false;
        palmForward.Normalize();

        palmNormal = Vector3.Cross(palmRight, palmForward);
        if (palmNormal.sqrMagnitude < 1e-8f)
            return false;
        palmNormal.Normalize();
        // Do not stabilize palmRight against the open pose in world space.
        // A wrist roll / palm flip legitimately rotates palmRight in world space,
        // and forcing it back to the open-pose world direction makes spread input invert.

        // Use the first finger segment for spread estimation.  Tip-based vectors
        // become noisy when the hand is a fist because fingertips are occluded and
        // folded into the palm, which can feed jitter back into proximal spread.
        if (!TryGetPalmRightComponent(poses, layout.indexBase, layout.index2, palmRight, out float index) ||
            !TryGetPalmRightComponent(poses, layout.middleBase, layout.middle2, palmRight, out float middle) ||
            !TryGetPalmRightComponent(poses, layout.ringBase, layout.ring2, palmRight, out float ring) ||
            !TryGetPalmRightComponent(poses, layout.littleBase, layout.little2, palmRight, out float little))
            return false;

        components = new Vector4(index, middle, ring, little);
        return true;
    }

    void StabilizeProximalSpreadPalmRight(HandRig rig, ref Vector3 palmRight)
    {
        if (rig == null)
            return;

        if (_openProximalSpreadPalmRights.TryGetValue(rig, out Vector3 openPalmRight) &&
            openPalmRight.sqrMagnitude > 1e-6f &&
            Vector3.Dot(palmRight, openPalmRight) < 0f)
        {
            palmRight = -palmRight;
        }
    }

    static bool TryGetPalmRightComponent(Pose[] poses, int from, int to, Vector3 palmRight, out float component)
    {
        component = 0f;
        Vector3 direction = poses[to].position - poses[from].position;
        if (direction.sqrMagnitude < 1e-8f)
            return false;

        direction.Normalize();
        component = Vector3.Dot(direction, palmRight);
        return true;
    }

    Vector3 ComputeThumbBallInput(HandRig rig, Pose[] poses)
    {
        if (!TryGetThumbPalmComponents(poses, out Vector3 current))
            return Vector3.zero;

        if (!_thumbOpenPalmComponents.TryGetValue(rig, out Vector3 open))
        {
            open = current;
            _thumbOpenPalmComponents[rig] = open;
        }

        Vector3 delta = current - open;
        float handSign = rig != null && rig.isLeft ? 1f : -1f;
        float spread = ApplyDeadZone(Mathf.Clamp(delta.x * handSign * rig.thumbSpreadDirection * 3.5f, -1f, 1f), rig.thumbBallDeadZone);
        float outOfPalm = ApplyDeadZone(Mathf.Clamp(delta.z * rig.thumbOutOfPalmDirection * 3.5f, -1f, 1f), rig.thumbBallDeadZone);
        return new Vector3(spread, outOfPalm, 0f);
    }

    static float ApplyDeadZone(float value, float deadZone)
    {
        deadZone = Mathf.Clamp01(deadZone);
        float abs = Mathf.Abs(value);
        if (abs <= deadZone)
            return 0f;

        return Mathf.Sign(value) * Mathf.InverseLerp(deadZone, 1f, abs);
    }

    bool TryGetThumbPalmComponents(Pose[] poses, out Vector3 components)
    {
        components = Vector3.zero;
        if (!TryGetJointPoseLayout(poses, out JointPoseLayout layout))
            return false;

        Vector3 wrist = poses[layout.wrist].position;
        Vector3 indexBase = poses[layout.indexBase].position;
        Vector3 littleBase = poses[layout.littleBase].position;
        Vector3 middleBase = poses[layout.middleBase].position;
        Vector3 thumbBase = poses[layout.thumb0].position;
        Vector3 thumbTip = poses[layout.thumbTip].position;

        Vector3 palmRight = indexBase - littleBase;
        Vector3 palmForward = middleBase - wrist;
        Vector3 thumbDir = thumbTip - thumbBase;
        if (palmRight.sqrMagnitude < 1e-8f || palmForward.sqrMagnitude < 1e-8f || thumbDir.sqrMagnitude < 1e-8f)
            return false;

        palmRight.Normalize();
        palmForward = Vector3.ProjectOnPlane(palmForward, palmRight);
        if (palmForward.sqrMagnitude < 1e-8f)
            return false;
        palmForward.Normalize();

        Vector3 palmNormal = Vector3.Cross(palmRight, palmForward);
        if (palmNormal.sqrMagnitude < 1e-8f)
            return false;
        palmNormal.Normalize();

        thumbDir.Normalize();
        components = new Vector3(
            Vector3.Dot(thumbDir, palmRight),
            Vector3.Dot(thumbDir, palmForward),
            Vector3.Dot(thumbDir, palmNormal));
        return true;
    }

    float ComputeCurlAngleFromJointPoseChain(Pose[] poses, params int[] jointIndices)
    {
        if (jointIndices == null || jointIndices.Length < 3)
            return 0f;

        float angleSum = 0f;
        int count = 0;
        for (int i = 0; i < jointIndices.Length - 2; i++)
        {
            Vector3 a = poses[jointIndices[i]].position;
            Vector3 b = poses[jointIndices[i + 1]].position;
            Vector3 c = poses[jointIndices[i + 2]].position;

            Vector3 ab = b - a;
            Vector3 bc = c - b;
            if (ab.sqrMagnitude < 1e-8f || bc.sqrMagnitude < 1e-8f)
                continue;

            angleSum += Vector3.Angle(ab, bc);
            count++;
        }

        if (count == 0)
            return 0f;

        return angleSum / count;
    }

    float[] GetOrCaptureOpenCurlAngles(HandRig rig, float[] rawAngles)
    {
        if (!_jointOpenCurlAngles.TryGetValue(rig, out float[] openAngles) ||
            openAngles == null ||
            openAngles.Length != 5)
        {
            openAngles = new float[5];
            if (rig.autoCalibrateJointCurlOpenPose)
            {
                Array.Copy(rawAngles, openAngles, 5);
            }
            else
            {
                for (int i = 0; i < openAngles.Length; i++)
                    openAngles[i] = rig.jointCurlOpenAngle;
            }

            _jointOpenCurlAngles[rig] = openAngles;
        }

        return openAngles;
    }

    float NormalizeJointCurl(HandRig rig, float rawAngle, float openAngle)
    {
        float closedDelta = Mathf.Max(1f, rig.jointCurlClosedDeltaAngle);
        float normalized = Mathf.Abs(rawAngle - openAngle) / closedDelta;
        return Mathf.Pow(Mathf.Clamp01(normalized), Mathf.Max(0.1f, rig.jointCurlResponse));
    }

    float GetFingerStrength(HandRig rig, OVRHand.HandFinger finger)
    {
        float fromDataSource;
        if (TryGetFingerStrengthFromHandDataSource(rig.handDataSource, finger, out fromDataSource))
            return fromDataSource;

        if (rig.ovrHand != null)
            return Mathf.Clamp01(rig.ovrHand.GetFingerPinchStrength(finger));

        return 0f;
    }

    void ApplyFingerCurl(HandRig rig, Transform proximal, Transform intermediate, Transform distal, float curl, float weight, float openExtensionDegrees, float proximalSpreadDegrees)
    {
        float openExtension = openExtensionDegrees * (1f - Mathf.Clamp01(curl));
        float extensionSign = GetRightMirrorSign(rig, rig.mirrorRightOpenExtension);
        ApplyLocalCurl(rig, proximal, curl * 78f, weight, openExtension, extensionSign, proximalSpreadDegrees);
        ApplyLocalCurl(rig, intermediate, curl * 96f, weight, openExtension, extensionSign, 0f);
        ApplyLocalCurl(rig, distal, curl * 65f * rig.distalCurlMultiplier, weight, openExtension, extensionSign, 0f);
    }

    void ApplyThumbCurl(HandRig rig, float curl, float weight)
    {
        float clamped = Mathf.Clamp01(curl);
        float openExtension = rig.thumbOpenExtensionDegrees * (1f - clamped);

        ApplyLocalThumbCurl(rig, rig.thumbProximal,
            clamped * rig.thumbCurlDegrees,
            clamped * rig.thumbOppositionDegrees,
            rig.thumbBallInput.x * rig.thumbSpreadDegrees,
            rig.thumbBallInput.y * rig.thumbOppositionDegrees,
            weight,
            openExtension,
            true);
        ApplyLocalThumbCurl(rig, rig.thumbIntermediate,
            clamped * rig.thumbCurlDegrees * 0.85f,
            clamped * rig.thumbOppositionDegrees * 0.45f,
            rig.thumbBallInput.x * rig.thumbSpreadDegrees * 0.25f,
            rig.thumbBallInput.y * rig.thumbOppositionDegrees * 0.25f,
            weight,
            openExtension * 0.45f,
            false);
        ApplyLocalThumbCurl(rig, rig.thumbDistal,
            clamped * rig.thumbCurlDegrees * 0.7f,
            0f,
            0f,
            0f,
            weight,
            openExtension * 0.25f,
            false);
    }

    void ApplyLocalCurl(HandRig rig, Transform bone, float degrees, float weight, float openExtensionDegrees, float extensionSign, float spreadDegrees)
    {
        if (bone == null)
            return;

        if (!_targetBindRotations.TryGetValue(bone, out Quaternion open))
        {
            open = bone.localRotation;
            _targetBindRotations[bone] = open;
        }

        Vector3 axis = rig.fingerCurlAxis.sqrMagnitude > 1e-6f ? rig.fingerCurlAxis.normalized : Vector3.right;
        float correctedDegrees = degrees - openExtensionDegrees * extensionSign;
        Quaternion curlRotation = Quaternion.AngleAxis(correctedDegrees * rig.fingerCurlDirection, axis);
        Quaternion spreadRotation = GetPalmNormalSpreadRotation(rig, bone, spreadDegrees * rig.proximalSpreadWeight);
        Quaternion desired = spreadRotation * open * curlRotation;
        bone.localRotation = Quaternion.Slerp(bone.localRotation, desired, weight);
    }

    Quaternion GetPalmNormalSpreadRotation(HandRig rig, Transform bone, float spreadDegrees)
    {
        if (Mathf.Approximately(spreadDegrees, 0f))
            return Quaternion.identity;

        if (!TryGetProximalSpreadWorldAxis(rig, bone, out Vector3 worldAxis))
            return Quaternion.identity;

        Quaternion worldRotation = Quaternion.AngleAxis(spreadDegrees, worldAxis);
        Quaternion parentRotation = bone.parent != null ? bone.parent.rotation : Quaternion.identity;
        return Quaternion.Inverse(parentRotation) * worldRotation * parentRotation;
    }

    bool TryGetProximalSpreadWorldAxis(HandRig rig, Transform bone, out Vector3 worldAxis)
    {
        worldAxis = Vector3.zero;
        if (bone == null)
            return false;

        if (rig != null && rig.hand != null)
        {
            if (!_avatarProximalSpreadAxisLocal.TryGetValue(rig, out Vector3 localAxis) || localAxis.sqrMagnitude < 1e-6f)
            {
                CaptureAvatarProximalSpreadAxisLocal(rig);
                _avatarProximalSpreadAxisLocal.TryGetValue(rig, out localAxis);
            }

            if (localAxis.sqrMagnitude > 1e-6f)
                worldAxis = rig.hand.rotation * localAxis.normalized;
        }

        if (worldAxis.sqrMagnitude < 1e-6f && TryComputeAvatarPalmNormalWorld(rig, out Vector3 palmNormal))
            worldAxis = palmNormal;

        if (worldAxis.sqrMagnitude < 1e-6f && rig != null && rig.hand != null)
        {
            Vector3 fallbackAxis = rig.proximalSpreadAxis.sqrMagnitude > 1e-6f
                ? rig.proximalSpreadAxis.normalized
                : Vector3.up;
            worldAxis = rig.hand.rotation * fallbackAxis;
        }

        if (worldAxis.sqrMagnitude < 1e-6f)
            return false;

        worldAxis.Normalize();
        if (rig != null && rig.invertProximalSpreadPalmAxis)
            worldAxis = -worldAxis;

        // Safety guard: abduction/adduction at the finger root must rotate around
        // a palm-normal-like axis, not around the finger's length axis.  If the
        // imported rig's local axes or an unlucky fallback makes the axis almost
        // parallel to the finger, project it away from the finger direction.
        if (TryGetFingerWorldDirection(bone, out Vector3 fingerDirection))
        {
            Vector3 correctedAxis = Vector3.ProjectOnPlane(worldAxis, fingerDirection);
            if (correctedAxis.sqrMagnitude < 1e-6f && TryGetAvatarPalmRightWorld(rig, out Vector3 palmRight))
                correctedAxis = Vector3.Cross(palmRight, fingerDirection);
            if (correctedAxis.sqrMagnitude < 1e-6f && rig != null && rig.hand != null)
                correctedAxis = Vector3.Cross(rig.hand.right, fingerDirection);
            if (correctedAxis.sqrMagnitude > 1e-6f)
                worldAxis = correctedAxis.normalized;
        }

        return worldAxis.sqrMagnitude > 1e-6f;
    }

    void CaptureAvatarProximalSpreadAxisLocal(HandRig rig)
    {
        if (rig == null || rig.hand == null)
            return;

        if (!TryComputeAvatarPalmNormalWorld(rig, out Vector3 worldAxis))
            return;

        Vector3 localAxis = Quaternion.Inverse(rig.hand.rotation) * worldAxis.normalized;
        if (localAxis.sqrMagnitude > 1e-6f)
            _avatarProximalSpreadAxisLocal[rig] = localAxis.normalized;
    }

    bool TryComputeAvatarPalmNormalWorld(HandRig rig, out Vector3 palmNormal)
    {
        palmNormal = Vector3.zero;
        if (rig == null)
            return false;

        if (!TryGetAvatarPalmRightWorld(rig, out Vector3 palmRight))
            return false;

        Vector3 palmForward = Vector3.zero;
        if (TryGetFingerWorldDirection(rig.middleProximal, out Vector3 middleDirection))
            palmForward = middleDirection;
        else if (rig.hand != null && rig.middleProximal != null)
            palmForward = rig.middleProximal.position - rig.hand.position;

        if (palmForward.sqrMagnitude < 1e-6f)
            return false;

        palmRight.Normalize();
        palmForward = Vector3.ProjectOnPlane(palmForward, palmRight);
        if (palmForward.sqrMagnitude < 1e-6f)
            return false;
        palmForward.Normalize();

        palmNormal = Vector3.Cross(palmRight, palmForward);
        if (palmNormal.sqrMagnitude < 1e-6f)
            return false;

        palmNormal.Normalize();
        return true;
    }

    bool TryGetAvatarPalmRightWorld(HandRig rig, out Vector3 palmRight)
    {
        palmRight = Vector3.zero;
        if (rig == null || rig.indexProximal == null || rig.littleProximal == null)
            return false;

        palmRight = rig.indexProximal.position - rig.littleProximal.position;
        return palmRight.sqrMagnitude > 1e-6f;
    }

    bool TryGetFingerWorldDirection(Transform proximal, out Vector3 direction)
    {
        direction = Vector3.zero;
        if (proximal == null)
            return false;

        Transform child = proximal.childCount > 0 ? proximal.GetChild(0) : null;
        if (child != null)
            direction = child.position - proximal.position;

        if (direction.sqrMagnitude < 1e-6f)
            direction = proximal.rotation * Vector3.forward;

        return direction.sqrMagnitude > 1e-6f;
    }

    void ApplyLocalThumbCurl(
        HandRig rig,
        Transform bone,
        float curlDegrees,
        float oppositionDegrees,
        float spreadDegrees,
        float ballOppositionDegrees,
        float weight,
        float openExtensionDegrees,
        bool applyOffsets)
    {
        if (bone == null)
            return;

        if (!_targetBindRotations.TryGetValue(bone, out Quaternion open))
        {
            open = bone.localRotation;
            _targetBindRotations[bone] = open;
        }

        Vector3 curlAxis = rig.thumbCurlAxis.sqrMagnitude > 1e-6f ? rig.thumbCurlAxis.normalized : Vector3.right;
        Vector3 oppositionAxis = rig.thumbOppositionAxis.sqrMagnitude > 1e-6f ? rig.thumbOppositionAxis.normalized : Vector3.forward;
        Vector3 spreadAxis = rig.thumbSpreadAxis.sqrMagnitude > 1e-6f ? rig.thumbSpreadAxis.normalized : Vector3.up;
        float extensionSign = GetRightMirrorSign(rig, rig.mirrorRightOpenExtension);
        float oppositionSign = GetRightMirrorSign(rig, rig.mirrorRightThumbOpposition);
        float curlOffset = applyOffsets ? rig.thumbCurlOffsetDegrees : 0f;
        float oppositionOffset = applyOffsets ? rig.thumbOppositionOffsetDegrees : 0f;
        float spreadOffset = applyOffsets ? rig.thumbSpreadOffsetDegrees : 0f;
        Quaternion extension = Quaternion.AngleAxis(-openExtensionDegrees * extensionSign * rig.fingerCurlDirection * rig.thumbCurlDirection, curlAxis);
        Quaternion curlRotation = Quaternion.AngleAxis((curlDegrees + curlOffset) * rig.fingerCurlDirection * rig.thumbCurlDirection, curlAxis);
        Quaternion oppositionRotation = Quaternion.AngleAxis((oppositionDegrees + ballOppositionDegrees + oppositionOffset) * oppositionSign * rig.fingerCurlDirection, oppositionAxis);
        Quaternion spreadRotation = Quaternion.AngleAxis((spreadDegrees + spreadOffset) * rig.fingerCurlDirection, spreadAxis);
        Quaternion desired = open * extension * spreadRotation * oppositionRotation * curlRotation;
        bone.localRotation = Quaternion.Slerp(bone.localRotation, desired, weight);
    }

    float GetRightMirrorSign(HandRig rig, bool mirrorRight)
    {
        return mirrorRight && rig != null && !rig.isLeft ? -1f : 1f;
    }

    void CaptureWristRotationOffset(HandRig rig)
    {
        if (rig == null || rig.hand == null)
            return;

        Transform wristSource = GetWristSource(rig);
        if (wristSource == null)
            return;

        rig.calibratedWristRotationOffset = Quaternion.Inverse(wristSource.rotation) * rig.hand.rotation;
    }

    void CaptureFingerBindPose(HandRig rig)
    {
        if (rig == null)
            return;

        CaptureAvatarProximalSpreadAxisLocal(rig);

        if (rig.ovrSkeleton == null)
            return;

        AddFingerBind(rig, OVRSkeleton.BoneId.Hand_Thumb1, rig.thumbProximal);
        AddFingerBind(rig, OVRSkeleton.BoneId.Hand_Thumb2, rig.thumbIntermediate);
        AddFingerBind(rig, OVRSkeleton.BoneId.Hand_Thumb3, rig.thumbDistal);
        AddFingerBind(rig, OVRSkeleton.BoneId.Hand_Index1, rig.indexProximal);
        AddFingerBind(rig, OVRSkeleton.BoneId.Hand_Index2, rig.indexIntermediate);
        AddFingerBind(rig, OVRSkeleton.BoneId.Hand_Index3, rig.indexDistal);
        AddFingerBind(rig, OVRSkeleton.BoneId.Hand_Middle1, rig.middleProximal);
        AddFingerBind(rig, OVRSkeleton.BoneId.Hand_Middle2, rig.middleIntermediate);
        AddFingerBind(rig, OVRSkeleton.BoneId.Hand_Middle3, rig.middleDistal);
        AddFingerBind(rig, OVRSkeleton.BoneId.Hand_Ring1, rig.ringProximal);
        AddFingerBind(rig, OVRSkeleton.BoneId.Hand_Ring2, rig.ringIntermediate);
        AddFingerBind(rig, OVRSkeleton.BoneId.Hand_Ring3, rig.ringDistal);
        AddFingerBind(rig, OVRSkeleton.BoneId.Hand_Pinky1, rig.littleProximal);
        AddFingerBind(rig, OVRSkeleton.BoneId.Hand_Pinky2, rig.littleIntermediate);
        AddFingerBind(rig, OVRSkeleton.BoneId.Hand_Pinky3, rig.littleDistal);
    }

    void AddFingerBind(HandRig rig, OVRSkeleton.BoneId sourceBoneId, Transform target)
    {
        Transform source = FindOvrBone(rig.ovrSkeleton, sourceBoneId);
        if (source == null || target == null)
            return;

        _sourceBindRotations[source] = source.localRotation;
        _targetBindRotations[target] = target.localRotation;
        _fingerMap[source] = target;
    }

    Transform FindOvrBone(OVRSkeleton skeleton, OVRSkeleton.BoneId boneId)
    {
        if (skeleton == null || skeleton.Bones == null)
            return null;

        foreach (OVRBone bone in skeleton.Bones)
        {
            if (bone.Id == boneId)
                return bone.Transform;
        }

        return null;
    }

    void AutoFindAvatarBones(HandRig rig)
    {
        string suffix = rig.isLeft ? "_L" : "_R";

        rig.shoulder = FindChildByName("Shoulder" + suffix, rig.shoulder);
        rig.upperArm = FindChildByName("UpperArm" + suffix, rig.upperArm);
        rig.lowerArm = FindChildByName("LowerArm" + suffix, rig.lowerArm);
        rig.hand = FindChildByName("Hand" + suffix, rig.hand);

        rig.thumbProximal = FindChildByName("ThumbProximal" + suffix, rig.thumbProximal);
        rig.thumbIntermediate = FindChildByName("ThumbIntermediate" + suffix, rig.thumbIntermediate);
        rig.thumbDistal = FindChildByName("ThumbDistal" + suffix, rig.thumbDistal);
        rig.indexProximal = FindChildByName("IndexProximal" + suffix, rig.indexProximal);
        rig.indexIntermediate = FindChildByName("IndexIntermediate" + suffix, rig.indexIntermediate);
        rig.indexDistal = FindChildByName("IndexDistal" + suffix, rig.indexDistal);
        rig.middleProximal = FindChildByName("MiddleProximal" + suffix, rig.middleProximal);
        rig.middleIntermediate = FindChildByName("MiddleIntermediate" + suffix, rig.middleIntermediate);
        rig.middleDistal = FindChildByName("MiddleDistal" + suffix, rig.middleDistal);
        rig.ringProximal = FindChildByName("RingProximal" + suffix, rig.ringProximal);
        rig.ringIntermediate = FindChildByName("RingIntermediate" + suffix, rig.ringIntermediate);
        rig.ringDistal = FindChildByName("RingDistal" + suffix, rig.ringDistal);
        rig.littleProximal = FindChildByName("LittleProximal" + suffix, rig.littleProximal);
        rig.littleIntermediate = FindChildByName("LittleIntermediate" + suffix, rig.littleIntermediate);
        rig.littleDistal = FindChildByName("LittleDistal" + suffix, rig.littleDistal);
    }

    Transform FindChildByName(string childName, Transform current)
    {
        if (current != null)
            return current;

        if (avatarRoot == null)
            return null;

        Transform[] children = avatarRoot.GetComponentsInChildren<Transform>(true);
        foreach (Transform child in children)
        {
            if (child.name == childName)
                return child;
        }

        return null;
    }

    static Transform GetTransformField(MonoBehaviour behaviour, string fieldName)
    {
        FieldInfo field = behaviour.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (field == null || !typeof(Transform).IsAssignableFrom(field.FieldType))
            return null;

        return field.GetValue(behaviour) as Transform;
    }

    static OVRSkeleton FindSkeletonForHand(OVRHand hand)
    {
        if (hand == null)
            return null;

        bool wantLeft = IsLikelyHandedness(hand, true);
        bool wantRight = IsLikelyHandedness(hand, false);

        OVRSkeleton skeleton = hand.GetComponent<OVRSkeleton>();
        if (skeleton != null)
            return skeleton;

        skeleton = hand.GetComponentInChildren<OVRSkeleton>(true);
        if (skeleton != null)
            return skeleton;

        Transform parent = hand.transform.parent;
        while (parent != null)
        {
            skeleton = parent.GetComponentInChildren<OVRSkeleton>(true);
            if (skeleton != null && IsSameSide(hand, skeleton))
                return skeleton;
            parent = parent.parent;
        }

        OVRSkeleton[] allSkeletons = FindObjectsByType<OVRSkeleton>(FindObjectsSortMode.None);
        foreach (OVRSkeleton candidate in allSkeletons)
        {
            if (candidate == null)
                continue;

            bool candidateLeft = IsLikelyHandedness(candidate, true);
            bool candidateRight = IsLikelyHandedness(candidate, false);
            if ((wantLeft && candidateLeft) || (wantRight && candidateRight))
                return candidate;
        }

        if (allSkeletons.Length == 1)
            return allSkeletons[0];

        return null;
    }

    static bool IsSameSide(OVRHand hand, OVRSkeleton skeleton)
    {
        bool handLeft = IsLikelyHandedness(hand, true);
        bool handRight = IsLikelyHandedness(hand, false);
        bool skeletonLeft = IsLikelyHandedness(skeleton, true);
        bool skeletonRight = IsLikelyHandedness(skeleton, false);

        if (handLeft && skeletonLeft)
            return true;
        if (handRight && skeletonRight)
            return true;

        return !handLeft && !handRight && !skeletonLeft && !skeletonRight;
    }

    static bool IsLikelyHandedness(Component component, bool left)
    {
        if (component == null)
            return false;

        string target = left ? "left" : "right";
        Transform current = component.transform;
        while (current != null)
        {
            if (current.name.ToLowerInvariant().Contains(target))
                return true;
            current = current.parent;
        }

        if (TryObjectContainsText(component, target))
            return true;

        Component[] components = component.GetComponents<Component>();
        for (int i = 0; i < components.Length; i++)
        {
            if (TryObjectContainsText(components[i], target))
                return true;
        }

        return false;
    }

    static bool TryObjectContainsText(object source, string target)
    {
        if (source == null)
            return false;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = source.GetType();

        string[] propertyNames = { "HandType", "Handedness" };
        for (int i = 0; i < propertyNames.Length; i++)
        {
            PropertyInfo property = type.GetProperty(propertyNames[i], flags);
            if (property != null &&
                property.GetIndexParameters().Length == 0 &&
                ValueContainsText(property.GetValue(source, null), target))
            {
                return true;
            }
        }

        string[] fieldNames = { "HandType", "_handType", "Handedness", "_handedness" };
        for (int i = 0; i < fieldNames.Length; i++)
        {
            FieldInfo field = type.GetField(fieldNames[i], flags);
            if (field != null && ValueContainsText(field.GetValue(source), target))
                return true;
        }

        return false;
    }

    static bool ValueContainsText(object value, string target)
    {
        return value != null && value.ToString().ToLowerInvariant().Contains(target);
    }

    static bool IsHandDataSourceLike(MonoBehaviour behaviour)
    {
        Type type = behaviour.GetType();
        while (type != null)
        {
            if (type.IsGenericType &&
                type.GetGenericTypeDefinition().Name.Contains("DataSource") &&
                type.GetGenericArguments().Length == 1 &&
                type.GetGenericArguments()[0].Name.Contains("HandDataAsset"))
            {
                return true;
            }

            if (type.Name.Contains("HandDataSource"))
                return true;

            type = type.BaseType;
        }

        return false;
    }

    static bool TryGetFingerStrengthFromHandDataSource(MonoBehaviour source, OVRHand.HandFinger finger, out float strength)
    {
        strength = 0f;
        if (source == null)
            return false;

        object dataAsset = GetHandDataAsset(source);
        if (dataAsset == null)
            return false;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        FieldInfo field = dataAsset.GetType().GetField("FingerPinchStrength", flags);
        object values = field?.GetValue(dataAsset);
        if (values is float[] floats && floats.Length > (int)finger)
        {
            strength = Mathf.Clamp01(floats[(int)finger]);
            return true;
        }

        PropertyInfo property = dataAsset.GetType().GetProperty("FingerPinchStrength", flags);
        values = property?.GetValue(dataAsset, null);
        if (values is float[] propFloats && propFloats.Length > (int)finger)
        {
            strength = Mathf.Clamp01(propFloats[(int)finger]);
            return true;
        }

        return false;
    }

    static Pose[] GetJointPosesFromHandDataSource(MonoBehaviour source)
    {
        object dataAsset = GetHandDataAsset(source);
        if (dataAsset == null)
            return null;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        FieldInfo field = dataAsset.GetType().GetField("JointPoses", flags);
        if (field?.GetValue(dataAsset) is Pose[] posesFromField)
            return posesFromField;

        PropertyInfo property = dataAsset.GetType().GetProperty("JointPoses", flags);
        if (property?.GetValue(dataAsset, null) is Pose[] posesFromProperty)
            return posesFromProperty;

        return null;
    }

    static object GetHandDataAsset(MonoBehaviour source)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = source.GetType();

        while (type != null)
        {
            PropertyInfo property = type.GetProperty("DataAsset", flags);
            if (property != null && property.GetIndexParameters().Length == 0)
                return property.GetValue(source, null);

            FieldInfo field = type.GetField("_handDataAsset", flags);
            if (field != null)
                return field.GetValue(source);

            type = type.BaseType;
        }

        return null;
    }
}
