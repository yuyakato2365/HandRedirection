using UnityEngine;

[ExecuteAlways]
public class DeskScaleSliderPanel : MonoBehaviour
{
    [System.Serializable]
    public class ScaledObject
    {
        public Transform target;
        public Vector3 initialLocalScale = Vector3.zero;
        public Vector3 initialLocalPosition = Vector3.zero;
        public Vector3 initialLocalBoundsMin = Vector3.zero;
        public Vector3 initialLocalBoundsMax = Vector3.zero;
        public bool hasInitialLocalBounds;
    }

    [Header("Target")]
    public GoGoInteractionController_NoY3 redirectionController;
    public bool autoFindRedirectionController = true;
    public bool applyToWidthScale = true;
    public bool applyToDepthScale = true;

    [Header("Scaled Scene Objects")]
    public bool autoFindScaniverseMinitable = true;
    public string scaniverseRootName = "Scaniverse 2026-05-20 114107";
    public string minitableName = "minitable";
    [Tooltip("Scene objects keep their captured size at this desk scale. Use 2 when the original desk size should be treated as 2x.")]
    public float sceneObjectReferenceScale = 2f;
    [Tooltip("Scales scene objects around the edge nearest the seated person instead of around their pivot.")]
    public bool keepSeatedSideEdgeFixed = true;
    [Tooltip("Seated-side edge in the scaled object's local space. Default -Z means the object grows toward local +Z.")]
    public Vector3 seatedSideLocalDirection = Vector3.back;
    [Tooltip("Do not scale scene object height when desk scale changes.")]
    public bool keepSceneObjectHeight = true;
    public ScaledObject[] scaledObjects = new ScaledObject[0];

    [Header("Hands")]
    public PinchProvider leftPinch;
    public PinchProvider rightPinch;
    public bool requirePinchToDrag = true;

    [Header("Range")]
    public float minScale = 1f;
    public float maxScale = 3f;
    public float currentScale = 1f;

    [Header("Layout")]
    public Vector2 panelSize = new Vector2(0.34f, 0.105f);
    public float panelThickness = 0.01f;
    public float trackWidth = 0.26f;
    public float trackHeight = 0.012f;
    public Vector2 knobSize = new Vector2(0.032f, 0.04f);
    public float labelYOffset = 0.032f;
    public float touchRadius = 0.045f;
    public bool showTouchProbe = true;
    public float touchProbeRadius = 0.012f;

    [Header("Materials")]
    public Material panelMaterial;
    public Material trackMaterial;
    public Material fillMaterial;
    public Material knobMaterial;
    public Material touchProbeMaterial;

    private Transform panelBack;
    private Transform track;
    private Transform fill;
    private Transform knob;
    private Transform valueLabel;
    private Transform leftProbe;
    private Transform rightProbe;
    private PinchProvider draggingPinch;

#if UNITY_EDITOR
    private bool editorRebuildQueued;
#endif

    private void OnEnable()
    {
        RequestRebuild();
    }

    private void Awake()
    {
        RequestRebuild();
    }

    private void Start()
    {
        AutoAssignReferencesIfNeeded();
        PullScaleFromController();
        CaptureMissingInitialScales();
        ApplyScaleToSceneObjects();
        UpdateVisuals();
    }

    private void OnValidate()
    {
        minScale = Mathf.Max(0.001f, minScale);
        maxScale = Mathf.Max(minScale + 0.001f, maxScale);
        currentScale = Mathf.Clamp(currentScale, minScale, maxScale);
        panelSize.x = Mathf.Max(0.08f, panelSize.x);
        panelSize.y = Mathf.Max(0.055f, panelSize.y);
        trackWidth = Mathf.Max(0.04f, trackWidth);
        trackHeight = Mathf.Max(0.003f, trackHeight);
        knobSize.x = Mathf.Max(0.008f, knobSize.x);
        knobSize.y = Mathf.Max(0.012f, knobSize.y);
        touchRadius = Mathf.Max(0.001f, touchRadius);
        RequestRebuild();
    }

    private void Update()
    {
        AutoAssignReferencesIfNeeded();

        if (!Application.isPlaying)
        {
            PullScaleFromController();
            CaptureMissingInitialScales();
            UpdateVisuals();
            return;
        }

        UpdateTouchProbe(leftProbe, leftPinch);
        UpdateTouchProbe(rightProbe, rightPinch);
        UpdateDrag(leftPinch);
        UpdateDrag(rightPinch);
    }

    [ContextMenu("Desk Scale Slider/Rebuild")]
    public void Rebuild()
    {
        BuildPanel();
    }

    [ContextMenu("Desk Scale Slider/Apply Current Scale")]
    public void ApplyCurrentScale()
    {
        SetScale(currentScale, true);
    }

    public void RebuildImmediateForEditor()
    {
        AutoAssignReferencesIfNeeded();
        PullScaleFromController();
        BuildPanel();
    }

    private void RequestRebuild()
    {
        if (!isActiveAndEnabled)
            return;

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            if (editorRebuildQueued)
                return;

            editorRebuildQueued = true;
            UnityEditor.EditorApplication.delayCall += DelayedEditorRebuild;
            return;
        }
#endif

        BuildPanel();
    }

#if UNITY_EDITOR
    private void DelayedEditorRebuild()
    {
        editorRebuildQueued = false;
        if (this == null || !isActiveAndEnabled || Application.isPlaying)
            return;

        AutoAssignReferencesIfNeeded();
        PullScaleFromController();
        BuildPanel();
    }
#endif

    private void BuildPanel()
    {
        ClearGeneratedChildren();

        panelBack = CreateCube("DeskScalePanel_Back", transform, Vector3.zero, new Vector3(panelSize.x, panelSize.y, panelThickness), panelMaterial, new Color(0.04f, 0.05f, 0.055f, 0.88f));
        track = CreateCube("DeskScaleSlider_Track", transform, new Vector3(0f, -0.015f, -0.008f), new Vector3(trackWidth, trackHeight, panelThickness * 0.7f), trackMaterial, new Color(0.22f, 0.24f, 0.25f, 1f));
        fill = CreateCube("DeskScaleSlider_Fill", transform, new Vector3(0f, -0.015f, -0.014f), new Vector3(trackWidth, trackHeight * 1.08f, panelThickness * 0.72f), fillMaterial, new Color(0.16f, 0.62f, 0.95f, 1f));
        knob = CreateCube("DeskScaleSlider_Knob", transform, Vector3.zero, new Vector3(knobSize.x, knobSize.y, panelThickness * 1.2f), knobMaterial, new Color(0.95f, 0.95f, 0.92f, 1f));
        valueLabel = CreateValueLabel();

        if (showTouchProbe)
        {
            leftProbe = CreateProbe("DeskScaleSlider_LeftTouchProbe");
            rightProbe = CreateProbe("DeskScaleSlider_RightTouchProbe");
        }

        UpdateVisuals();
    }

    private Transform CreateValueLabel()
    {
        GameObject labelObject = new GameObject("DeskScaleSlider_ValueLabel");
        labelObject.transform.SetParent(transform, false);
        labelObject.transform.localPosition = new Vector3(0f, labelYOffset, -0.018f);
        labelObject.transform.localRotation = Quaternion.identity;
        labelObject.transform.localScale = Vector3.one * 0.025f;

        TextMesh text = labelObject.AddComponent<TextMesh>();
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        text.fontSize = 48;
        text.characterSize = 0.08f;
        text.color = Color.white;
        return labelObject.transform;
    }

    private Transform CreateProbe(string probeName)
    {
        GameObject probe = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        probe.name = probeName;
        probe.transform.SetParent(transform, false);
        probe.transform.localScale = Vector3.one * (touchProbeRadius * 2f);
        DestroyImmediateSafe(probe.GetComponent<Collider>());
        ApplyMaterial(probe.GetComponent<Renderer>(), touchProbeMaterial, Color.white);
        probe.SetActive(false);
        return probe.transform;
    }

    private static Transform CreateCube(string name, Transform parent, Vector3 localPosition, Vector3 localScale, Material template, Color color)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = name;
        cube.transform.SetParent(parent, false);
        cube.transform.localPosition = localPosition;
        cube.transform.localScale = localScale;
        ApplyMaterial(cube.GetComponent<Renderer>(), template, color);
        return cube.transform;
    }

    private void ClearGeneratedChildren()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform child = transform.GetChild(i);
            if (IsGeneratedChild(child.name))
                DestroyImmediateSafe(child.gameObject);
        }
    }

    private static bool IsGeneratedChild(string childName)
    {
        return childName == "DeskScalePanel_Back"
            || childName == "DeskScaleSlider_Track"
            || childName == "DeskScaleSlider_Fill"
            || childName == "DeskScaleSlider_Knob"
            || childName == "DeskScaleSlider_ValueLabel"
            || childName == "DeskScaleSlider_LeftTouchProbe"
            || childName == "DeskScaleSlider_RightTouchProbe";
    }

    private void UpdateDrag(PinchProvider pinch)
    {
        if (pinch == null)
            return;

        bool pinching = !requirePinchToDrag || pinch.IsPinching;
        if (draggingPinch == pinch && !pinching)
        {
            draggingPinch = null;
            return;
        }

        if (!pinching)
            return;

        if (draggingPinch != null && draggingPinch != pinch)
            return;

        if (draggingPinch == null && !IsNearSlider(pinch.PinchPosWorld))
            return;

        draggingPinch = pinch;
        if (TryGetSliderT(pinch.PinchPosWorld, out float t))
            SetScale(Mathf.Lerp(minScale, maxScale, t), true);
    }

    private bool IsNearSlider(Vector3 worldPosition)
    {
        if (track == null)
            return false;

        Vector3 nearest = track.TransformPoint(new Vector3(Mathf.Clamp(track.InverseTransformPoint(worldPosition).x, -0.5f, 0.5f), 0f, 0f));
        return (worldPosition - nearest).sqrMagnitude <= touchRadius * touchRadius;
    }

    private bool TryGetSliderT(Vector3 worldPosition, out float t)
    {
        t = 0f;
        if (track == null)
            return false;

        Vector3 local = track.InverseTransformPoint(worldPosition);
        t = Mathf.Clamp01(local.x + 0.5f);
        return true;
    }

    private void SetScale(float value, bool applyToController)
    {
        currentScale = Mathf.Clamp(value, minScale, maxScale);
        if (applyToController)
            ApplyScaleToController();

        ApplyScaleToSceneObjects();
        UpdateVisuals();
    }

    private void ApplyScaleToController()
    {
        GoGoInteractionController_NoY3 controller = ResolveRedirectionController();
        if (controller == null)
            return;

        if (applyToWidthScale)
            controller.deskWidthScale = currentScale;
        if (applyToDepthScale)
            controller.deskDepthScale = currentScale;
    }

    private void PullScaleFromController()
    {
        GoGoInteractionController_NoY3 controller = ResolveRedirectionController();
        if (controller == null)
            return;

        if (applyToWidthScale)
            currentScale = controller.deskWidthScale;
        else if (applyToDepthScale)
            currentScale = controller.deskDepthScale;

        currentScale = Mathf.Clamp(currentScale, minScale, maxScale);
    }

    private void ApplyScaleToSceneObjects()
    {
        CaptureMissingInitialScales();

        if (scaledObjects == null)
            return;

        for (int i = 0; i < scaledObjects.Length; i++)
        {
            ScaledObject scaled = scaledObjects[i];
            if (scaled == null || scaled.target == null)
                continue;

            float referenceScale = Mathf.Max(0.001f, sceneObjectReferenceScale);
            float factor = currentScale / referenceScale;
            scaled.target.localScale = new Vector3(
                scaled.initialLocalScale.x * factor,
                keepSceneObjectHeight ? scaled.initialLocalScale.y : scaled.initialLocalScale.y * factor,
                scaled.initialLocalScale.z * factor);

            if (keepSeatedSideEdgeFixed)
                scaled.target.localPosition = scaled.initialLocalPosition + ComputeFixedEdgeOffset(scaled, factor);
        }
    }

    private void CaptureMissingInitialScales()
    {
        AutoFindMinitableIfNeeded();

        if (scaledObjects == null)
            return;

        for (int i = 0; i < scaledObjects.Length; i++)
        {
            ScaledObject scaled = scaledObjects[i];
            if (scaled == null || scaled.target == null)
                continue;
            if (scaled.initialLocalScale.sqrMagnitude <= 1e-8f)
                scaled.initialLocalScale = scaled.target.localScale;
            if (!scaled.hasInitialLocalBounds)
            {
                scaled.initialLocalPosition = scaled.target.localPosition;
                scaled.hasInitialLocalBounds = TryGetLocalRenderBounds(scaled.target, out scaled.initialLocalBoundsMin, out scaled.initialLocalBoundsMax);
            }
        }
    }

    private void AutoFindMinitableIfNeeded()
    {
        if (!autoFindScaniverseMinitable || HasScaledTarget())
            return;

        Transform root = FindSceneTransformByName(scaniverseRootName, null);
        Transform minitable = FindSceneTransformByName(minitableName, root);
        if (minitable == null)
            return;

        bool hasBounds = TryGetLocalRenderBounds(minitable, out Vector3 boundsMin, out Vector3 boundsMax);
        scaledObjects = new[]
        {
            new ScaledObject
            {
                target = minitable,
                initialLocalScale = minitable.localScale,
                initialLocalPosition = minitable.localPosition,
                hasInitialLocalBounds = hasBounds,
                initialLocalBoundsMin = boundsMin,
                initialLocalBoundsMax = boundsMax
            }
        };
    }

    private Vector3 ComputeFixedEdgeOffset(ScaledObject scaled, float factor)
    {
        if (scaled == null || !scaled.hasInitialLocalBounds)
            return Vector3.zero;

        Vector3 localDirection = seatedSideLocalDirection.sqrMagnitude > 1e-8f
            ? seatedSideLocalDirection.normalized
            : Vector3.back;

        Vector3 halfSize = (scaled.initialLocalBoundsMax - scaled.initialLocalBoundsMin) * 0.5f;
        Vector3 localOffset = new Vector3(
            localDirection.x * halfSize.x * (factor - 1f),
            keepSceneObjectHeight ? 0f : localDirection.y * halfSize.y * (factor - 1f),
            localDirection.z * halfSize.z * (factor - 1f));

        return -localOffset;
    }

    private static bool TryGetLocalRenderBounds(Transform target, out Vector3 min, out Vector3 max)
    {
        min = Vector3.zero;
        max = Vector3.zero;
        if (target == null)
            return false;

        Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
        bool hasBounds = false;
        Matrix4x4 worldToLocal = target.worldToLocalMatrix;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null)
                continue;

            Bounds bounds = renderer.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 worldCorner = new Vector3(
                    (corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                    (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                Vector3 localCorner = worldToLocal.MultiplyPoint3x4(worldCorner);

                if (!hasBounds)
                {
                    min = localCorner;
                    max = localCorner;
                    hasBounds = true;
                }
                else
                {
                    min = Vector3.Min(min, localCorner);
                    max = Vector3.Max(max, localCorner);
                }
            }
        }

        return hasBounds;
    }

    private bool HasScaledTarget()
    {
        if (scaledObjects == null)
            return false;

        for (int i = 0; i < scaledObjects.Length; i++)
        {
            if (scaledObjects[i] != null && scaledObjects[i].target != null)
                return true;
        }

        return false;
    }

    private void UpdateVisuals()
    {
        float t = Mathf.InverseLerp(minScale, maxScale, currentScale);
        float x = Mathf.Lerp(-trackWidth * 0.5f, trackWidth * 0.5f, t);
        float trackY = -0.015f;

        if (knob != null)
            knob.localPosition = new Vector3(x, trackY, -0.02f);

        if (fill != null)
        {
            float fillWidth = Mathf.Max(0.001f, trackWidth * t);
            fill.localScale = new Vector3(fillWidth, trackHeight * 1.08f, panelThickness * 0.72f);
            fill.localPosition = new Vector3(-trackWidth * 0.5f + fillWidth * 0.5f, trackY, -0.014f);
        }

        TextMesh text = valueLabel != null ? valueLabel.GetComponent<TextMesh>() : null;
        if (text != null)
            text.text = currentScale.ToString("0.00") + "x";
    }

    private void AutoAssignReferencesIfNeeded()
    {
        if (autoFindRedirectionController && !IsUsableRedirectionController(redirectionController))
            redirectionController = FindBestRedirectionController();

        if (leftPinch != null && rightPinch != null)
            return;

        PinchProvider[] providers = FindObjectsByType<PinchProvider>(FindObjectsSortMode.None);
        for (int i = 0; i < providers.Length; i++)
        {
            PinchProvider provider = providers[i];
            if (provider == null)
                continue;

            bool isLeft = IsLikelyLeft(provider);
            if (isLeft && leftPinch == null)
                leftPinch = provider;
            else if (!isLeft && rightPinch == null)
                rightPinch = provider;
        }
    }

    private GoGoInteractionController_NoY3 ResolveRedirectionController()
    {
        if (autoFindRedirectionController && !IsUsableRedirectionController(redirectionController))
            redirectionController = FindBestRedirectionController();

        return redirectionController;
    }

    public static GoGoInteractionController_NoY3 FindBestRedirectionController()
    {
        GoGoInteractionController_NoY3[] controllers = FindObjectsByType<GoGoInteractionController_NoY3>(FindObjectsSortMode.None);
        GoGoInteractionController_NoY3 best = null;
        int bestScore = int.MinValue;

        for (int i = 0; i < controllers.Length; i++)
        {
            GoGoInteractionController_NoY3 controller = controllers[i];
            if (controller == null)
                continue;

            int score = ScoreRedirectionController(controller);
            if (score > bestScore)
            {
                best = controller;
                bestScore = score;
            }
        }

        return best;
    }

    private static bool IsUsableRedirectionController(GoGoInteractionController_NoY3 controller)
    {
        return controller != null && ScoreRedirectionController(controller) >= 20;
    }

    private static int ScoreRedirectionController(GoGoInteractionController_NoY3 controller)
    {
        if (controller == null)
            return int.MinValue;

        int score = 0;
        if (controller.isActiveAndEnabled)
            score += 4;
        if (controller.deskOrigin != null)
            score += 12;
        if (controller.leftHandOriginal != null || controller.rightHandOriginal != null)
            score += 8;
        if (controller.leftHandRedirector != null || controller.rightHandRedirector != null)
            score += 8;
        if (controller.objects != null && controller.objects.Count > 0)
            score += 10 + controller.objects.Count;
        if (controller.cubeWarped != null)
            score += 3;

        return score;
    }

    private static Transform FindSceneTransformByName(string objectName, Transform root)
    {
        if (string.IsNullOrWhiteSpace(objectName))
            return null;

        Transform[] transforms = Resources.FindObjectsOfTypeAll<Transform>();
        for (int i = 0; i < transforms.Length; i++)
        {
            Transform candidate = transforms[i];
            if (candidate == null || candidate.gameObject == null)
                continue;
            if (!candidate.gameObject.scene.IsValid())
                continue;
            if (root != null && !candidate.IsChildOf(root))
                continue;
            if (string.Equals(candidate.name, objectName, System.StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }

    private static bool IsLikelyLeft(PinchProvider provider)
    {
        if (provider == null)
            return false;

        string text = provider.name + " " + (provider.ovrHand != null ? provider.ovrHand.name : string.Empty);
        text = text.ToLowerInvariant();
        return text.Contains("left") || text.Contains("_l") || text.Contains("-l");
    }

    private void UpdateTouchProbe(Transform probe, PinchProvider pinch)
    {
        if (!showTouchProbe || probe == null || pinch == null)
            return;

        probe.gameObject.SetActive(!requirePinchToDrag || pinch.IsPinching);
        probe.position = pinch.PinchPosWorld;
        probe.localScale = Vector3.one * (touchProbeRadius * 2f);
    }

    private static void ApplyMaterial(Renderer renderer, Material template, Color color)
    {
        if (renderer == null)
            return;

        Material material = template != null ? new Material(template) : CreateDefaultMaterial();
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", color);

        renderer.sharedMaterial = material;
    }

    private static Material CreateDefaultMaterial()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");
        if (shader == null)
            shader = Shader.Find("Unlit/Color");

        return shader != null ? new Material(shader) : new Material(Shader.Find("Sprites/Default"));
    }

    private static void DestroyImmediateSafe(Object obj)
    {
        if (obj == null)
            return;

        if (Application.isPlaying)
            Destroy(obj);
        else
            DestroyImmediate(obj);
    }
}
