using UnityEngine;

/// <summary>
/// Fits an avatar body to the user's HMD pose before hand IK/redirection is applied.
/// Attach this to the avatar root or a setup object, then assign HMD, avatar root, and avatar head.
/// </summary>
public class AvatarBodyFitter : MonoBehaviour
{
    [Header("References")]
    public Transform hmdTransform;
    public Transform avatarRoot;
    public Transform avatarHead;

    [Header("Fit")]
    [Tooltip("Horizontal yaw offset from HMD forward to avatar forward, in degrees.")]
    public float yawOffsetDegrees = 0f;

    [Tooltip("Offset from HMD to the avatar head target, expressed in avatar yaw space.")]
    public Vector3 hmdToAvatarHeadOffset = new Vector3(0f, -0.08f, 0.03f);

    [Tooltip("If enabled, scales the avatar so its configured eye height matches the current HMD height.")]
    public bool scaleToHmdHeight = false;

    [Tooltip("Approximate avatar eye/view height before scaling. TonaThas forUnity uses about 1.6m.")]
    public float avatarEyeHeight = 1.6f;

    [Tooltip("Extra multiplier after height fitting.")]
    public float scaleMultiplier = 1f;

    [Header("Follow")]
    public bool followContinuously = true;
    public bool followPosition = true;
    public bool followYaw = true;
    public bool ignoreHmdPitchAndRoll = true;

    Vector3 _initialRootScale = Vector3.one;
    Vector3 _headLocalAtSetup;
    bool _captured;

    void Reset()
    {
        avatarRoot = transform;
        Camera mainCamera = Camera.main;
        if (mainCamera != null)
            hmdTransform = mainCamera.transform;
    }

    void Awake()
    {
        CaptureSetupPose();
    }

    void LateUpdate()
    {
        if (followContinuously)
            FitNow();
    }

    [ContextMenu("Avatar Body Fitter/Capture Setup Pose")]
    public void CaptureSetupPose()
    {
        if (avatarRoot == null)
            avatarRoot = transform;

        if (avatarRoot == null || avatarHead == null)
            return;

        _initialRootScale = avatarRoot.localScale;
        _headLocalAtSetup = avatarRoot.InverseTransformPoint(avatarHead.position);
        _captured = true;
    }

    [ContextMenu("Avatar Body Fitter/Fit Now")]
    public void FitNow()
    {
        if (hmdTransform == null || avatarRoot == null || avatarHead == null)
            return;

        if (!_captured)
            CaptureSetupPose();

        float scale = scaleMultiplier;
        if (scaleToHmdHeight && avatarEyeHeight > 0.01f)
            scale *= Mathf.Max(0.01f, hmdTransform.position.y / avatarEyeHeight);

        avatarRoot.localScale = _initialRootScale * scale;

        Quaternion targetRotation = avatarRoot.rotation;
        if (followYaw)
            targetRotation = GetHmdYawRotation() * Quaternion.Euler(0f, yawOffsetDegrees, 0f);
        else if (ignoreHmdPitchAndRoll)
            targetRotation = Quaternion.Euler(0f, avatarRoot.eulerAngles.y, 0f);

        if (followYaw)
            avatarRoot.rotation = targetRotation;

        if (followPosition)
        {
            Vector3 desiredHeadPosition = hmdTransform.position + targetRotation * hmdToAvatarHeadOffset;
            avatarRoot.position = desiredHeadPosition - targetRotation * (_headLocalAtSetup * scale);
        }
    }

    Quaternion GetHmdYawRotation()
    {
        Vector3 forward = hmdTransform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 1e-6f)
            return Quaternion.Euler(0f, hmdTransform.eulerAngles.y, 0f);

        return Quaternion.LookRotation(forward.normalized, Vector3.up);
    }
}
