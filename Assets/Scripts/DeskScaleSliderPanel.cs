using System.Collections.Generic;
using System.IO;
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
        public Quaternion initialLocalRotation = Quaternion.identity;
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
    [Tooltip("Optional explicit desk used as the primary width/depth reference. Assigning this is the easiest way to use a newly added desk. It is also scaled by the slider.")]
    public Transform primaryDeskReference;
    [Tooltip("Explicit Scaniverse room roots whose horizontal mesh should deform with the primary desk footprint. When empty, Scaniverse Root Name is used as a fallback.")]
    public Transform[] scaniverseDeformationRoots = new Transform[0];
    public bool autoFindScaniverseMinitable = true;
    public string scaniverseRootName = "Scaniverse 2026-06-17 213301";
    public string minitableName = "minitable";
    [Tooltip("Scene objects keep their captured size at this desk scale. Use 1 when the manually placed desk is the baseline size.")]
    public float sceneObjectReferenceScale = 1f;
    [Tooltip("Use the current scene transform as the scale baseline when Play starts. This preserves manual desk placement.")]
    public bool recaptureSceneObjectBaselineOnStart = true;
    [Tooltip("If enabled, the current scale is applied to scene objects immediately on Play start. Leave off to avoid moving a manually placed desk.")]
    public bool applySceneObjectScaleOnStart = false;
    [Tooltip("Scales scene objects around the edge nearest the seated person instead of around their pivot.")]
    public bool keepSeatedSideEdgeFixed = true;
    [Tooltip("Choose the fixed edge from the edge nearest RedirectionOrigin. If RedirectionOrigin is missing, seatedSideLocalDirection is used as a fallback.")]
    public bool chooseFixedEdgeFromRedirectionOrigin = true;
    [Tooltip("Seated-side edge in the scaled object's local space. Default -Z means the object grows toward local +Z.")]
    public Vector3 seatedSideLocalDirection = Vector3.back;
    [Tooltip("Do not scale scene object height when desk scale changes.")]
    public bool keepSceneObjectHeight = true;
    public ScaledObject[] scaledObjects = new ScaledObject[0];

    [Header("UI Position Follow")]
    [Tooltip("Move the desk slider with the scaled desk. Child UI, such as the color palette, follows through the Transform hierarchy.")]
    public bool moveUiWithDeskScale = true;
    [Tooltip("Move followed UI only along its parent-local Z axis, preserving its manually placed X/Y position.")]
    public bool moveUiOnlyAlongParentLocalZ = true;
    [Tooltip("Multiplier for UI movement away from its 1x position. 0.85 makes the 3x desk position appear at about 2.7x while keeping 1x unchanged.")]
    [Range(0f, 1.5f)] public float uiFollowMovementRatio = 0.85f;
    [Tooltip("Defaults to this DeskScaleSliderPanel transform.")]
    public Transform deskSliderToMove;

    [Header("VR Flicker Diagnostics")]
    [Tooltip("Logs slider/desk/palette pose jumps, renderer state changes, and long frames without changing their transforms.")]
    public bool logUiFlickerDiagnostics = true;
    public float diagnosticPositionJumpMeters = 0.005f;
    public float diagnosticRotationJumpDegrees = 0.5f;
    public float diagnosticLongFrameSeconds = 0.05f;
    public float diagnosticLogCooldownSeconds = 0.25f;

    [Header("Scaniverse Partial Deformation")]
    [Tooltip("Deforms the Scaniverse room continuously from the desk footprint so the scaled area stays connected to the surrounding mesh.")]
    public bool deformScaniverseRoomWithDeskScale = true;
    [Tooltip("Expand the desk-width interval in the desk X direction and move the outside on the unfixed side to keep the mesh connected.")]
    public bool deformScaniverseWidthBand = true;
    [Tooltip("Expand the desk-depth interval in the desk Z direction and move the outside on the unfixed side to keep the mesh connected.")]
    public bool deformScaniverseDepthBand = true;
    public bool recalculateScaniverseDeformedBounds = true;

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
    private Matrix4x4 dragTrackWorldToLocal;
    private bool hasDragTrackFrame;
    private readonly List<ScaniverseMeshState> scaniverseMeshStates = new List<ScaniverseMeshState>();
    private readonly ScaledObject explicitPrimaryDeskState = new ScaledObject();
    private Transform uiFollowDesk;
    private Vector3 deskSliderInitialDeskLocalPosition;
    private Vector3 deskSliderInitialParentLocalPosition;
    private bool hasDeskSliderFollowBaseline;
    private Transform diagnosticPalette;
    private Renderer[] diagnosticRenderers = new Renderer[0];
    private bool[] diagnosticRendererStates = new bool[0];
    private Vector3 diagnosticLastSliderWorldPosition;
    private Vector3 diagnosticLastSliderLocalPosition;
    private Quaternion diagnosticLastSliderWorldRotation;
    private Vector3 diagnosticLastDeskWorldPosition;
    private Quaternion diagnosticLastDeskWorldRotation;
    private Vector3 diagnosticLastPaletteLocalPosition;
    private int diagnosticLastEnabledRendererCount;
    private float diagnosticNextLogTime;
    private bool diagnosticInitialized;
    private string diagnosticLogPath;

#if UNITY_EDITOR
    private bool editorRebuildQueued;
#endif

    private sealed class ScaniverseMeshState
    {
        public Transform deformationRoot;
        public MeshFilter meshFilter;
        public Mesh mesh;
        public Vector3[] originalVertices;
        public Vector3[] deformedVertices;
    }

    private void OnEnable()
    {
        diagnosticInitialized = false;
        RequestRebuild();
    }

    private void Start()
    {
        AutoAssignReferencesIfNeeded();
        PullScaleFromController();
        if (recaptureSceneObjectBaselineOnStart)
            RecaptureSceneObjectBaselines();
        else
        {
            CaptureMissingInitialScales();
            CaptureUiFollowBaselines(true);
        }

        if (applySceneObjectScaleOnStart)
            ApplyScaleToSceneObjects();
        else
            ApplyScaniverseRoomDeformation();

        UpdateVisuals();
    }

    private void LateUpdate()
    {
        if (!Application.isPlaying || !logUiFlickerDiagnostics)
            return;

        UpdateUiFlickerDiagnostics();
    }

    private void OnDisable()
    {
        RestoreScaniverseMeshes();
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
        uiFollowMovementRatio = Mathf.Clamp(uiFollowMovementRatio, 0f, 1.5f);
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
            {
                if (Application.isPlaying)
                    child.gameObject.SetActive(false);
                DestroyImmediateSafe(child.gameObject);
            }
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
            hasDragTrackFrame = false;
            return;
        }

        if (!pinching)
            return;

        if (draggingPinch != null && draggingPinch != pinch)
            return;

        if (draggingPinch == null && !IsNearSlider(pinch.PinchPosWorld))
            return;

        if (draggingPinch == null)
        {
            draggingPinch = pinch;
            dragTrackWorldToLocal = track.worldToLocalMatrix;
            hasDragTrackFrame = true;
        }
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

        Vector3 local = hasDragTrackFrame && draggingPinch != null
            ? dragTrackWorldToLocal.MultiplyPoint3x4(worldPosition)
            : track.InverseTransformPoint(worldPosition);
        t = Mathf.Clamp01(local.x + 0.5f);
        return true;
    }

    private void SetScale(float value, bool applyToController)
    {
        currentScale = Mathf.Clamp(value, minScale, maxScale);
        if (applyToController)
            ApplyScaleToController();

        ApplyScaleToSceneObjects();
        ApplyScaniverseRoomDeformation();
        UpdateVisuals();
    }

    public void SetScaniverseRoomDeformationEnabled(bool enabled)
    {
        deformScaniverseRoomWithDeskScale = enabled;
        if (enabled)
            ApplyScaniverseRoomDeformation();
        else
            RestoreScaniverseMeshes();
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

        ScaledObject explicitDesk = GetExplicitPrimaryDeskState();
        ApplyScaleToSceneObject(explicitDesk);

        if (scaledObjects == null)
        {
            ApplyUiFollowPositions();
            return;
        }

        for (int i = 0; i < scaledObjects.Length; i++)
        {
            ScaledObject scaled = scaledObjects[i];
            if (scaled == null || scaled.target == null || IsSameTarget(scaled, explicitDesk))
                continue;

            ApplyScaleToSceneObject(scaled);
        }

        ApplyUiFollowPositions();
    }

    private void ApplyScaleToSceneObject(ScaledObject scaled)
    {
        if (scaled == null || scaled.target == null)
            return;

        float referenceScale = Mathf.Max(0.001f, sceneObjectReferenceScale);
        float factor = currentScale / referenceScale;
        scaled.target.localScale = new Vector3(
            scaled.initialLocalScale.x * factor,
            keepSceneObjectHeight ? scaled.initialLocalScale.y : scaled.initialLocalScale.y * factor,
            scaled.initialLocalScale.z * factor);

        if (keepSeatedSideEdgeFixed)
            scaled.target.localPosition = scaled.initialLocalPosition + ComputeFixedEdgeOffset(scaled, factor);
    }

    private void CaptureMissingInitialScales()
    {
        AutoFindMinitableIfNeeded();

        CaptureMissingInitialScale(GetExplicitPrimaryDeskState());

        if (scaledObjects == null)
            return;

        for (int i = 0; i < scaledObjects.Length; i++)
        {
            ScaledObject scaled = scaledObjects[i];
            if (scaled == null || scaled.target == null)
                continue;

            CaptureMissingInitialScale(scaled);
        }
    }

    private static void CaptureMissingInitialScale(ScaledObject scaled)
    {
        if (scaled == null || scaled.target == null)
            return;

        if (scaled.initialLocalScale.sqrMagnitude <= 1e-8f)
            scaled.initialLocalScale = scaled.target.localScale;
        if (IsZeroQuaternion(scaled.initialLocalRotation))
            scaled.initialLocalRotation = scaled.target.localRotation;
        if (!scaled.hasInitialLocalBounds)
        {
            scaled.initialLocalPosition = scaled.target.localPosition;
            scaled.hasInitialLocalBounds = TryGetLocalRenderBounds(scaled.target, out scaled.initialLocalBoundsMin, out scaled.initialLocalBoundsMax);
        }
    }

    [ContextMenu("Desk Scale Slider/Recapture Scene Object Baselines")]
    public void RecaptureSceneObjectBaselines()
    {
        AutoFindMinitableIfNeeded();

        CaptureSceneObjectBaseline(GetExplicitPrimaryDeskState());

        if (scaledObjects == null)
        {
            ResetScaniverseMeshCache();
            CaptureUiFollowBaselines(true);
            return;
        }

        for (int i = 0; i < scaledObjects.Length; i++)
        {
            ScaledObject scaled = scaledObjects[i];
            if (scaled == null || scaled.target == null || IsSameTarget(scaled, explicitPrimaryDeskState))
                continue;

            CaptureSceneObjectBaseline(scaled);
        }

        ResetScaniverseMeshCache();
        CaptureUiFollowBaselines(true);
    }

    private static void CaptureSceneObjectBaseline(ScaledObject scaled)
    {
        if (scaled == null || scaled.target == null)
            return;

        scaled.initialLocalScale = scaled.target.localScale;
        scaled.initialLocalPosition = scaled.target.localPosition;
        scaled.initialLocalRotation = scaled.target.localRotation;
        scaled.hasInitialLocalBounds = TryGetLocalRenderBounds(scaled.target, out scaled.initialLocalBoundsMin, out scaled.initialLocalBoundsMax);
    }

    private void CaptureUiFollowBaselines(bool force)
    {
        if (!moveUiWithDeskScale)
            return;

        ResolveUiFollowReference();
        ScaledObject desk = GetPrimaryScaledObject();
        if (desk == null || desk.target == null)
            return;

        uiFollowDesk = desk.target;
        if (deskSliderToMove != null && (force || !hasDeskSliderFollowBaseline))
        {
            deskSliderInitialDeskLocalPosition = uiFollowDesk.InverseTransformPoint(deskSliderToMove.position);
            deskSliderInitialParentLocalPosition = deskSliderToMove.localPosition;
            hasDeskSliderFollowBaseline = true;
        }

    }

    private void ApplyUiFollowPositions()
    {
        if (!moveUiWithDeskScale)
            return;

        if (uiFollowDesk == null)
            CaptureUiFollowBaselines(false);
        if (uiFollowDesk == null)
            return;

        if (deskSliderToMove != null && hasDeskSliderFollowBaseline && !deskSliderToMove.IsChildOf(uiFollowDesk))
            ApplyUiFollowPosition(deskSliderToMove, deskSliderInitialDeskLocalPosition, deskSliderInitialParentLocalPosition);
    }

    private void ApplyUiFollowPosition(Transform target, Vector3 initialDeskLocalPosition, Vector3 initialParentLocalPosition)
    {
        Vector3 followedWorldPosition = uiFollowDesk.TransformPoint(initialDeskLocalPosition);
        if (!moveUiOnlyAlongParentLocalZ)
        {
            Vector3 initialWorldPosition = target.parent != null
                ? target.parent.TransformPoint(initialParentLocalPosition)
                : initialParentLocalPosition;
            Vector3 desiredWorldPosition = initialWorldPosition
                + (followedWorldPosition - initialWorldPosition) * uiFollowMovementRatio;
            if ((target.position - desiredWorldPosition).sqrMagnitude > 0.0000000001f)
                target.position = desiredWorldPosition;
            return;
        }

        if (target.parent == null)
        {
            float desiredZ = initialParentLocalPosition.z
                + (followedWorldPosition.z - initialParentLocalPosition.z) * uiFollowMovementRatio;
            Vector3 desiredWorldPosition = new Vector3(initialParentLocalPosition.x, initialParentLocalPosition.y, desiredZ);
            if ((target.position - desiredWorldPosition).sqrMagnitude > 0.0000000001f)
                target.position = desiredWorldPosition;
            return;
        }

        Vector3 followedParentLocalPosition = target.parent.InverseTransformPoint(followedWorldPosition);
        float desiredLocalZ = initialParentLocalPosition.z
            + (followedParentLocalPosition.z - initialParentLocalPosition.z) * uiFollowMovementRatio;
        Vector3 desiredLocalPosition = new Vector3(
            initialParentLocalPosition.x,
            initialParentLocalPosition.y,
            desiredLocalZ);
        if ((target.localPosition - desiredLocalPosition).sqrMagnitude > 0.0000000001f)
            target.localPosition = desiredLocalPosition;
    }

    private void ResolveUiFollowReference()
    {
        if (deskSliderToMove == null)
            deskSliderToMove = transform;
    }

    private ScaledObject GetPrimaryScaledObject()
    {
        ScaledObject explicitDesk = GetExplicitPrimaryDeskState();
        if (explicitDesk != null)
            return explicitDesk;

        if (scaledObjects == null)
            return null;

        for (int i = 0; i < scaledObjects.Length; i++)
        {
            if (scaledObjects[i] != null && scaledObjects[i].target != null)
                return scaledObjects[i];
        }

        return null;
    }

    private ScaledObject GetExplicitPrimaryDeskState()
    {
        if (primaryDeskReference == null)
        {
            explicitPrimaryDeskState.target = null;
            return null;
        }

        if (explicitPrimaryDeskState.target != primaryDeskReference)
        {
            explicitPrimaryDeskState.target = primaryDeskReference;
            explicitPrimaryDeskState.initialLocalScale = Vector3.zero;
            explicitPrimaryDeskState.initialLocalPosition = Vector3.zero;
            explicitPrimaryDeskState.initialLocalRotation = Quaternion.identity;
            explicitPrimaryDeskState.initialLocalBoundsMin = Vector3.zero;
            explicitPrimaryDeskState.initialLocalBoundsMax = Vector3.zero;
            explicitPrimaryDeskState.hasInitialLocalBounds = false;
        }

        return explicitPrimaryDeskState;
    }

    private static bool IsSameTarget(ScaledObject left, ScaledObject right)
    {
        return left != null
            && right != null
            && left.target != null
            && left.target == right.target;
    }

    private void AutoFindMinitableIfNeeded()
    {
        if (primaryDeskReference != null || !autoFindScaniverseMinitable || HasScaledTarget())
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
                initialLocalRotation = minitable.localRotation,
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

        if (!TryGetFixedEdgeLocalCoordinate(scaled, out Vector3 fixedEdgeCoordinate))
            return Vector3.zero;

        Quaternion initialRotation = IsZeroQuaternion(scaled.initialLocalRotation)
            ? scaled.target.localRotation
            : scaled.initialLocalRotation;

        Vector3 initialEdgeOffset = Vector3.Scale(scaled.initialLocalScale, fixedEdgeCoordinate);
        Vector3 scaledEdgeOffset = Vector3.Scale(scaled.target.localScale, fixedEdgeCoordinate);
        return initialRotation * (initialEdgeOffset - scaledEdgeOffset);
    }

    private bool TryGetFixedEdgeLocalCoordinate(ScaledObject scaled, out Vector3 fixedEdgeCoordinate)
    {
        fixedEdgeCoordinate = Vector3.zero;
        if (scaled == null || scaled.target == null || !scaled.hasInitialLocalBounds)
            return false;

        BoundsEdges edges = new BoundsEdges(scaled.initialLocalBoundsMin, scaled.initialLocalBoundsMax);
        if (chooseFixedEdgeFromRedirectionOrigin && TryGetRedirectionOriginTransform(out Transform redirectionOrigin))
        {
            Vector3 originLocal = scaled.target.InverseTransformPoint(redirectionOrigin.position);
            float minXDistance = SqrDistanceToXEdge(originLocal, edges.minX, edges);
            float maxXDistance = SqrDistanceToXEdge(originLocal, edges.maxX, edges);
            float minZDistance = SqrDistanceToZEdge(originLocal, edges.minZ, edges);
            float maxZDistance = SqrDistanceToZEdge(originLocal, edges.maxZ, edges);

            float bestDistance = minXDistance;
            fixedEdgeCoordinate = new Vector3(edges.minX, 0f, 0f);

            if (maxXDistance < bestDistance)
            {
                bestDistance = maxXDistance;
                fixedEdgeCoordinate = new Vector3(edges.maxX, 0f, 0f);
            }

            if (minZDistance < bestDistance)
            {
                bestDistance = minZDistance;
                fixedEdgeCoordinate = new Vector3(0f, 0f, edges.minZ);
            }

            if (maxZDistance < bestDistance)
                fixedEdgeCoordinate = new Vector3(0f, 0f, edges.maxZ);

            return true;
        }

        Vector3 fallbackDirection = seatedSideLocalDirection.sqrMagnitude > 1e-8f
            ? seatedSideLocalDirection.normalized
            : Vector3.back;
        if (Mathf.Abs(fallbackDirection.x) > Mathf.Abs(fallbackDirection.z))
            fixedEdgeCoordinate = new Vector3(fallbackDirection.x < 0f ? edges.minX : edges.maxX, 0f, 0f);
        else
            fixedEdgeCoordinate = new Vector3(0f, 0f, fallbackDirection.z < 0f ? edges.minZ : edges.maxZ);

        return true;
    }

    private bool TryGetRedirectionOriginTransform(out Transform redirectionOrigin)
    {
        redirectionOrigin = null;

        GoGoInteractionController_NoY3 controller = ResolveRedirectionController();
        if (controller != null && controller.redirectionOrigin != null)
        {
            redirectionOrigin = controller.redirectionOrigin;
            return true;
        }

        SpatialAnchorToDeskOriginBinder binder = FindAnyObjectByType<SpatialAnchorToDeskOriginBinder>();
        if (binder != null && binder.redirectionOrigin != null)
        {
            redirectionOrigin = binder.redirectionOrigin;
            return true;
        }

        return false;
    }

    private void ApplyScaniverseRoomDeformation()
    {
        if (!Application.isPlaying)
            return;
        if (!deformScaniverseRoomWithDeskScale)
        {
            RestoreScaniverseMeshes();
            return;
        }
        if (scaledObjects == null || scaledObjects.Length == 0)
            return;

        ScaledObject desk = FindPrimaryScaledObject();
        if (desk == null || desk.target == null || !desk.hasInitialLocalBounds)
            return;

        float referenceScale = Mathf.Max(0.001f, sceneObjectReferenceScale);
        float widthFactor = applyToWidthScale ? currentScale / referenceScale : 1f;
        float depthFactor = applyToDepthScale ? currentScale / referenceScale : 1f;
        TryReadControllerScaleFactors(referenceScale, ref widthFactor, ref depthFactor);

        if (HasExplicitScaniverseDeformationRoots())
        {
            for (int i = 0; i < scaniverseDeformationRoots.Length; i++)
            {
                Transform root = scaniverseDeformationRoots[i];
                if (root != null)
                    ApplyScaniverseRoomDeformationToRoot(desk, root, widthFactor, depthFactor);
            }
            return;
        }

        Transform fallbackRoot = FindSceneTransformByName(scaniverseRootName, null);
        if (fallbackRoot != null)
            ApplyScaniverseRoomDeformationToRoot(desk, fallbackRoot, widthFactor, depthFactor);
    }

    private void ApplyScaniverseRoomDeformationToRoot(
        ScaledObject desk,
        Transform scaniverseRoot,
        float widthFactor,
        float depthFactor)
    {
        EnsureScaniverseMeshCache(scaniverseRoot);

        Matrix4x4 initialDeskLocalToRootLocal;
        if (!TryGetInitialDeskLocalToRootLocal(desk, scaniverseRoot, out initialDeskLocalToRootLocal))
            return;

        Matrix4x4 rootLocalToInitialDeskLocal = initialDeskLocalToRootLocal.inverse;
        BoundsEdges edges = new BoundsEdges(desk.initialLocalBoundsMin, desk.initialLocalBoundsMax);
        Vector3 fixedCoordinate = ResolveFixedScaleCoordinate(desk, scaniverseRoot, rootLocalToInitialDeskLocal, edges);
        Matrix4x4 rootLocalToWorld = scaniverseRoot.localToWorldMatrix;
        Matrix4x4 worldToRootLocal = scaniverseRoot.worldToLocalMatrix;
        for (int stateIndex = 0; stateIndex < scaniverseMeshStates.Count; stateIndex++)
        {
            ScaniverseMeshState state = scaniverseMeshStates[stateIndex];
            if (state == null
                || state.deformationRoot != scaniverseRoot
                || state.meshFilter == null
                || state.mesh == null
                || state.originalVertices == null)
                continue;

            Matrix4x4 meshLocalToRootLocal = worldToRootLocal * state.meshFilter.transform.localToWorldMatrix;
            Matrix4x4 rootLocalToMeshLocal = state.meshFilter.transform.worldToLocalMatrix * rootLocalToWorld;
            if (state.deformedVertices == null || state.deformedVertices.Length != state.originalVertices.Length)
                state.deformedVertices = new Vector3[state.originalVertices.Length];

            for (int i = 0; i < state.originalVertices.Length; i++)
            {
                Vector3 rootLocal = meshLocalToRootLocal.MultiplyPoint3x4(state.originalVertices[i]);
                Vector3 deskLocal = rootLocalToInitialDeskLocal.MultiplyPoint3x4(rootLocal);

                if (deformScaniverseWidthBand)
                    deskLocal.x = MapCoordinateWithConnectedOutside(deskLocal.x, edges.minX, edges.maxX, fixedCoordinate.x, widthFactor);
                if (deformScaniverseDepthBand)
                    deskLocal.z = MapCoordinateWithConnectedOutside(deskLocal.z, edges.minZ, edges.maxZ, fixedCoordinate.z, depthFactor);

                Vector3 deformedRootLocal = initialDeskLocalToRootLocal.MultiplyPoint3x4(deskLocal);
                state.deformedVertices[i] = rootLocalToMeshLocal.MultiplyPoint3x4(deformedRootLocal);
            }

            state.mesh.vertices = state.deformedVertices;
            if (recalculateScaniverseDeformedBounds)
                state.mesh.RecalculateBounds();
        }
    }

    private bool HasExplicitScaniverseDeformationRoots()
    {
        if (scaniverseDeformationRoots == null)
            return false;

        for (int i = 0; i < scaniverseDeformationRoots.Length; i++)
        {
            if (scaniverseDeformationRoots[i] != null)
                return true;
        }

        return false;
    }

    private ScaledObject FindPrimaryScaledObject()
    {
        return GetPrimaryScaledObject();
    }

    private void TryReadControllerScaleFactors(float referenceScale, ref float widthFactor, ref float depthFactor)
    {
        GoGoInteractionController_NoY3 controller = ResolveRedirectionController();
        if (controller == null)
            return;

        if (applyToWidthScale)
            widthFactor = controller.deskWidthScale / referenceScale;
        if (applyToDepthScale)
            depthFactor = controller.deskDepthScale / referenceScale;
    }

    private bool TryGetInitialDeskLocalToRootLocal(ScaledObject desk, Transform scaniverseRoot, out Matrix4x4 initialDeskLocalToRootLocal)
    {
        initialDeskLocalToRootLocal = Matrix4x4.identity;
        if (desk == null || desk.target == null || scaniverseRoot == null)
            return false;

        Transform parent = desk.target.parent;
        Matrix4x4 parentLocalToWorld = parent != null ? parent.localToWorldMatrix : Matrix4x4.identity;
        Quaternion initialRotation = IsZeroQuaternion(desk.initialLocalRotation) ? desk.target.localRotation : desk.initialLocalRotation;
        Vector3 initialScale = desk.initialLocalScale.sqrMagnitude > 1e-8f ? desk.initialLocalScale : desk.target.localScale;
        Matrix4x4 initialDeskLocalToWorld = parentLocalToWorld * Matrix4x4.TRS(desk.initialLocalPosition, initialRotation, initialScale);
        initialDeskLocalToRootLocal = scaniverseRoot.worldToLocalMatrix * initialDeskLocalToWorld;
        return true;
    }

    private Vector3 ResolveFixedScaleCoordinate(ScaledObject desk, Transform scaniverseRoot, Matrix4x4 rootLocalToInitialDeskLocal, BoundsEdges edges)
    {
        Vector3 center = edges.Center;
        if (chooseFixedEdgeFromRedirectionOrigin && TryGetRedirectionOriginTransform(out Transform redirectionOrigin))
        {
            Vector3 originRootLocal = scaniverseRoot.worldToLocalMatrix.MultiplyPoint3x4(redirectionOrigin.position);
            Vector3 originDeskLocal = rootLocalToInitialDeskLocal.MultiplyPoint3x4(originRootLocal);

            float minXDistance = SqrDistanceToXEdge(originDeskLocal, edges.minX, edges);
            float maxXDistance = SqrDistanceToXEdge(originDeskLocal, edges.maxX, edges);
            float minZDistance = SqrDistanceToZEdge(originDeskLocal, edges.minZ, edges);
            float maxZDistance = SqrDistanceToZEdge(originDeskLocal, edges.maxZ, edges);

            float bestDistance = minXDistance;
            Vector3 fixedCoordinate = new Vector3(edges.minX, 0f, center.z);

            if (maxXDistance < bestDistance)
            {
                bestDistance = maxXDistance;
                fixedCoordinate = new Vector3(edges.maxX, 0f, center.z);
            }

            if (minZDistance < bestDistance)
            {
                bestDistance = minZDistance;
                fixedCoordinate = new Vector3(center.x, 0f, edges.minZ);
            }

            if (maxZDistance < bestDistance)
                fixedCoordinate = new Vector3(center.x, 0f, edges.maxZ);

            return fixedCoordinate;
        }

        Vector3 fallbackDirection = seatedSideLocalDirection.sqrMagnitude > 1e-8f
            ? seatedSideLocalDirection.normalized
            : Vector3.back;
        if (Mathf.Abs(fallbackDirection.x) > Mathf.Abs(fallbackDirection.z))
            return new Vector3(fallbackDirection.x < 0f ? edges.minX : edges.maxX, 0f, center.z);

        return new Vector3(center.x, 0f, fallbackDirection.z < 0f ? edges.minZ : edges.maxZ);
    }

    private static float MapCoordinateWithConnectedOutside(float value, float min, float max, float fixedCoordinate, float factor)
    {
        factor = Mathf.Max(0.001f, factor);
        float width = max - min;
        if (Mathf.Abs(width) <= 1e-8f || Mathf.Abs(factor - 1f) <= 1e-8f)
            return value;

        fixedCoordinate = Mathf.Clamp(fixedCoordinate, min, max);
        float scaledMin = fixedCoordinate + (min - fixedCoordinate) * factor;
        float scaledMax = fixedCoordinate + (max - fixedCoordinate) * factor;

        if (value < min)
            return value + (scaledMin - min);
        if (value > max)
            return value + (scaledMax - max);

        return fixedCoordinate + (value - fixedCoordinate) * factor;
    }

    private void EnsureScaniverseMeshCache(Transform scaniverseRoot)
    {
        if (scaniverseRoot == null)
            return;

        MeshFilter[] filters = scaniverseRoot.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter filter = filters[i];
            if (filter == null || filter.sharedMesh == null || IsScaledObjectHierarchy(filter.transform))
                continue;
            if (HasScaniverseMeshState(filter))
                continue;

            Mesh mesh = filter.mesh;
            if (mesh == null)
                continue;

            ScaniverseMeshState state = new ScaniverseMeshState
            {
                deformationRoot = scaniverseRoot,
                meshFilter = filter,
                mesh = mesh,
                originalVertices = mesh.vertices,
                deformedVertices = new Vector3[mesh.vertexCount]
            };
            scaniverseMeshStates.Add(state);
        }
    }

    private bool HasScaniverseMeshState(MeshFilter meshFilter)
    {
        for (int i = 0; i < scaniverseMeshStates.Count; i++)
        {
            ScaniverseMeshState state = scaniverseMeshStates[i];
            if (state != null && state.meshFilter == meshFilter)
                return true;
        }

        return false;
    }

    private bool IsScaledObjectHierarchy(Transform transformToCheck)
    {
        if (transformToCheck == null)
            return false;

        ScaledObject explicitDesk = GetExplicitPrimaryDeskState();
        if (explicitDesk != null
            && explicitDesk.target != null
            && (transformToCheck == explicitDesk.target || transformToCheck.IsChildOf(explicitDesk.target)))
            return true;

        if (scaledObjects == null)
            return false;

        for (int i = 0; i < scaledObjects.Length; i++)
        {
            ScaledObject scaled = scaledObjects[i];
            if (scaled != null
                && scaled.target != null
                && (transformToCheck == scaled.target || transformToCheck.IsChildOf(scaled.target)))
                return true;
        }

        return false;
    }

    private void RestoreScaniverseMeshes()
    {
        for (int i = 0; i < scaniverseMeshStates.Count; i++)
        {
            ScaniverseMeshState state = scaniverseMeshStates[i];
            if (state == null || state.mesh == null || state.originalVertices == null)
                continue;

            state.mesh.vertices = state.originalVertices;
            if (recalculateScaniverseDeformedBounds)
                state.mesh.RecalculateBounds();
        }
    }

    private void ResetScaniverseMeshCache()
    {
        RestoreScaniverseMeshes();
        scaniverseMeshStates.Clear();
    }

    private static float SqrDistanceToXEdge(Vector3 point, float edgeX, BoundsEdges edges)
    {
        float clampedZ = Mathf.Clamp(point.z, edges.minZ, edges.maxZ);
        float dx = point.x - edgeX;
        float dz = point.z - clampedZ;
        return dx * dx + dz * dz;
    }

    private static float SqrDistanceToZEdge(Vector3 point, float edgeZ, BoundsEdges edges)
    {
        float clampedX = Mathf.Clamp(point.x, edges.minX, edges.maxX);
        float dx = point.x - clampedX;
        float dz = point.z - edgeZ;
        return dx * dx + dz * dz;
    }

    private static bool IsZeroQuaternion(Quaternion value)
    {
        return Mathf.Abs(value.x) + Mathf.Abs(value.y) + Mathf.Abs(value.z) + Mathf.Abs(value.w) <= 1e-8f;
    }

    private struct BoundsEdges
    {
        public float minX;
        public float maxX;
        public float minZ;
        public float maxZ;

        public BoundsEdges(Vector3 min, Vector3 max)
        {
            minX = min.x;
            maxX = max.x;
            minZ = min.z;
            maxZ = max.z;
        }

        public Vector3 Center
        {
            get { return new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f); }
        }
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

            Bounds bounds = renderer.localBounds;
            Matrix4x4 rendererLocalToTargetLocal = worldToLocal * renderer.transform.localToWorldMatrix;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 rendererLocalCorner = new Vector3(
                    (corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                    (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                Vector3 localCorner = rendererLocalToTargetLocal.MultiplyPoint3x4(rendererLocalCorner);

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

    private void UpdateUiFlickerDiagnostics()
    {
        Transform slider = deskSliderToMove != null ? deskSliderToMove : transform;
        if (!diagnosticInitialized)
        {
            InitializeUiFlickerDiagnostics(slider);
            return;
        }

        Transform sliderParent = slider.parent;
        float sliderWorldJump = Vector3.Distance(diagnosticLastSliderWorldPosition, slider.position);
        float sliderLocalJump = Vector3.Distance(diagnosticLastSliderLocalPosition, slider.localPosition);
        float sliderRotationJump = Quaternion.Angle(diagnosticLastSliderWorldRotation, slider.rotation);
        float parentWorldJump = sliderParent != null
            ? Vector3.Distance(diagnosticLastDeskWorldPosition, sliderParent.position)
            : 0f;
        float parentRotationJump = sliderParent != null
            ? Quaternion.Angle(diagnosticLastDeskWorldRotation, sliderParent.rotation)
            : 0f;
        float paletteLocalJump = diagnosticPalette != null
            ? Vector3.Distance(diagnosticLastPaletteLocalPosition, diagnosticPalette.localPosition)
            : 0f;

        int enabledRendererCount = 0;
        string rendererChanges = string.Empty;
        for (int i = 0; i < diagnosticRenderers.Length; i++)
        {
            Renderer renderer = diagnosticRenderers[i];
            bool visible = renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy;
            if (visible)
                enabledRendererCount++;
            if (i < diagnosticRendererStates.Length && visible != diagnosticRendererStates[i])
            {
                rendererChanges += (rendererChanges.Length == 0 ? string.Empty : ",")
                    + (renderer != null ? renderer.name : "destroyed")
                    + "=" + (visible ? "on" : "off");
                diagnosticRendererStates[i] = visible;
            }
        }

        bool poseJump = sliderWorldJump >= diagnosticPositionJumpMeters
            || sliderLocalJump >= diagnosticPositionJumpMeters
            || sliderRotationJump >= diagnosticRotationJumpDegrees
            || parentWorldJump >= diagnosticPositionJumpMeters
            || parentRotationJump >= diagnosticRotationJumpDegrees
            || paletteLocalJump >= diagnosticPositionJumpMeters;
        bool rendererChanged = enabledRendererCount != diagnosticLastEnabledRendererCount || rendererChanges.Length > 0;
        bool longFrame = Time.unscaledDeltaTime >= diagnosticLongFrameSeconds;

        if ((poseJump || rendererChanged || longFrame) && Time.unscaledTime >= diagnosticNextLogTime)
        {
            diagnosticNextLogTime = Time.unscaledTime + Mathf.Max(0.05f, diagnosticLogCooldownSeconds);
            string line = $"frame={Time.frameCount} dt={Time.unscaledDeltaTime:0.0000}s "
                + $"sliderWorldJump={sliderWorldJump:0.0000}m sliderLocalJump={sliderLocalJump:0.0000}m sliderRotJump={sliderRotationJump:0.00}deg "
                + $"parentWorldJump={parentWorldJump:0.0000}m parentRotJump={parentRotationJump:0.00}deg "
                + $"paletteLocalJump={paletteLocalJump:0.0000}m renderers={enabledRendererCount}/{diagnosticRenderers.Length} "
                + $"rendererChanges=[{rendererChanges}] dragging={(draggingPinch != null)} scale={currentScale:0.000}";
            WriteUiFlickerDiagnostic(line, true);
        }

        diagnosticLastSliderWorldPosition = slider.position;
        diagnosticLastSliderLocalPosition = slider.localPosition;
        diagnosticLastSliderWorldRotation = slider.rotation;
        if (sliderParent != null)
        {
            diagnosticLastDeskWorldPosition = sliderParent.position;
            diagnosticLastDeskWorldRotation = sliderParent.rotation;
        }
        if (diagnosticPalette != null)
            diagnosticLastPaletteLocalPosition = diagnosticPalette.localPosition;
        diagnosticLastEnabledRendererCount = enabledRendererCount;
    }

    private void InitializeUiFlickerDiagnostics(Transform slider)
    {
        diagnosticPalette = FindChildRecursive(slider, "VRColorPalettePanel");
        diagnosticRenderers = slider.GetComponentsInChildren<Renderer>(true);
        diagnosticRendererStates = new bool[diagnosticRenderers.Length];
        diagnosticLastEnabledRendererCount = 0;
        for (int i = 0; i < diagnosticRenderers.Length; i++)
        {
            Renderer renderer = diagnosticRenderers[i];
            bool visible = renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy;
            diagnosticRendererStates[i] = visible;
            if (visible)
                diagnosticLastEnabledRendererCount++;
        }

        diagnosticLastSliderWorldPosition = slider.position;
        diagnosticLastSliderLocalPosition = slider.localPosition;
        diagnosticLastSliderWorldRotation = slider.rotation;
        if (slider.parent != null)
        {
            diagnosticLastDeskWorldPosition = slider.parent.position;
            diagnosticLastDeskWorldRotation = slider.parent.rotation;
        }
        if (diagnosticPalette != null)
            diagnosticLastPaletteLocalPosition = diagnosticPalette.localPosition;

        diagnosticLogPath = Path.Combine(Application.persistentDataPath, "DeskUiFlickerDiagnostics.log");
        diagnosticInitialized = true;
        WriteUiFlickerDiagnostic(
            $"START slider={slider.name} parent={(slider.parent != null ? slider.parent.name : "none")} "
            + $"palette={(diagnosticPalette != null ? diagnosticPalette.name : "not-found")} "
            + $"renderers={diagnosticLastEnabledRendererCount}/{diagnosticRenderers.Length} path={diagnosticLogPath}",
            false);
    }

    private void WriteUiFlickerDiagnostic(string message, bool warning)
    {
        string line = $"{System.DateTime.UtcNow:O} {message}";
        if (warning)
            Debug.LogWarning("[DeskUiFlicker] " + line, this);
        else
            Debug.Log("[DeskUiFlicker] " + line, this);

        try
        {
            File.AppendAllText(diagnosticLogPath, line + System.Environment.NewLine);
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning("[DeskUiFlicker] Failed to write diagnostics: " + exception.Message, this);
            logUiFlickerDiagnostics = false;
        }
    }

    private static Transform FindChildRecursive(Transform root, string childName)
    {
        if (root == null)
            return null;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            if (string.Equals(child.name, childName, System.StringComparison.OrdinalIgnoreCase))
                return child;
            Transform nested = FindChildRecursive(child, childName);
            if (nested != null)
                return nested;
        }
        return null;
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

    private static void DestroyImmediateSafe(UnityEngine.Object obj)
    {
        if (obj == null)
            return;

        if (Application.isPlaying)
            Destroy(obj);
        else
            DestroyImmediate(obj);
    }
}
