using System;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class ManualSpatialAnchorPlacer : MonoBehaviour
{
    public enum PlacementSourceMode
    {
        PointerTransform,
        OvrHandJoint,
        RaycastFromPointer,
        CameraForwardFallback
    }

    public enum OvrHandPlacementJoint
    {
        PointerPose,
        HandRoot,
        Wrist,
        IndexTip,
        PinchPoint
    }

    [Header("AR Anchor")]
    public ARAnchorManager anchorManager;
    [Tooltip("Use a Unity-world session anchor when Meta/AR anchors are unavailable, e.g. Quest Link / PCVR.")]
    public bool allowPcvrSessionAnchorFallback = true;
    public float anchorCreateTimeoutSec = 4f;

    [Header("Placement Source")]
    public PlacementSourceMode sourceMode = PlacementSourceMode.RaycastFromPointer;
    public Transform placementPointer;
    public Camera fallbackCamera;
    public LayerMask raycastMask = ~0;
    public float raycastMaxDistance = 5f;
    public float fallbackDistance = 1.0f;
    public bool keepRotationLevel = true;

    [Header("Visuals")]
    public GameObject previewObject;
    public GameObject anchorMarkerPrefab;
    public bool hidePreviewWhenIdle = true;
    public bool createDefaultVisuals = true;

    [Header("VR Status")]
    public TextMesh statusText;
    public bool createDefaultStatusText = true;
    public Vector3 statusTextCameraOffset = new Vector3(0f, -0.25f, 1.2f);

    [Header("Quest Controller Input")]
    public bool enableOvrControllerInput = true;
    public OVRInput.RawButton confirmButton = OVRInput.RawButton.RIndexTrigger;
    public OVRInput.RawButton cancelButton = OVRInput.RawButton.B;

    [Header("Quest Hand Input")]
    public bool enableOvrHandPinchInput = true;
    public OVRHand confirmHand;
    public OVRHand.HandFinger confirmFinger = OVRHand.HandFinger.Index;
    [Range(0f, 1f)] public float pinchConfirmThreshold = 0.7f;
    [Range(0f, 1f)] public float pinchReleaseThreshold = 0.35f;
    public bool autoFindConfirmHand = true;
    public bool showHandDebugInStatus = true;
    public bool preferLiveHandPoseForPlacement = true;
    public OvrHandPlacementJoint placementHandJoint = OvrHandPlacementJoint.PointerPose;
    public Vector3 handPlacementLocalOffset = Vector3.zero;

    [Header("Editor Debug Input")]
    public bool enableKeyboardInput = true;
    public KeyCode beginKey = KeyCode.B;
    public KeyCode confirmKey = KeyCode.C;
    public KeyCode cancelKey = KeyCode.Escape;

    public ARAnchor CurrentAnchor { get; private set; }
    public Transform CurrentAnchorTransform => CurrentAnchor != null ? CurrentAnchor.transform : sessionAnchorTransform;
    public bool IsPlacementMode { get; private set; }
    public bool IsCreatingAnchor => isCreatingAnchor;
    public bool HasAnchor => CurrentAnchor != null || sessionAnchorTransform != null;

    public event Action PlacementStarted;
    public event Action PlacementCanceled;
    public event Action<ARAnchor> AnchorCreated;
    public event Action<Transform> AnchorTransformCreated;
    public event Action AnchorCleared;
    public event Action<string> AnchorCreateFailed;

    private Pose candidatePose;
    private GameObject anchorMarkerInstance;
    private Transform sessionAnchorTransform;
    private bool wasPinching;
    private string placementStatusHint = "";
    private bool isCreatingAnchor;
    private float anchorCreateStartTime;

    private void Awake()
    {
        if (fallbackCamera == null)
            fallbackCamera = Camera.main;

        EnsureDefaultVisuals();
        EnsureDefaultStatusText();
        TryAutoAssignHand();
        SetPreviewActive(false);
        SetStatusMessage("Anchor placer ready");
    }

    private void Update()
    {
        if (isCreatingAnchor)
        {
            float elapsed = Time.realtimeSinceStartup - anchorCreateStartTime;
            SetStatusMessage($"Creating Spatial Anchor...\nWaiting {elapsed:0.0}s");
            if (elapsed >= anchorCreateTimeoutSec)
            {
                isCreatingAnchor = false;
                if (allowPcvrSessionAnchorFallback)
                    CreatePcvrSessionAnchor("Anchor creation timeout");
                else
                    SetStatusMessage("Anchor creation timeout");
            }
            return;
        }

        if (enableKeyboardInput && Input.GetKeyDown(beginKey))
            BeginPlacement();

        if (!IsPlacementMode)
            return;

        TryAutoAssignHand();
        UpdateCandidatePose();

        if (enableKeyboardInput && Input.GetKeyDown(confirmKey))
            ConfirmPlacement();

        if (enableKeyboardInput && Input.GetKeyDown(cancelKey))
            CancelPlacement();

        if (enableOvrControllerInput)
        {
            if (OVRInput.GetDown(confirmButton))
                ConfirmPlacement();

            if (OVRInput.GetDown(cancelButton))
                CancelPlacement();
        }

        if (enableOvrHandPinchInput && confirmHand != null)
        {
            float pinchStrength = confirmHand.GetFingerPinchStrength(confirmFinger);
            bool pinching = confirmHand.IsTracked &&
                            (confirmHand.GetFingerIsPinching(confirmFinger) || pinchStrength >= pinchConfirmThreshold);
            if (pinching && !wasPinching)
                ConfirmPlacement();
            if (!pinching && pinchStrength <= pinchReleaseThreshold)
                wasPinching = false;
            else if (pinching)
                wasPinching = true;
        }

        UpdatePlacementStatusHint();
    }

    private void LateUpdate()
    {
        UpdateStatusTextPose();
    }

    public void BeginPlacement()
    {
        TryAutoAssignHand();
        RefreshFallbackCamera();
        IsPlacementMode = true;
        wasPinching = false;
        UpdateCandidatePose();
        SetPreviewActive(true);
        placementStatusHint = "Anchor placement started";
        UpdatePlacementStatusHint();
        PlacementStarted?.Invoke();
    }

    public void CancelPlacement()
    {
        if (!IsPlacementMode)
            return;

        IsPlacementMode = false;
        SetPreviewActive(false);
        SetStatusMessage("Anchor placement canceled");
        PlacementCanceled?.Invoke();
    }

    public async void ConfirmPlacement()
    {
        if (!IsPlacementMode)
        {
            SetStatusMessage("Confirm ignored\nAnchor placement is not active");
            return;
        }

        IsPlacementMode = false;
        SetPreviewActive(false);
        SetStatusMessage("Creating Spatial Anchor...");
        isCreatingAnchor = true;
        anchorCreateStartTime = Time.realtimeSinceStartup;

        if (anchorManager == null)
        {
            isCreatingAnchor = false;
            FailAnchorCreation("ARAnchorManager is not assigned.");
            return;
        }

        if (anchorManager.subsystem == null || !anchorManager.subsystem.running)
        {
            isCreatingAnchor = false;
            string reason = "ARAnchorManager subsystem is not running. Using PCVR session anchor instead.";
            if (allowPcvrSessionAnchorFallback)
                CreatePcvrSessionAnchor(reason);
            else
                FailAnchorCreation(reason);
            return;
        }

        if (CurrentAnchor != null)
        {
            anchorManager.TryRemoveAnchor(CurrentAnchor);
            CurrentAnchor = null;
        }

        ClearSessionAnchor();

        var result = await anchorManager.TryAddAnchorAsync(candidatePose);
        if (!isCreatingAnchor)
            return;

        isCreatingAnchor = false;
        if (!result.status.IsSuccess())
        {
            FailAnchorCreation(result.status.ToString());
            return;
        }

        CurrentAnchor = result.value;
        AttachOrCreateMarker();
        SetStatusMessage("Spatial Anchor created");
        AnchorCreated?.Invoke(CurrentAnchor);
        AnchorTransformCreated?.Invoke(CurrentAnchor.transform);
    }

    public void ClearAnchor()
    {
        if (anchorMarkerInstance != null)
        {
            Destroy(anchorMarkerInstance);
            anchorMarkerInstance = null;
        }

        ClearSessionAnchor();

        if (CurrentAnchor != null && anchorManager != null)
        {
            anchorManager.TryRemoveAnchor(CurrentAnchor);
            CurrentAnchor = null;
        }

        SetStatusMessage("Anchor cleared");
        AnchorCleared?.Invoke();
    }

    public void SetStatusMessage(string message)
    {
        placementStatusHint = message;
        if (statusText != null)
            statusText.text = message;
    }

    private void UpdatePlacementStatusHint()
    {
        if (!IsPlacementMode || statusText == null)
            return;

        string handLine = "";
        if (showHandDebugInStatus)
        {
            if (!enableOvrHandPinchInput)
            {
                handLine = "Hand pinch input: disabled";
            }
            else if (confirmHand == null)
            {
                handLine = "confirmHand is not assigned";
            }
            else
            {
                float strength = confirmHand.GetFingerPinchStrength(confirmFinger);
                handLine = $"Hand: {confirmHand.name}\nTracked: {confirmHand.IsTracked}  pinch: {strength:0.00}";
            }
        }

        statusText.text =
            $"{placementStatusHint}\n" +
            "Move the hand marker to the reference point\n" +
            $"Pinch to confirm, threshold {pinchConfirmThreshold:0.00}\n" +
            handLine;
    }

    private void TryAutoAssignHand()
    {
        if (!autoFindConfirmHand || confirmHand != null)
            return;

        OVRHand[] hands = FindObjectsByType<OVRHand>(FindObjectsSortMode.None);
        if (hands == null || hands.Length == 0)
            return;

        for (int i = 0; i < hands.Length; i++)
        {
            if (hands[i] != null && hands[i].name.ToLowerInvariant().Contains("right"))
            {
                confirmHand = hands[i];
                return;
            }
        }

        confirmHand = hands[0];
    }

    private void UpdateCandidatePose()
    {
        RefreshFallbackCamera();

        if (!preferLiveHandPoseForPlacement && placementPointer == null && confirmHand != null)
            placementPointer = confirmHand.transform;

        if (TryGetLiveHandPlacementPose(out Pose handPose))
        {
            candidatePose = handPose;
            if (previewObject != null)
                previewObject.transform.SetPositionAndRotation(candidatePose.position, candidatePose.rotation);
            return;
        }

        Transform pointer = placementPointer != null ? placementPointer : (fallbackCamera != null ? fallbackCamera.transform : null);
        if (pointer == null)
            return;

        Vector3 position;
        Vector3 forward;

        if (sourceMode == PlacementSourceMode.OvrHandJoint)
        {
            position = pointer.position + pointer.forward * fallbackDistance;
            forward = pointer.forward;
            candidatePose = new Pose(position, MakeRotation(forward, Vector3.up));
        }
        else if (sourceMode == PlacementSourceMode.RaycastFromPointer &&
            Physics.Raycast(pointer.position, pointer.forward, out RaycastHit hit, raycastMaxDistance, raycastMask, QueryTriggerInteraction.Ignore))
        {
            position = hit.point;
            forward = Vector3.ProjectOnPlane(pointer.forward, hit.normal);
            if (forward.sqrMagnitude < 1e-6f)
                forward = Vector3.ProjectOnPlane(pointer.up, hit.normal);
            candidatePose = new Pose(position, MakeRotation(forward, hit.normal));
        }
        else if (sourceMode == PlacementSourceMode.CameraForwardFallback)
        {
            position = pointer.position + pointer.forward * fallbackDistance;
            forward = pointer.forward;
            candidatePose = new Pose(position, MakeRotation(forward, Vector3.up));
        }
        else
        {
            bool raycastMissed = sourceMode == PlacementSourceMode.RaycastFromPointer;
            position = raycastMissed ? pointer.position + pointer.forward * fallbackDistance : pointer.position;
            forward = pointer.forward;
            candidatePose = new Pose(position, MakeRotation(forward, Vector3.up));
        }

        if (previewObject != null)
            previewObject.transform.SetPositionAndRotation(candidatePose.position, candidatePose.rotation);
    }

    private void RefreshFallbackCamera()
    {
        if (fallbackCamera == null)
            fallbackCamera = Camera.main;
        if (fallbackCamera == null)
            fallbackCamera = FindAnyObjectByType<Camera>();
    }

    private bool TryGetLiveHandPlacementPose(out Pose pose)
    {
        pose = default;
        if (sourceMode != PlacementSourceMode.OvrHandJoint && !preferLiveHandPoseForPlacement)
            return false;
        if (confirmHand == null || !confirmHand.IsTracked)
            return false;

        Transform handTransform = confirmHand.transform;
        Vector3 position = handTransform.position;
        Quaternion rotation = handTransform.rotation;

        if (placementHandJoint == OvrHandPlacementJoint.PointerPose && TryGetPointerPose(out Pose pointerPose))
        {
            position = pointerPose.position;
            rotation = pointerPose.rotation;
        }
        else if (placementHandJoint == OvrHandPlacementJoint.PinchPoint &&
            TryGetSkeletonBonePose(OVRSkeleton.BoneId.Hand_IndexTip, out Pose indexPose) &&
            TryGetSkeletonBonePose(OVRSkeleton.BoneId.Hand_ThumbTip, out Pose thumbPose))
        {
            position = (indexPose.position + thumbPose.position) * 0.5f;
            rotation = indexPose.rotation;
        }
        else if (placementHandJoint == OvrHandPlacementJoint.IndexTip &&
                 TryGetSkeletonBonePose(OVRSkeleton.BoneId.Hand_IndexTip, out Pose indexTipPose))
        {
            position = indexTipPose.position;
            rotation = indexTipPose.rotation;
        }
        else if (placementHandJoint == OvrHandPlacementJoint.Wrist &&
                 TryGetSkeletonBonePose(OVRSkeleton.BoneId.Hand_WristRoot, out Pose wristPose))
        {
            position = wristPose.position;
            rotation = wristPose.rotation;
        }

        position += rotation * handPlacementLocalOffset;
        Vector3 forward = rotation * Vector3.forward;
        pose = new Pose(position, MakeRotation(forward, Vector3.up));
        return true;
    }

    private bool TryGetSkeletonBonePose(OVRSkeleton.BoneId boneId, out Pose pose)
    {
        pose = default;
        if (confirmHand == null)
            return false;

        OVRSkeleton skeleton = confirmHand.GetComponent<OVRSkeleton>();
        if (skeleton == null || skeleton.Bones == null)
            return false;

        foreach (var bone in skeleton.Bones)
        {
            if (bone.Id != boneId || bone.Transform == null)
                continue;

            pose = new Pose(bone.Transform.position, bone.Transform.rotation);
            return true;
        }

        return false;
    }

    private bool TryGetPointerPose(out Pose pose)
    {
        pose = default;
        if (confirmHand == null || !confirmHand.IsTracked || !confirmHand.IsPointerPoseValid)
            return false;

        Transform pointer = confirmHand.GetPointerRayTransform();
        if (pointer == null)
            return false;

        pose = new Pose(pointer.position, pointer.rotation);
        return true;
    }

    private Quaternion MakeRotation(Vector3 forward, Vector3 up)
    {
        Vector3 resolvedUp = keepRotationLevel ? Vector3.up : up;
        Vector3 resolvedForward = keepRotationLevel ? Vector3.ProjectOnPlane(forward, Vector3.up) : Vector3.ProjectOnPlane(forward, resolvedUp);

        if (resolvedForward.sqrMagnitude < 1e-6f)
            resolvedForward = Vector3.forward;

        return Quaternion.LookRotation(resolvedForward.normalized, resolvedUp.normalized);
    }

    private void AttachOrCreateMarker()
    {
        if (CurrentAnchor == null)
            return;

        CreateMarkerUnder(CurrentAnchor.transform, "SpatialAnchorMarker", new Color(0.1f, 1f, 0.25f, 1f));
    }

    private void FailAnchorCreation(string reason)
    {
        Debug.LogError($"[ManualSpatialAnchorPlacer] Failed to create anchor: {reason}");
        SetStatusMessage($"Anchor create failed\n{reason}");
        if (allowPcvrSessionAnchorFallback)
            CreatePcvrSessionAnchor(reason);
        AnchorCreateFailed?.Invoke(reason);
    }

    private void CreatePcvrSessionAnchor(string reason)
    {
        ClearSessionAnchor();

        GameObject sessionAnchor = new GameObject("PCVRSessionAnchor");
        sessionAnchor.transform.SetPositionAndRotation(candidatePose.position, candidatePose.rotation);
        sessionAnchorTransform = sessionAnchor.transform;

        CreateMarkerUnder(sessionAnchorTransform, "PCVRSessionAnchorMarker", new Color(0.1f, 0.75f, 1f, 1f));
        SetStatusMessage($"PCVR session anchor created\n{reason}");
        AnchorTransformCreated?.Invoke(sessionAnchorTransform);
    }

    private void CreateMarkerUnder(Transform parent, string markerName, Color fallbackColor)
    {
        if (anchorMarkerInstance != null)
            Destroy(anchorMarkerInstance);

        anchorMarkerInstance = anchorMarkerPrefab != null
            ? Instantiate(anchorMarkerPrefab, parent)
            : GameObject.CreatePrimitive(PrimitiveType.Sphere);

        anchorMarkerInstance.name = markerName;

        if (anchorMarkerPrefab == null)
        {
            anchorMarkerInstance.transform.SetParent(parent, false);
            anchorMarkerInstance.transform.localScale = Vector3.one * 0.06f;
            RemoveCollider(anchorMarkerInstance);
            SetPrimitiveColor(anchorMarkerInstance, fallbackColor);
        }

        anchorMarkerInstance.transform.localPosition = Vector3.zero;
        anchorMarkerInstance.transform.localRotation = Quaternion.identity;
    }

    private void SetPreviewActive(bool active)
    {
        if (previewObject != null)
            previewObject.SetActive(active || !hidePreviewWhenIdle);
    }

    private void EnsureDefaultVisuals()
    {
        if (!createDefaultVisuals)
            return;

        if (previewObject == null)
        {
            previewObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            previewObject.name = "SpatialAnchorPreview";
            previewObject.transform.localScale = Vector3.one * 0.08f;
            RemoveCollider(previewObject);
            SetPrimitiveColor(previewObject, new Color(1f, 0.85f, 0.05f, 0.75f));
        }
    }

    private void EnsureDefaultStatusText()
    {
        if (!createDefaultStatusText || statusText != null)
            return;

        GameObject textObject = new GameObject("SpatialAnchorStatusText");
        statusText = textObject.AddComponent<TextMesh>();
        statusText.anchor = TextAnchor.MiddleCenter;
        statusText.alignment = TextAlignment.Center;
        statusText.characterSize = 0.045f;
        statusText.fontSize = 48;
        statusText.color = Color.white;
    }

    private void UpdateStatusTextPose()
    {
        if (statusText == null)
            return;

        Transform cam = fallbackCamera != null ? fallbackCamera.transform : (Camera.main != null ? Camera.main.transform : null);
        if (cam == null)
            return;

        statusText.transform.position =
            cam.position +
            cam.right * statusTextCameraOffset.x +
            cam.up * statusTextCameraOffset.y +
            cam.forward * statusTextCameraOffset.z;
        statusText.transform.rotation = Quaternion.LookRotation(statusText.transform.position - cam.position, cam.up);
    }

    private static void SetPrimitiveColor(GameObject target, Color color)
    {
        Renderer renderer = target.GetComponent<Renderer>();
        if (renderer == null)
            return;

        renderer.material = new Material(Shader.Find("Standard"));
        renderer.material.color = color;
    }

    private static void RemoveCollider(GameObject target)
    {
        Collider collider = target.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);
    }

    private void ClearSessionAnchor()
    {
        if (sessionAnchorTransform == null)
            return;

        Destroy(sessionAnchorTransform.gameObject);
        sessionAnchorTransform = null;
    }
}
