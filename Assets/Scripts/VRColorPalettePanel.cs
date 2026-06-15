using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
public class VRColorPalettePanel : MonoBehaviour
{
    [System.Serializable]
    public struct PaletteColor
    {
        public string name;
        public Color color;
    }

    [System.Serializable]
    public struct TargetButtonEntry
    {
        [Tooltip("Text shown on the palette button. If empty, the target object name is used.")]
        public string label;
        [Tooltip("Scene object controlled by this button. A ColorPaletteTarget is added automatically if needed.")]
        public GameObject targetObject;
    }

    [Header("Target")]
    public ColorPaletteTarget target;
    [Tooltip("If enabled, the panel will use the most recently spawned ColorPaletteTarget when target is empty.")]
    public bool autoUseLatestTarget = true;
    [Tooltip("Buttons shown at the top of the palette. Set the label and target object here.")]
    public TargetButtonEntry[] targetButtonEntries;
    [Tooltip("Used only by the editor setup menu. Runtime/editor rebuilds do not repopulate Target Button Entries from this list.")]
    public string[] autoDiscoverTargetNames = { "Usagi", "Usagi (1)", "teapot", "Teapot" };
    public bool autoAddColorTargetToDiscoveredObjects = true;

    [Header("Hands")]
    public PinchProvider leftPinch;
    public PinchProvider rightPinch;
    [Tooltip("When true, touching a swatch only selects while the hand is pinching.")]
    public bool requirePinchToSelect = false;

    [Header("Panel Layout")]
    public bool enableGradientPicker = true;
    public bool enableTargetButtons = true;
    public Vector2 targetButtonSize = new Vector2(0.082f, 0.032f);
    public float targetButtonsGap = 0.016f;
    public Vector2 gradientSize = new Vector2(0.24f, 0.14f);
    public Vector2 valueSliderSize = new Vector2(0.035f, 0.14f);
    public float gradientToSwatchesGap = 0.018f;
    public PaletteColor[] colors =
    {
        new PaletteColor { name = "White", color = Color.white },
        new PaletteColor { name = "Black", color = Color.black },
        new PaletteColor { name = "Red", color = new Color(0.95f, 0.12f, 0.10f, 1f) },
        new PaletteColor { name = "Orange", color = new Color(1.00f, 0.45f, 0.08f, 1f) },
        new PaletteColor { name = "Yellow", color = new Color(1.00f, 0.82f, 0.12f, 1f) },
        new PaletteColor { name = "Green", color = new Color(0.10f, 0.70f, 0.30f, 1f) },
        new PaletteColor { name = "Blue", color = new Color(0.10f, 0.35f, 0.95f, 1f) },
        new PaletteColor { name = "Violet", color = new Color(0.50f, 0.22f, 0.90f, 1f) }
    };
    public int columns = 4;
    public Vector2 swatchSize = new Vector2(0.055f, 0.055f);
    public Vector2 spacing = new Vector2(0.012f, 0.012f);
    public float panelThickness = 0.01f;
    public float swatchDepthOffset = -0.008f;

    [Header("Touch")]
    public float touchRadius = 0.035f;
    public float repeatDelay = 0.25f;
    public bool showTouchProbe = true;
    public float touchProbeRadius = 0.012f;

    [Header("Materials")]
    public Material panelMaterial;
    public Material swatchMaterialTemplate;
    public Material selectedFrameMaterial;
    public Material touchProbeMaterial;

    private readonly List<Swatch> swatches = new List<Swatch>();
    private readonly List<TargetButton> targetButtons = new List<TargetButton>();
    private Transform swatchRoot;
    private Transform targetButtonRoot;
    private Transform frame;
    private Transform targetFrame;
    private Transform leftProbe;
    private Transform rightProbe;
    private Transform gradientArea;
    private Transform valueSlider;
    private Transform gradientCursor;
    private Transform valueCursor;
    private Renderer valueSliderRenderer;
    private Texture2D hueSaturationTexture;
    private Texture2D valueTexture;
    private int selectedIndex = -1;
    private float nextSelectTime;
    private float currentHue = 0f;
    private float currentSaturation = 1f;
    private float currentValue = 1f;
#if UNITY_EDITOR
    private bool editorRebuildQueued;
#endif

    private class Swatch
    {
        public PaletteColor paletteColor;
        public Transform transform;
        public Collider collider;
        public Renderer renderer;
    }

    private class TargetButton
    {
        public string label;
        public ColorPaletteTarget target;
        public Transform transform;
        public Renderer renderer;
    }

    void OnEnable()
    {
        RequestRebuild();
    }

    void Awake()
    {
        RequestRebuild();
    }

    void Start()
    {
        AutoAssignPinchProvidersIfNeeded();
        ResolveTargetIfNeeded();
    }

    void OnValidate()
    {
        columns = Mathf.Max(1, columns);
        swatchSize.x = Mathf.Max(0.005f, swatchSize.x);
        swatchSize.y = Mathf.Max(0.005f, swatchSize.y);
        targetButtonSize.x = Mathf.Max(0.02f, targetButtonSize.x);
        targetButtonSize.y = Mathf.Max(0.012f, targetButtonSize.y);
        gradientSize.x = Mathf.Max(0.02f, gradientSize.x);
        gradientSize.y = Mathf.Max(0.02f, gradientSize.y);
        valueSliderSize.x = Mathf.Max(0.01f, valueSliderSize.x);
        valueSliderSize.y = Mathf.Max(0.02f, valueSliderSize.y);
        touchRadius = Mathf.Max(0.001f, touchRadius);
        repeatDelay = Mathf.Max(0.02f, repeatDelay);

        RequestRebuild();
    }

    void Update()
    {
        ResolveTargetIfNeeded();

        if (leftPinch == null || rightPinch == null)
            AutoAssignPinchProvidersIfNeeded();

        UpdateTouchProbe(leftProbe, leftPinch);
        UpdateTouchProbe(rightProbe, rightPinch);

        TrySelectWithHand(leftPinch);
        TrySelectWithHand(rightPinch);
    }

    public void SetTarget(ColorPaletteTarget newTarget)
    {
        target = newTarget;
    }

    public void Rebuild()
    {
        RequestRebuild();
    }

    public void RefreshSceneTargetsFromEditor()
    {
        RefreshTargetCandidates();
    }

    public void RebuildImmediateForEditor()
    {
        RefreshTargetCandidates();
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

        RefreshTargetCandidates();
        BuildPanel();
    }

#if UNITY_EDITOR
    private void DelayedEditorRebuild()
    {
        editorRebuildQueued = false;

        if (this == null || !isActiveAndEnabled || Application.isPlaying)
            return;

        RefreshTargetCandidates();
        BuildPanel();
    }
#endif

    private void BuildPanel()
    {
        ClearGeneratedChildren();
        swatches.Clear();
        targetButtons.Clear();

        targetButtonRoot = new GameObject("PaletteTargetButtons").transform;
        targetButtonRoot.SetParent(transform, false);
        swatchRoot = new GameObject("Swatches").transform;
        swatchRoot.SetParent(transform, false);

        int count = colors != null ? colors.Length : 0;
        int columnCount = Mathf.Max(1, columns);
        int rows = Mathf.Max(1, Mathf.CeilToInt(count / (float)columnCount));
        int targetCount = enableTargetButtons && targetButtonEntries != null ? targetButtonEntries.Length : 0;

        float targetButtonsWidth = Mathf.Max(0f, targetCount * targetButtonSize.x + Mathf.Max(0, targetCount - 1) * spacing.x);
        float targetButtonsHeight = targetCount > 0 ? targetButtonSize.y : 0f;
        float swatchesWidth = columnCount * swatchSize.x + Mathf.Max(0, columnCount - 1) * spacing.x;
        float swatchesHeight = rows * swatchSize.y + Mathf.Max(0, rows - 1) * spacing.y;
        float gradientBlockWidth = enableGradientPicker ? gradientSize.x + spacing.x + valueSliderSize.x : 0f;
        float gradientBlockHeight = enableGradientPicker ? Mathf.Max(gradientSize.y, valueSliderSize.y) : 0f;
        float contentWidth = Mathf.Max(swatchesWidth, gradientBlockWidth, targetButtonsWidth);
        float contentHeight = swatchesHeight
            + (enableGradientPicker ? gradientBlockHeight + gradientToSwatchesGap : 0f)
            + (targetCount > 0 ? targetButtonsHeight + targetButtonsGap : 0f);
        float padding = 0.035f;
        float width = contentWidth + padding;
        float height = contentHeight + padding;

        GameObject back = GameObject.CreatePrimitive(PrimitiveType.Cube);
        back.name = "PalettePanel_Back";
        back.transform.SetParent(transform, false);
        back.transform.localScale = new Vector3(width, height, panelThickness);
        back.transform.localPosition = Vector3.zero;
        ApplyMaterial(back.GetComponent<Renderer>(), panelMaterial, new Color(0.05f, 0.05f, 0.05f, 0.85f));

        float topY = height * 0.5f - padding * 0.5f;
        float swatchesTopY = topY;

        if (targetCount > 0)
        {
            float buttonY = topY - targetButtonSize.y * 0.5f;
            float startX = -targetButtonsWidth * 0.5f + targetButtonSize.x * 0.5f;

            for (int i = 0; i < targetCount; i++)
            {
                TargetButtonEntry entry = targetButtonEntries[i];
                ColorPaletteTarget entryTarget = ResolveEntryTarget(entry, autoAddColorTargetToDiscoveredObjects);
                if (entryTarget == null)
                    continue;

                Vector3 localPosition = new Vector3(startX + i * (targetButtonSize.x + spacing.x), buttonY, swatchDepthOffset);
                Transform button = CreateTargetButton(entry, localPosition);
                targetButtons.Add(new TargetButton
                {
                    label = GetEntryLabel(entry),
                    target = entryTarget,
                    transform = button,
                    renderer = button.GetComponent<Renderer>()
                });
            }

            targetFrame = new GameObject("PaletteSelectedTargetFrame").transform;
            targetFrame.SetParent(transform, false);
            targetFrame.gameObject.SetActive(false);
            CreateFrameBar(targetFrame, "Top", new Vector3(0f, (targetButtonSize.y + 0.009f) * 0.5f, 0f), new Vector3(targetButtonSize.x + 0.014f, 0.004f, panelThickness * 0.45f));
            CreateFrameBar(targetFrame, "Bottom", new Vector3(0f, -(targetButtonSize.y + 0.009f) * 0.5f, 0f), new Vector3(targetButtonSize.x + 0.014f, 0.004f, panelThickness * 0.45f));
            CreateFrameBar(targetFrame, "Left", new Vector3(-(targetButtonSize.x + 0.009f) * 0.5f, 0f, 0f), new Vector3(0.004f, targetButtonSize.y + 0.014f, panelThickness * 0.45f));
            CreateFrameBar(targetFrame, "Right", new Vector3((targetButtonSize.x + 0.009f) * 0.5f, 0f, 0f), new Vector3(0.004f, targetButtonSize.y + 0.014f, panelThickness * 0.45f));
            UpdateTargetFrame();

            swatchesTopY = topY - targetButtonSize.y - targetButtonsGap;
        }

        if (enableGradientPicker)
        {
            float gradientBlockTopY = swatchesTopY;
            float gradientY = gradientBlockTopY - gradientBlockHeight * 0.5f;
            float gradientX = -contentWidth * 0.5f + gradientSize.x * 0.5f;
            float sliderX = gradientX + gradientSize.x * 0.5f + spacing.x + valueSliderSize.x * 0.5f;

            gradientArea = CreateTexturedPad(
                "HueSaturationGradient",
                new Vector3(gradientX, gradientY, swatchDepthOffset),
                new Vector3(gradientSize.x, gradientSize.y, panelThickness * 0.75f),
                CreateHueSaturationTexture());

            valueSlider = CreateTexturedPad(
                "ValueSlider",
                new Vector3(sliderX, gradientY, swatchDepthOffset),
                new Vector3(valueSliderSize.x, valueSliderSize.y, panelThickness * 0.75f),
                CreateValueTexture());
            valueSliderRenderer = valueSlider.GetComponent<Renderer>();

            gradientCursor = CreateCursor("GradientCursor", 0.012f);
            valueCursor = CreateCursor("ValueCursor", 0.010f);
            UpdateGradientCursors();

            swatchesTopY = gradientBlockTopY - gradientBlockHeight - gradientToSwatchesGap;
        }

        for (int i = 0; i < count; i++)
        {
            int row = i / columnCount;
            int col = i % columnCount;

            float x = -contentWidth * 0.5f + swatchSize.x * 0.5f + col * (swatchSize.x + spacing.x);
            float y = swatchesTopY - swatchSize.y * 0.5f - row * (swatchSize.y + spacing.y);

            GameObject swatchObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            swatchObject.name = "PaletteSwatch_" + SafeName(colors[i].name, i);
            swatchObject.transform.SetParent(swatchRoot, false);
            swatchObject.transform.localPosition = new Vector3(x, y, swatchDepthOffset);
            swatchObject.transform.localScale = new Vector3(swatchSize.x, swatchSize.y, panelThickness * 0.75f);

            Renderer renderer = swatchObject.GetComponent<Renderer>();
            ApplyMaterial(renderer, swatchMaterialTemplate, colors[i].color);

            swatches.Add(new Swatch
            {
                paletteColor = colors[i],
                transform = swatchObject.transform,
                collider = swatchObject.GetComponent<Collider>(),
                renderer = renderer
            });
        }

        frame = new GameObject("PaletteSelectedFrame").transform;
        frame.SetParent(transform, false);
        frame.gameObject.SetActive(false);
        CreateFrameBar(frame, "Top", new Vector3(0f, (swatchSize.y + 0.012f) * 0.5f, 0f), new Vector3(swatchSize.x + 0.018f, 0.006f, panelThickness * 0.45f));
        CreateFrameBar(frame, "Bottom", new Vector3(0f, -(swatchSize.y + 0.012f) * 0.5f, 0f), new Vector3(swatchSize.x + 0.018f, 0.006f, panelThickness * 0.45f));
        CreateFrameBar(frame, "Left", new Vector3(-(swatchSize.x + 0.012f) * 0.5f, 0f, 0f), new Vector3(0.006f, swatchSize.y + 0.018f, panelThickness * 0.45f));
        CreateFrameBar(frame, "Right", new Vector3((swatchSize.x + 0.012f) * 0.5f, 0f, 0f), new Vector3(0.006f, swatchSize.y + 0.018f, panelThickness * 0.45f));

        if (showTouchProbe)
        {
            leftProbe = CreateProbe("LeftTouchProbe");
            rightProbe = CreateProbe("RightTouchProbe");
        }
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
        return childName == "PaletteTargetButtons"
            || childName == "Swatches"
            || childName == "PalettePanel_Back"
            || childName == "HueSaturationGradient"
            || childName == "ValueSlider"
            || childName == "GradientCursor"
            || childName == "ValueCursor"
            || childName == "PaletteSelectedFrame"
            || childName == "PaletteSelectedTargetFrame"
            || childName == "LeftTouchProbe"
            || childName == "RightTouchProbe";
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

    private void CreateFrameBar(Transform parentFrame, string barName, Vector3 localPosition, Vector3 localScale)
    {
        GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bar.name = "Frame_" + barName;
        bar.transform.SetParent(parentFrame, false);
        bar.transform.localPosition = localPosition;
        bar.transform.localScale = localScale;
        DestroyImmediateSafe(bar.GetComponent<Collider>());
        ApplyMaterial(bar.GetComponent<Renderer>(), selectedFrameMaterial, Color.white);
    }

    private void TrySelectWithHand(PinchProvider pinch)
    {
        if (pinch == null)
            return;
        if (requirePinchToSelect && !pinch.IsPinching)
            return;

        int targetIndex = FindTouchedTargetButton(pinch.PinchPosWorld);
        if (targetIndex >= 0)
        {
            SelectTarget(targetIndex);
            return;
        }

        if (TrySelectGradient(pinch.PinchPosWorld))
            return;

        if (Time.realtimeSinceStartup < nextSelectTime)
            return;

        int index = FindTouchedSwatch(pinch.PinchPosWorld);
        if (index < 0)
            return;

        SelectColor(index);
        nextSelectTime = Time.realtimeSinceStartup + repeatDelay;
    }

    private int FindTouchedTargetButton(Vector3 worldPosition)
    {
        float touchRadiusSqr = touchRadius * touchRadius;
        int bestIndex = -1;
        float bestDistance = float.PositiveInfinity;

        for (int i = 0; i < targetButtons.Count; i++)
        {
            TargetButton button = targetButtons[i];
            if (button == null || button.transform == null || button.target == null)
                continue;

            float distanceSqr = (worldPosition - button.transform.position).sqrMagnitude;
            if (distanceSqr <= touchRadiusSqr && distanceSqr < bestDistance)
            {
                bestDistance = distanceSqr;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private void SelectTarget(int index)
    {
        if (index < 0 || index >= targetButtons.Count)
            return;

        target = targetButtons[index].target;
        LatestColorPaletteTarget.Set(target);
        UpdateTargetFrame();
    }

    private int FindTouchedSwatch(Vector3 worldPosition)
    {
        float touchRadiusSqr = touchRadius * touchRadius;
        int bestIndex = -1;
        float bestDistance = float.PositiveInfinity;

        for (int i = 0; i < swatches.Count; i++)
        {
            Swatch swatch = swatches[i];
            if (swatch == null || swatch.transform == null)
                continue;

            float distanceSqr = (worldPosition - swatch.transform.position).sqrMagnitude;
            if (distanceSqr <= touchRadiusSqr && distanceSqr < bestDistance)
            {
                bestDistance = distanceSqr;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private void SelectColor(int index)
    {
        if (index < 0 || index >= swatches.Count)
            return;

        selectedIndex = index;
        if (frame != null)
        {
            frame.gameObject.SetActive(true);
            frame.position = swatches[index].transform.position + transform.forward * 0.006f;
            frame.rotation = swatches[index].transform.rotation;
        }

        Color color = swatches[index].paletteColor.color;
        Color.RGBToHSV(color, out currentHue, out currentSaturation, out currentValue);
        UpdateValueTexture();
        UpdateGradientCursors();

        if (target != null)
            target.ApplyColor(color);
    }

    private bool TrySelectGradient(Vector3 worldPosition)
    {
        if (!enableGradientPicker)
            return false;

        if (TryGetLocalUvOnPad(gradientArea, gradientSize, worldPosition, out float hue, out float saturation))
        {
            currentHue = Mathf.Clamp01(hue);
            currentSaturation = Mathf.Clamp01(saturation);
            ApplyGradientColor();
            return true;
        }

        if (TryGetLocalUvOnPad(valueSlider, valueSliderSize, worldPosition, out _, out float value))
        {
            currentValue = Mathf.Clamp01(value);
            ApplyGradientColor();
            return true;
        }

        return false;
    }

    private void ApplyGradientColor()
    {
        selectedIndex = -1;
        if (frame != null)
            frame.gameObject.SetActive(false);

        UpdateValueTexture();
        UpdateGradientCursors();

        if (target != null)
            target.ApplyColor(Color.HSVToRGB(currentHue, currentSaturation, currentValue));
    }

    private bool TryGetLocalUvOnPad(Transform pad, Vector2 size, Vector3 worldPosition, out float u, out float v)
    {
        u = 0f;
        v = 0f;

        if (pad == null)
            return false;

        Vector3 local = pad.InverseTransformPoint(worldPosition);
        if (Mathf.Abs(local.z) > 0.75f)
            return false;

        if (Mathf.Abs(local.x) > 0.5f || Mathf.Abs(local.y) > 0.5f)
            return false;

        u = Mathf.Clamp01(local.x + 0.5f);
        v = Mathf.Clamp01(local.y + 0.5f);
        return true;
    }

    private void ResolveTargetIfNeeded()
    {
        if (target != null || !autoUseLatestTarget)
            return;

        target = LatestColorPaletteTarget.Current;
        UpdateTargetFrame();
    }

    private void RefreshTargetCandidates()
    {
        if (!enableTargetButtons)
            return;

        List<TargetButtonEntry> entries = new List<TargetButtonEntry>();
        if (targetButtonEntries != null)
        {
            for (int i = 0; i < targetButtonEntries.Length; i++)
            {
                TargetButtonEntry entry = targetButtonEntries[i];
                ColorPaletteTarget entryTarget = ResolveEntryTarget(entry, autoAddColorTargetToDiscoveredObjects);
                if (entryTarget == null)
                {
                    entries.Add(entry);
                }
                else if (!ContainsTarget(entries, entryTarget))
                {
                    entries.Add(entry);
                }
            }
        }

        targetButtonEntries = entries.ToArray();

        if (target == null)
        {
            for (int i = 0; i < targetButtonEntries.Length; i++)
            {
                target = ResolveEntryTarget(targetButtonEntries[i], autoAddColorTargetToDiscoveredObjects);
                if (target != null)
                    break;
            }
        }
    }

    private void AutoAssignPinchProvidersIfNeeded()
    {
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

        probe.gameObject.SetActive(!requirePinchToSelect || pinch.IsPinching);
        probe.position = pinch.PinchPosWorld;
        probe.localScale = Vector3.one * (touchProbeRadius * 2f);
    }

    private Transform CreateTexturedPad(string padName, Vector3 localPosition, Vector3 localScale, Texture2D texture)
    {
        GameObject pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
        pad.name = padName;
        pad.transform.SetParent(transform, false);
        pad.transform.localPosition = localPosition;
        pad.transform.localScale = localScale;

        Renderer renderer = pad.GetComponent<Renderer>();
        ApplyTextureMaterial(renderer, texture);
        return pad.transform;
    }

    private Transform CreateTargetButton(TargetButtonEntry entry, Vector3 localPosition)
    {
        GameObject button = GameObject.CreatePrimitive(PrimitiveType.Cube);
        button.name = "TargetButton_" + SafeName(GetEntryLabel(entry), targetButtons.Count);
        button.transform.SetParent(targetButtonRoot, false);
        button.transform.localPosition = localPosition;
        button.transform.localScale = new Vector3(targetButtonSize.x, targetButtonSize.y, panelThickness * 0.75f);
        ApplyMaterial(button.GetComponent<Renderer>(), swatchMaterialTemplate, new Color(0.16f, 0.16f, 0.16f, 1f));

        GameObject labelObject = new GameObject("Label");
        labelObject.transform.SetParent(button.transform, false);
        labelObject.transform.localPosition = new Vector3(0f, 0f, -0.7f);
        labelObject.transform.localRotation = Quaternion.identity;
        labelObject.transform.localScale = Vector3.one * 0.02f;

        TextMesh label = labelObject.AddComponent<TextMesh>();
        label.text = ShortLabel(GetEntryLabel(entry));
        label.anchor = TextAnchor.MiddleCenter;
        label.alignment = TextAlignment.Center;
        label.fontSize = 32;
        label.characterSize = 0.08f;
        label.color = Color.white;

        return button.transform;
    }

    private Transform CreateCursor(string cursorName, float size)
    {
        GameObject cursor = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        cursor.name = cursorName;
        cursor.transform.SetParent(transform, false);
        cursor.transform.localScale = Vector3.one * size;
        DestroyImmediateSafe(cursor.GetComponent<Collider>());
        ApplyMaterial(cursor.GetComponent<Renderer>(), selectedFrameMaterial, Color.white);
        return cursor.transform;
    }

    private void UpdateGradientCursors()
    {
        if (gradientCursor != null && gradientArea != null)
        {
            gradientCursor.position = gradientArea.TransformPoint(new Vector3(currentHue - 0.5f, currentSaturation - 0.5f, 0f)) + transform.forward * 0.012f;
            gradientCursor.rotation = transform.rotation;
        }

        if (valueCursor != null && valueSlider != null)
        {
            valueCursor.position = valueSlider.TransformPoint(new Vector3(0f, currentValue - 0.5f, 0f)) + transform.forward * 0.012f;
            valueCursor.rotation = transform.rotation;
        }
    }

    private void UpdateTargetFrame()
    {
        if (targetFrame == null)
            return;

        targetFrame.gameObject.SetActive(false);
        if (target == null)
            return;

        for (int i = 0; i < targetButtons.Count; i++)
        {
            TargetButton button = targetButtons[i];
            if (button == null || button.target != target || button.transform == null)
                continue;

            targetFrame.gameObject.SetActive(true);
            targetFrame.position = button.transform.position + transform.forward * 0.006f;
            targetFrame.rotation = button.transform.rotation;
            return;
        }
    }

    private Texture2D CreateHueSaturationTexture()
    {
        const int width = 128;
        const int height = 128;

        if (hueSaturationTexture == null)
        {
            hueSaturationTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            hueSaturationTexture.name = "RuntimeHueSaturationGradient";
            hueSaturationTexture.wrapMode = TextureWrapMode.Clamp;
            hueSaturationTexture.filterMode = FilterMode.Bilinear;
        }

        for (int y = 0; y < height; y++)
        {
            float saturation = 1f - y / (height - 1f);
            for (int x = 0; x < width; x++)
            {
                float hue = 1f - x / (width - 1f);
                hueSaturationTexture.SetPixel(x, y, Color.HSVToRGB(hue, saturation, 1f));
            }
        }

        hueSaturationTexture.Apply();
        return hueSaturationTexture;
    }

    private Texture2D CreateValueTexture()
    {
        if (valueTexture == null)
        {
            valueTexture = new Texture2D(8, 128, TextureFormat.RGBA32, false);
            valueTexture.name = "RuntimeValueGradient";
            valueTexture.wrapMode = TextureWrapMode.Clamp;
            valueTexture.filterMode = FilterMode.Bilinear;
        }

        UpdateValueTexture();
        return valueTexture;
    }

    private void UpdateValueTexture()
    {
        if (valueTexture == null)
            return;

        int width = valueTexture.width;
        int height = valueTexture.height;
        for (int y = 0; y < height; y++)
        {
            float value = 1f - y / (height - 1f);
            Color color = Color.HSVToRGB(currentHue, currentSaturation, value);
            for (int x = 0; x < width; x++)
                valueTexture.SetPixel(x, y, color);
        }

        valueTexture.Apply();

        if (valueSliderRenderer != null && valueSliderRenderer.sharedMaterial != null)
            SetMaterialTexture(valueSliderRenderer.sharedMaterial, valueTexture);
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

    private static void ApplyTextureMaterial(Renderer renderer, Texture2D texture)
    {
        if (renderer == null)
            return;

        Material material = CreateDefaultMaterial();
        SetMaterialTexture(material, texture);
        renderer.sharedMaterial = material;
    }

    private static void SetMaterialTexture(Material material, Texture2D texture)
    {
        if (material == null || texture == null)
            return;

        if (material.HasProperty("_BaseMap"))
            material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_MainTex"))
            material.SetTexture("_MainTex", texture);
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

    private static string SafeName(string name, int index)
    {
        if (string.IsNullOrWhiteSpace(name))
            return index.ToString();

        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        return name.Replace(' ', '_');
    }

    private static string ShortLabel(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Target";

        return name.Length <= 10 ? name : name.Substring(0, 10);
    }

    private static string GetEntryLabel(TargetButtonEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.label))
            return entry.label;

        return entry.targetObject != null ? entry.targetObject.name : "Target";
    }

    private static ColorPaletteTarget ResolveEntryTarget(TargetButtonEntry entry, bool addIfMissing)
    {
        if (entry.targetObject == null)
            return null;

        ColorPaletteTarget target = entry.targetObject.GetComponent<ColorPaletteTarget>();
        if (target == null && addIfMissing)
            target = entry.targetObject.AddComponent<ColorPaletteTarget>();

        return target;
    }

    private static bool ContainsTarget(List<TargetButtonEntry> entries, ColorPaletteTarget target)
    {
        if (target == null)
            return false;

        for (int i = 0; i < entries.Count; i++)
        {
            if (ResolveEntryTarget(entries[i], false) == target)
                return true;
        }

        return false;
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
