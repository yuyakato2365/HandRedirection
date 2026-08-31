using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One-handed world-space opacity control shown above the right hand while an
/// anchor/desk alignment is being placed. The left pinch drags the single knob.
/// </summary>
public sealed class AnchorPlacementOpacitySlider : MonoBehaviour
{
    [Header("Placement State")]
    public ManualSpatialAnchorPlacer anchorPlacer;
    public SpatialAnchorToDeskOriginBinder deskBinder;
    public AnchorPlacementSceneFader sceneFader;

    [Header("Hands")]
    public OVRHand rightHand;
    public OVRHand leftHand;
    public PinchProvider rightPinchProvider;
    public PinchProvider leftPinchProvider;
    public bool autoFindHands = true;

    [Header("Value")]
    [Range(0.05f, 1f)] public float opacityMultiplier = 0.28f;
    [Range(0.01f, 1f)] public float minimumOpacity = 0.05f;
    public bool persistValue = true;
    public string playerPrefsKey = "HandRedirection.AnchorPlacementOpacity";

    [Header("Panel Placement")]
    public Vector3 rightHandWorldOffset = new Vector3(0f, 0.16f, 0f);
    public float panelWidth = 0.24f;
    public float trackWidth = 0.18f;
    public float pinchGrabRadius = 0.055f;
    public float pinchStartThreshold = 0.7f;
    public float pinchReleaseThreshold = 0.35f;
    public float followPositionSpeed = 20f;
    public float followRotationSpeed = 16f;

    private Transform panelRoot;
    private Transform fill;
    private Transform knob;
    private TextMesh valueLabel;
    private bool dragging;
    private bool wasPinching;
    private bool initializedPose;
    private bool lastVisible;
    private bool lastUsedTrackedRightHand;
    private bool poseStateLogged;

    private static readonly Color PanelColor = new Color(0.035f, 0.055f, 0.09f, 0.94f);
    private static readonly Color TrackColor = new Color(0.16f, 0.20f, 0.27f, 1f);
    private static readonly Color AccentColor = new Color(0.08f, 0.78f, 0.95f, 1f);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void EnsureRuntimeSlider()
    {
        EnsureForScene(
            FindFirstObjectByType<ManualSpatialAnchorPlacer>(),
            FindFirstObjectByType<SpatialAnchorToDeskOriginBinder>());
    }

    public static AnchorPlacementOpacitySlider EnsureForScene(
        ManualSpatialAnchorPlacer placer,
        SpatialAnchorToDeskOriginBinder binder)
    {
        AnchorPlacementSceneFader fader = FindFirstObjectByType<AnchorPlacementSceneFader>();
        if (fader == null)
        {
            GameObject host = new GameObject("AnchorPlacementSceneFader");
            fader = host.AddComponent<AnchorPlacementSceneFader>();
        }

        if (placer != null)
            fader.anchorPlacer = placer;
        else if (fader.anchorPlacer == null)
            fader.anchorPlacer = FindFirstObjectByType<ManualSpatialAnchorPlacer>();

        if (binder != null)
            fader.deskBinder = binder;
        else if (fader.deskBinder == null)
            fader.deskBinder = FindFirstObjectByType<SpatialAnchorToDeskOriginBinder>();

        if (fader.deskBinder != null)
            fader.deskBinder.UseExternalPlacementTransparencyController();

        AnchorPlacementOpacitySlider slider = fader.GetComponent<AnchorPlacementOpacitySlider>();
        if (slider == null)
            slider = fader.gameObject.AddComponent<AnchorPlacementOpacitySlider>();

        slider.anchorPlacer = fader.anchorPlacer;
        slider.deskBinder = fader.deskBinder;
        slider.sceneFader = fader;
        fader.RefreshTargetRenderers();
        slider.EnsureVisuals();

        Debug.Log(
            $"[AnchorPlacementOpacitySlider] Ready host={fader.name} " +
            $"panel={slider.panelRoot?.name ?? "missing"} placer={fader.anchorPlacer?.name ?? "missing"} " +
            $"binder={fader.deskBinder?.name ?? "missing"}",
            slider);
        return slider;
    }

    private void Awake()
    {
        if (persistValue && PlayerPrefs.HasKey(playerPrefsKey))
            opacityMultiplier = PlayerPrefs.GetFloat(playerPrefsKey, opacityMultiplier);
        opacityMultiplier = Mathf.Clamp(opacityMultiplier, minimumOpacity, 1f);
    }

    private void OnEnable()
    {
        ResolveReferences();
        EnsureVisuals();
        ApplyOpacity();
    }

    private void OnDisable()
    {
        ReleaseRotationInputOwnership();
    }

    private void OnDestroy()
    {
        ReleaseRotationInputOwnership();
    }

    private void Update()
    {
        ResolveReferences();
        EnsureVisuals();

        bool visible = AnchorPlacementSceneFader.IsPlacementVisualFadeActive(anchorPlacer, deskBinder);
        if (panelRoot != null && panelRoot.gameObject.activeSelf != visible)
            panelRoot.gameObject.SetActive(visible);

        if (visible != lastVisible)
        {
            Debug.Log(
                $"[AnchorPlacementOpacitySlider] visible={visible} " +
                $"rightHand={(rightHand != null ? rightHand.name : "missing")} " +
                $"leftHand={(leftHand != null ? leftHand.name : "missing")}",
                this);
            lastVisible = visible;
            poseStateLogged = false;
        }

        if (!visible)
        {
            dragging = false;
            wasPinching = false;
            initializedPose = false;
            ReleaseRotationInputOwnership();
            return;
        }

        UpdatePanelPose();
        UpdateLeftPinchDrag();
    }

    private void ResolveReferences()
    {
        if (anchorPlacer == null)
            anchorPlacer = FindAnyObjectByType<ManualSpatialAnchorPlacer>();
        if (deskBinder == null)
            deskBinder = FindAnyObjectByType<SpatialAnchorToDeskOriginBinder>();
        if (sceneFader == null)
            sceneFader = FindAnyObjectByType<AnchorPlacementSceneFader>();

        // Use the exact hands already selected by the desk-alignment workflow.
        // A name-only lookup can select an inactive duplicate in Meta sample scenes.
        if (deskBinder != null && deskBinder.rightConfirmHand != null)
            rightHand = deskBinder.rightConfirmHand;
        else if (anchorPlacer != null && anchorPlacer.confirmHand != null)
            rightHand = anchorPlacer.confirmHand;

        if (deskBinder != null && deskBinder.leftRotationHand != null)
            leftHand = deskBinder.leftRotationHand;

        if (deskBinder != null && deskBinder.rightConfirmPinchProvider != null)
            rightPinchProvider = deskBinder.rightConfirmPinchProvider;
        if (deskBinder != null && deskBinder.leftRotationPinchProvider != null)
            leftPinchProvider = deskBinder.leftRotationPinchProvider;

        if (!autoFindHands ||
            (leftHand != null && rightHand != null &&
             leftPinchProvider != null && rightPinchProvider != null))
            return;

        OVRHand[] hands = FindObjectsByType<OVRHand>(FindObjectsSortMode.None);
        for (int i = 0; i < hands.Length; i++)
        {
            OVRHand hand = hands[i];
            if (hand == null)
                continue;

            string lower = hand.name.ToLowerInvariant();
            if (leftHand == null && lower.Contains("left"))
                leftHand = hand;
            else if (rightHand == null && lower.Contains("right"))
                rightHand = hand;
        }

        PinchProvider[] providers = FindObjectsByType<PinchProvider>(FindObjectsSortMode.None);
        for (int i = 0; i < providers.Length; i++)
        {
            PinchProvider provider = providers[i];
            if (provider == null)
                continue;

            string lower = (provider.name + " " +
                            (provider.ovrHand != null ? provider.ovrHand.name : string.Empty)).ToLowerInvariant();
            if (leftPinchProvider == null && lower.Contains("left"))
                leftPinchProvider = provider;
            else if (rightPinchProvider == null && lower.Contains("right"))
                rightPinchProvider = provider;
        }
    }

    private void EnsureVisuals()
    {
        if (panelRoot != null)
            return;

        panelRoot = new GameObject("AnchorPlacementOpacitySliderPanel").transform;
        panelRoot.SetParent(transform, false);

        CreateBlock("Panel", panelRoot, new Vector3(panelWidth, 0.105f, 0.008f), new Vector3(0f, 0f, 0.012f), PanelColor);
        CreateBlock("Track", panelRoot, new Vector3(trackWidth, 0.012f, 0.012f), new Vector3(0f, -0.018f, 0f), TrackColor);
        fill = CreateBlock("Fill", panelRoot, Vector3.one, Vector3.zero, AccentColor);
        knob = CreateSphere("Knob", panelRoot, 0.022f, AccentColor);

        GameObject labelObject = new GameObject("OpacityMultiplierLabel");
        labelObject.transform.SetParent(panelRoot, false);
        labelObject.transform.localPosition = new Vector3(0f, 0.026f, -0.008f);
        labelObject.transform.localRotation = Quaternion.identity;
        valueLabel = labelObject.AddComponent<TextMesh>();
        valueLabel.anchor = TextAnchor.MiddleCenter;
        valueLabel.alignment = TextAlignment.Center;
        valueLabel.characterSize = 0.012f;
        valueLabel.fontSize = 46;
        valueLabel.color = Color.white;
        ConfigureAlwaysVisibleRenderer(valueLabel.GetComponent<Renderer>());

        panelRoot.gameObject.SetActive(false);
        UpdateVisualValue();
    }

    private void UpdatePanelPose()
    {
        // The scene can contain several cameras. Use the same XR camera that the
        // anchor placer uses; Camera.main may point at a non-HMD camera.
        Camera viewer = anchorPlacer != null ? anchorPlacer.fallbackCamera : null;
        if (viewer == null)
            viewer = Camera.main;
        if (viewer == null)
            viewer = FindAnyObjectByType<Camera>();

        bool hasTrackedWrist = TryGetRightHandPosition(rightHand, rightPinchProvider, out Vector3 wristPosition);
        if (hasTrackedWrist && viewer != null && !IsUsableTrackedPosition(viewer, wristPosition))
            hasTrackedWrist = false;

        if (!hasTrackedWrist && TryGetPlacementCandidateHandPosition(viewer, out Vector3 candidateHandPosition))
        {
            wristPosition = candidateHandPosition;
            hasTrackedWrist = true;
        }
        Vector3 desiredPosition;
        if (hasTrackedWrist)
        {
            desiredPosition = wristPosition + rightHandWorldOffset;
            if (viewer != null)
            {
                Vector3 towardViewer = viewer.transform.position - desiredPosition;
                if (towardViewer.sqrMagnitude > 0.0001f)
                    desiredPosition += towardViewer.normalized * 0.035f;

                // Keep the panel inside the visible eye image even when the
                // tracked hand is close to an HMD viewport edge.
                Vector3 viewport = viewer.WorldToViewportPoint(desiredPosition);
                if (viewport.z > 0.05f)
                {
                    viewport.x = Mathf.Clamp(viewport.x, 0.14f, 0.86f);
                    viewport.y = Mathf.Clamp(viewport.y, 0.14f, 0.86f);
                    desiredPosition = viewer.ViewportToWorldPoint(viewport);
                }
            }
        }
        else if (viewer != null)
        {
            // Remain visible while hand tracking initializes. Once the wrist is
            // tracked, the panel immediately resumes right-hand following.
            desiredPosition = viewer.transform.position +
                              viewer.transform.forward * 0.48f +
                              viewer.transform.right * 0.17f -
                              viewer.transform.up * 0.05f;
        }
        else
        {
            return;
        }

        Quaternion desiredRotation = panelRoot.rotation;
        if (viewer != null)
        {
            Vector3 towardViewer = viewer.transform.position - desiredPosition;
            if (towardViewer.sqrMagnitude > 0.0001f)
                desiredRotation = Quaternion.LookRotation(-towardViewer.normalized, Vector3.up);
        }

        if (!poseStateLogged || hasTrackedWrist != lastUsedTrackedRightHand)
        {
            Vector3 viewportPoint = viewer != null
                ? viewer.WorldToViewportPoint(desiredPosition)
                : new Vector3(float.NaN, float.NaN, float.NaN);
            Debug.Log(
                $"[AnchorPlacementOpacitySlider] pose=" +
                $"{(hasTrackedWrist ? "right-wrist" : "camera-fallback")} " +
                $"camera={(viewer != null ? viewer.name : "missing")} " +
                $"position={desiredPosition:F3} viewport={viewportPoint:F3}",
                this);
            poseStateLogged = true;
            lastUsedTrackedRightHand = hasTrackedWrist;
        }

        if (!initializedPose)
        {
            panelRoot.SetPositionAndRotation(desiredPosition, desiredRotation);
            initializedPose = true;
            return;
        }

        float positionT = 1f - Mathf.Exp(-followPositionSpeed * Time.unscaledDeltaTime);
        float rotationT = 1f - Mathf.Exp(-followRotationSpeed * Time.unscaledDeltaTime);
        panelRoot.position = Vector3.Lerp(panelRoot.position, desiredPosition, positionT);
        panelRoot.rotation = Quaternion.Slerp(panelRoot.rotation, desiredRotation, rotationT);
    }

    private void UpdateLeftPinchDrag()
    {
        bool hasPinchPoint = TryGetPinchPoint(leftPinchProvider, leftHand, out Vector3 pinchPoint);
        float pinchStrength = GetLeftPinchStrength();
        bool pinching = wasPinching
            ? pinchStrength > pinchReleaseThreshold
            : pinchStrength >= pinchStartThreshold;

        // Allow grabbing after the fingers are already pinched. Requiring the
        // pinch edge to occur exactly over the narrow track was too brittle.
        if (!dragging && pinching && hasPinchPoint)
        {
            Vector3 closest = panelRoot.TransformPoint(new Vector3(ValueToTrackX(opacityMultiplier), -0.018f, 0f));
            Vector3 localPinch = panelRoot.InverseTransformPoint(pinchPoint);
            bool closeToKnob = Vector3.Distance(pinchPoint, closest) <= pinchGrabRadius;
            bool closeToTrack = Mathf.Abs(localPinch.y + 0.018f) <= pinchGrabRadius &&
                                Mathf.Abs(localPinch.x) <= trackWidth * 0.5f + pinchGrabRadius;
            dragging = closeToKnob || closeToTrack;
        }

        if (dragging && hasPinchPoint)
        {
            Vector3 localPinch = panelRoot.InverseTransformPoint(pinchPoint);
            float normalized = Mathf.InverseLerp(-trackWidth * 0.5f, trackWidth * 0.5f, localPinch.x);
            SetOpacity(Mathf.Lerp(minimumOpacity, 1f, normalized));
        }

        if (!pinching)
        {
            if (dragging && persistValue)
            {
                PlayerPrefs.SetFloat(playerPrefsKey, opacityMultiplier);
                PlayerPrefs.Save();
            }
            dragging = false;
        }

        wasPinching = pinching;
        if (deskBinder != null)
            deskBinder.SetExternalLeftRotationInputSuppressed(dragging);
    }

    private float GetLeftPinchStrength()
    {
        if (leftPinchProvider != null && leftPinchProvider.ovrHand != null)
            return leftPinchProvider.ovrHand.GetFingerPinchStrength(OVRHand.HandFinger.Index);
        if (leftHand != null)
            return leftHand.GetFingerPinchStrength(OVRHand.HandFinger.Index);
        return 0f;
    }

    private void ReleaseRotationInputOwnership()
    {
        if (deskBinder != null)
            deskBinder.SetExternalLeftRotationInputSuppressed(false);
    }

    private void SetOpacity(float value)
    {
        value = Mathf.Clamp(value, minimumOpacity, 1f);
        if (Mathf.Abs(value - opacityMultiplier) < 0.001f)
            return;

        opacityMultiplier = value;
        ApplyOpacity();
        UpdateVisualValue();
    }

    private void ApplyOpacity()
    {
        if (sceneFader != null)
            sceneFader.SetPlacementAlpha(opacityMultiplier);
        if (deskBinder != null)
            deskBinder.SetPlacementOpacityMultiplier(opacityMultiplier);
    }

    private void UpdateVisualValue()
    {
        if (knob == null || fill == null)
            return;

        float x = ValueToTrackX(opacityMultiplier);
        knob.localPosition = new Vector3(x, -0.018f, -0.012f);

        float left = -trackWidth * 0.5f;
        float width = Mathf.Max(0.002f, x - left);
        fill.localScale = new Vector3(width, 0.014f, 0.014f);
        fill.localPosition = new Vector3(left + width * 0.5f, -0.018f, -0.008f);

        if (valueLabel != null)
            valueLabel.text = $"Opacity  {opacityMultiplier:0.00}x";
    }

    private float ValueToTrackX(float value)
    {
        float normalized = Mathf.InverseLerp(minimumOpacity, 1f, value);
        return Mathf.Lerp(-trackWidth * 0.5f, trackWidth * 0.5f, normalized);
    }

    private bool TryGetPlacementCandidateHandPosition(Camera viewer, out Vector3 position)
    {
        position = default;
        if (anchorPlacer == null || !anchorPlacer.IsPlacementMode ||
            !anchorPlacer.placeDirectlyBelowHandRoot ||
            rightHand == null || !rightHand.IsTracked)
            return false;

        position = anchorPlacer.CandidatePose.position +
                   Vector3.up * Mathf.Max(0f, anchorPlacer.directHandVerticalOffsetMeters);
        return viewer == null || IsUsableTrackedPosition(viewer, position);
    }

    private static bool IsUsableTrackedPosition(Camera viewer, Vector3 position)
    {
        if (viewer == null)
            return true;

        Vector3 viewport = viewer.WorldToViewportPoint(position);
        return viewport.z > 0.05f && viewport.z < 2.5f &&
               viewport.x > -0.35f && viewport.x < 1.35f &&
               viewport.y > -0.55f && viewport.y < 1.55f;
    }

    private static bool TryGetRightHandPosition(
        OVRHand hand,
        PinchProvider provider,
        out Vector3 position)
    {
        position = default;
        OVRHand trackedHand = provider != null && provider.ovrHand != null
            ? provider.ovrHand
            : hand;
        if (trackedHand == null || !trackedHand.IsTracked)
            return false;

        OVRSkeleton skeleton = FindHandSkeleton(trackedHand);
        if (skeleton != null && skeleton.Bones != null)
        {
            for (int i = 0; i < skeleton.Bones.Count; i++)
            {
                OVRBone bone = skeleton.Bones[i];
                if (bone.Id == OVRSkeleton.BoneId.Hand_WristRoot && bone.Transform != null)
                {
                    position = bone.Transform.position;
                    return true;
                }
            }
        }

        // The OVRHand component in this scene is a data-source object whose own
        // Transform is not the rendered hand pose. Prefer its configured tips.
        if (TryGetProviderTipMidpoint(provider, out position))
            return true;

        if (trackedHand.IsPointerPoseValid)
        {
            Transform pointer = trackedHand.GetPointerRayTransform();
            if (pointer != null)
            {
                position = pointer.position;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetPinchPoint(
        PinchProvider provider,
        OVRHand hand,
        out Vector3 position)
    {
        position = default;
        if (provider != null && provider.ovrHand != null && provider.ovrHand.IsTracked &&
            TryGetProviderTipMidpoint(provider, out position))
            return true;

        if (hand == null || !hand.IsTracked)
            return false;

        OVRSkeleton skeleton = FindHandSkeleton(hand);
        if (skeleton == null || skeleton.Bones == null)
            return false;

        Transform indexTip = null;
        Transform thumbTip = null;
        for (int i = 0; i < skeleton.Bones.Count; i++)
        {
            OVRBone bone = skeleton.Bones[i];
            if (bone.Id == OVRSkeleton.BoneId.Hand_IndexTip)
                indexTip = bone.Transform;
            else if (bone.Id == OVRSkeleton.BoneId.Hand_ThumbTip)
                thumbTip = bone.Transform;
        }

        if (indexTip == null || thumbTip == null)
            return false;

        position = (indexTip.position + thumbTip.position) * 0.5f;
        return true;
    }

    private static bool TryGetProviderTipMidpoint(PinchProvider provider, out Vector3 position)
    {
        position = default;
        if (provider == null || provider.thumbTip == null || provider.indexTip == null)
            return false;

        position = (provider.thumbTip.position + provider.indexTip.position) * 0.5f;
        return true;
    }

    private static OVRSkeleton FindHandSkeleton(OVRHand hand)
    {
        if (hand == null)
            return null;

        OVRSkeleton skeleton = hand.GetComponent<OVRSkeleton>();
        if (skeleton == null)
            skeleton = hand.GetComponentInChildren<OVRSkeleton>(true);
        if (skeleton == null)
            skeleton = hand.GetComponentInParent<OVRSkeleton>();
        return skeleton;
    }

    private static Transform CreateBlock(string name, Transform parent, Vector3 scale, Vector3 localPosition, Color color)
    {
        GameObject obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
        obj.name = name;
        obj.transform.SetParent(parent, false);
        obj.transform.localPosition = localPosition;
        obj.transform.localRotation = Quaternion.identity;
        obj.transform.localScale = scale;
        Object.Destroy(obj.GetComponent<Collider>());
        SetRendererColor(obj.GetComponent<Renderer>(), color);
        return obj.transform;
    }

    private static Transform CreateSphere(string name, Transform parent, float diameter, Color color)
    {
        GameObject obj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        obj.name = name;
        obj.transform.SetParent(parent, false);
        obj.transform.localRotation = Quaternion.identity;
        obj.transform.localScale = Vector3.one * diameter;
        Object.Destroy(obj.GetComponent<Collider>());
        SetRendererColor(obj.GetComponent<Renderer>(), color);
        return obj.transform;
    }

    private static void SetRendererColor(Renderer renderer, Color color)
    {
        if (renderer == null)
            return;

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find("Unlit/Color");
        if (shader == null)
            return;

        Material material = new Material(shader) { color = color };
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", color);
        renderer.material = material;
        ConfigureAlwaysVisibleRenderer(renderer);
    }

    private static void ConfigureAlwaysVisibleRenderer(Renderer renderer)
    {
        if (renderer == null)
            return;

        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.sortingOrder = short.MaxValue;

        Material source = renderer.material;
        if (source == null)
            return;

        // TextMesh normally shares its font material, so never mutate that shared asset.
        Material material = new Material(source);
        material.renderQueue = 4000;
        material.SetInt("_ZWrite", 0);
        material.SetInt("_ZTest", (int)CompareFunction.Always);
        material.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
        renderer.material = material;
    }
}
