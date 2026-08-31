using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class ScalePlacementChallengeController : MonoBehaviour
{
    [Serializable]
    public class Pattern
    {
        public string id = "A";
        public string targetObjectId = "1";
        [Tooltip("Offset in meters from RedirectionOrigin: X is right and Y is forward on the tabletop.")]
        public Vector2 deskLocalPosition = new Vector2(0f, 0.82f);
        [Tooltip("Required object scale ratio for local X/Y/Z.")]
        public Vector3 targetScaleRatio = new Vector3(1.35f, 1.35f, 1.35f);
        [Tooltip("Allowed relative size error for X/Y/Z. 0.10 means +/-10 percent of the target on that axis.")]
        public Vector3 scaleToleranceRatio = new Vector3(0.10f, 0.10f, 0.10f);
        [Tooltip("Allowed center-position error in meters. X/Z are used; Y is retained only for saved-settings compatibility and is never used for completion.")]
        public Vector3 positionToleranceMeters = new Vector3(0.09f, 0.10f, 0.09f);
        [Tooltip("Optional extra visual clearance outside the exact target footprint. Keep zero when the ring itself should represent the clear size.")]
        [Min(0f)] public float ringPaddingMeters = 0f;
    }

    [Serializable]
    private struct SavedPatternSettings
    {
        public string targetObjectId;
        public Vector3 targetScaleRatio;
        public Vector3 scaleToleranceRatio;
        public Vector3 positionToleranceMeters;
    }

    [Header("References")]
    public Transform deskOrigin;
    [Tooltip("Pattern positions and front/left/right axes are based on this transform, never on the HMD.")]
    public Transform redirectionOrigin;
    public Transform tabletopReference;
    public GoGoInteractionController_NoY3 redirectionController;
    public DeskScaleSliderPanel deskScaleSlider;
    public ExhibitionExperienceResetter experienceResetter;

    [Header("Patterns")]
    public Pattern[] patterns =
    {
        // In the calibrated scene RedirectionOrigin.forward is the direction
        // where A is physically seen on the right. Keep that known-good A
        // position, mirror C to the left, and put B on the perpendicular front.
        new Pattern { id = "A", targetObjectId = "1", deskLocalPosition = new Vector2(0f, 0.82f), targetScaleRatio = new Vector3(1.35f, 1.35f, 1.35f) },
        new Pattern { id = "B", targetObjectId = "3", deskLocalPosition = new Vector2(-0.72f, 0f), targetScaleRatio = new Vector3(1.70f, 1.70f, 1.70f) },
        new Pattern { id = "C", targetObjectId = "4", deskLocalPosition = new Vector2(0f, -0.82f), targetScaleRatio = new Vector3(2.10f, 2.10f, 2.10f) }
    };
    public string activePatternId = "A";
    public bool persistActivePattern = true;
    public string activePatternPrefsKey = "HandRedirection.TargetRingPattern";
    public bool persistPatternSettings = true;
    public string patternSettingsPrefsKeyPrefix = "HandRedirection.TargetRingSettings.";
    [Tooltip("Start with the ring task disabled. Selecting A/B/C explicitly enables it.")]
    public bool enableChallengeOnStart = false;

    [Header("Ring Visual")]
    [Range(24, 128)] public int ringSegments = 72;
    [Min(0.001f)] public float ringWidth = 0.012f;
    public float tabletopLiftMeters = 0.006f;
    public Color incorrectColor = new Color(1f, 0.08f, 0.08f, 0.95f);
    public Color correctColor = new Color(0.05f, 0.85f, 1f, 0.98f);
    public bool latchSuccessUntilPatternChange = true;

    public event Action<string> PatternCompleted;
    public string ActivePatternId => activePatternId;
    public bool IsComplete => successLatched;
    public bool IsChallengeEnabled => challengeEnabled;

    private LineRenderer ring;
    private Material ringMaterial;
    private Pattern activePattern;
    private GoGoInteractionController_NoY3.WarpObjectEntry activeEntry;
    private float tabletopHeight;
    private bool tabletopFrameCaptured;
    private Vector2 ringRadii = new Vector2(0.12f, 0.12f);
    private Vector3 targetObjectHalfExtents = new Vector3(0.095f, 0.05f, 0.095f);
    private float targetObjectLongestHalfExtent = 0.095f;
    private bool activeTargetUsesUniformScale;
    private float targetObjectHalfHeight = 0.05f;
    private bool successLatched;
    private bool wasCorrect;
    private bool challengeEnabled;
    private bool diagnosticStateInitialized;
    private bool lastDiagnosticPositionCorrect;
    private bool lastDiagnosticSizeCorrect;
    private readonly Dictionary<GoGoInteractionController_NoY3.WarpObjectEntry, Vector3> baselineScaleByEntry
        = new Dictionary<GoGoInteractionController_NoY3.WarpObjectEntry, Vector3>();

    private void Awake()
    {
        LoadAllPatternSettings();
        if (persistActivePattern && !string.IsNullOrWhiteSpace(activePatternPrefsKey))
            activePatternId = PlayerPrefs.GetString(activePatternPrefsKey, activePatternId);
    }

    private void Start()
    {
        ResolveReferences();
        CreateRing();
        challengeEnabled = false;
        ring.gameObject.SetActive(false);
        if (enableChallengeOnStart)
        {
            if (!ActivatePattern(activePatternId, false, false))
                ActivatePattern("A", false, false);
        }
    }

    private void Update()
    {
        if (!challengeEnabled)
        {
            if (ring != null && ring.gameObject.activeSelf)
                ring.gameObject.SetActive(false);
            return;
        }

        if (activePattern == null || activeEntry == null || activeEntry.warpedObject == null)
        {
            ResolveReferences();
            GoGoInteractionController_NoY3.WarpObjectEntry previousEntry = activeEntry;
            ResolveActiveEntry();
            if (activePattern == null || activeEntry == null || activeEntry.warpedObject == null)
                return;

            // The controller and its object list can become available after this
            // component's Start. Rebuild the ring as soon as the real entry is
            // resolved instead of leaving the 12 cm fallback ring forever.
            if (activeEntry != previousEntry)
            {
                ComputeTargetRingSize();
                UpdateRingPose();
            }
        }

        UpdateRingPose();
        bool correct = EvaluateCurrentTarget();
        if (correct && !wasCorrect && !successLatched)
        {
            successLatched = true;
            ExhibitionAudioFeedback.PlayCue(ExhibitionAudioFeedback.Cue.PlacementSuccess);
            PatternCompleted?.Invoke(activePattern.id);
        }
        if (!latchSuccessUntilPatternChange && !correct)
            successLatched = false;
        wasCorrect = correct;
        UpdateRingColor(successLatched || correct ? correctColor : incorrectColor);
    }

    public bool ActivatePattern(string patternId, bool resetSizes, bool save = true)
    {
        ResolveReferences();
        Pattern next = FindPattern(patternId);
        if (next == null)
            return false;

        if (resetSizes)
        {
            if (experienceResetter != null)
                experienceResetter.ResetForNextParticipant();
            if (deskScaleSlider != null)
                deskScaleSlider.SetScaleFromExternal(deskScaleSlider.sceneObjectReferenceScale);
        }

        activePattern = next;
        activePatternId = next.id.ToUpperInvariant();
        challengeEnabled = true;
        successLatched = false;
        wasCorrect = false;
        diagnosticStateInitialized = false;
        ResolveActiveEntry();
        if (!tabletopFrameCaptured)
        {
            tabletopHeight = ComputeTabletopHeight();
            tabletopFrameCaptured = true;
        }
        ComputeTargetRingSize();
        CreateRing();
        ring.gameObject.SetActive(true);
        UpdateRingPose();
        UpdateRingColor(incorrectColor);

        if (save && persistActivePattern && !string.IsNullOrWhiteSpace(activePatternPrefsKey))
        {
            PlayerPrefs.SetString(activePatternPrefsKey, activePatternId);
            PlayerPrefs.Save();
        }
        return true;
    }

    public void DisableChallenge()
    {
        challengeEnabled = false;
        successLatched = false;
        wasCorrect = false;
        if (ring != null)
            ring.gameObject.SetActive(false);
    }

    private void ResolveReferences()
    {
        if (redirectionController == null)
            redirectionController = DeskScaleSliderPanel.FindBestRedirectionController();
        if (deskOrigin == null && redirectionController != null)
            deskOrigin = redirectionController.deskOrigin;
        if (redirectionOrigin == null && redirectionController != null)
            redirectionOrigin = redirectionController.redirectionOrigin;
        if (deskOrigin == null)
        {
            SpatialAnchorToDeskOriginBinder binder = FindAnyObjectByType<SpatialAnchorToDeskOriginBinder>();
            if (binder != null)
            {
                deskOrigin = binder.deskOrigin;
                if (redirectionOrigin == null) redirectionOrigin = binder.redirectionOrigin;
            }
        }
        if (redirectionOrigin == null)
            redirectionOrigin = deskOrigin;
        if (deskScaleSlider == null)
            deskScaleSlider = FindAnyObjectByType<DeskScaleSliderPanel>();
        if (tabletopReference == null && deskScaleSlider != null)
            tabletopReference = deskScaleSlider.primaryDeskReference;
        if (experienceResetter == null)
            experienceResetter = FindAnyObjectByType<ExhibitionExperienceResetter>();
    }

    private Pattern FindPattern(string id)
    {
        if (patterns == null || string.IsNullOrWhiteSpace(id)) return null;
        for (int i = 0; i < patterns.Length; i++)
            if (patterns[i] != null && string.Equals(patterns[i].id, id, StringComparison.OrdinalIgnoreCase))
                return patterns[i];
        return null;
    }

    private void ResolveActiveEntry()
    {
        activeEntry = null;
        if (activePattern == null || redirectionController == null || redirectionController.objects == null) return;
        for (int i = 0; i < redirectionController.objects.Count; i++)
        {
            GoGoInteractionController_NoY3.WarpObjectEntry entry = redirectionController.objects[i];
            if (entry != null && entry.enabled && string.Equals(entry.name, activePattern.targetObjectId, StringComparison.OrdinalIgnoreCase))
            {
                activeEntry = entry;
                CaptureBaselineScale(entry);
                return;
            }
        }
    }

    private void CaptureBaselineScale(GoGoInteractionController_NoY3.WarpObjectEntry entry)
    {
        if (entry == null || baselineScaleByEntry.ContainsKey(entry))
            return;
        Transform scaleSource = entry.warpedScaleSource != null ? entry.warpedScaleSource : entry.warpedObject;
        if (scaleSource == null)
            return;
        Vector3 baseline = entry.baseScaleInitialized ? entry.baseWarpedScale : scaleSource.localScale;
        if (Mathf.Abs(baseline.x) < 1e-5f || Mathf.Abs(baseline.y) < 1e-5f || Mathf.Abs(baseline.z) < 1e-5f)
            baseline = scaleSource.localScale;
        baselineScaleByEntry[entry] = baseline;
    }

    private float ComputeTabletopHeight()
    {
        if (deskOrigin == null || tabletopReference == null) return 0f;
        Renderer[] renderers = tabletopReference.GetComponentsInChildren<Renderer>(false);
        float highest = 0f;
        bool found = false;
        Vector3 up = deskOrigin.up.normalized;
        for (int i = 0; i < renderers.Length; i++)
        {
            Bounds b = renderers[i].bounds;
            Vector3 c = b.center;
            Vector3 e = b.extents;
            for (int mask = 0; mask < 8; mask++)
            {
                Vector3 corner = c + new Vector3((mask & 1) == 0 ? -e.x : e.x, (mask & 2) == 0 ? -e.y : e.y, (mask & 4) == 0 ? -e.z : e.z);
                float h = Vector3.Dot(corner - deskOrigin.position, up);
                if (!found || h > highest) { highest = h; found = true; }
            }
        }
        return found ? highest : 0f;
    }

    public bool TryGetPatternSettings(
        string patternId,
        out string targetObjectId,
        out Vector3 targetScale,
        out Vector3 scaleTolerance,
        out Vector3 positionTolerance)
    {
        Pattern pattern = FindPattern(patternId);
        if (pattern == null)
        {
            targetObjectId = "";
            targetScale = Vector3.one;
            scaleTolerance = Vector3.one * 0.1f;
            positionTolerance = Vector3.one * 0.09f;
            return false;
        }
        targetObjectId = pattern.targetObjectId;
        targetScale = pattern.targetScaleRatio;
        scaleTolerance = pattern.scaleToleranceRatio;
        positionTolerance = pattern.positionToleranceMeters;
        return true;
    }

    public bool SetPatternSettings(
        string patternId,
        string targetObjectId,
        Vector3 targetScale,
        Vector3 scaleTolerance,
        Vector3 positionTolerance,
        bool save = true)
    {
        Pattern pattern = FindPattern(patternId);
        if (pattern == null || string.IsNullOrWhiteSpace(targetObjectId))
            return false;

        pattern.targetObjectId = targetObjectId.Trim();
        pattern.targetScaleRatio = MaxComponents(targetScale, 0.01f);
        pattern.scaleToleranceRatio = MaxComponents(scaleTolerance, 0f);
        pattern.positionToleranceMeters = MaxComponents(positionTolerance, 0.001f);
        if (save)
            SavePatternSettings(pattern);
        if (activePattern == pattern)
        {
            successLatched = false;
            wasCorrect = false;
            diagnosticStateInitialized = false;
            ResolveActiveEntry();
            ComputeTargetRingSize();
            UpdateRingPose();
            UpdateRingColor(incorrectColor);
        }
        return true;
    }

    private void ComputeTargetRingSize()
    {
        ringRadii = new Vector2(0.12f, 0.12f);
        targetObjectHalfExtents = new Vector3(0.095f, 0.05f, 0.095f);
        targetObjectLongestHalfExtent = 0.095f;
        activeTargetUsesUniformScale = false;
        targetObjectHalfHeight = 0.05f;
        if (activeEntry == null || activeEntry.warpedObject == null || activePattern == null)
            return;

        Transform scaleSource = activeEntry.warpedScaleSource != null ? activeEntry.warpedScaleSource : activeEntry.warpedObject;
        GetObjectMeasurementAxes(scaleSource, out Vector3 sizeX, out Vector3 sizeY, out Vector3 sizeZ);
        CaptureBaselineScale(activeEntry);
        Vector3 baselineScale = baselineScaleByEntry.TryGetValue(activeEntry, out Vector3 capturedScale)
            ? capturedScale
            : scaleSource.localScale;
        Vector3 desiredScale = Vector3.Scale(baselineScale, activePattern.targetScaleRatio);

        // Evaluate the renderers at the requested scale without modifying the
        // live object. Dividing the current world bounds by local X/Y/Z ratios
        // was only valid for axis-aligned models and produced incorrect B/C
        // rings when their mesh hierarchy or rotation differed from A.
        if (!TryGetVisualHalfExtentsAtScale(
                activeEntry.warpedObject, scaleSource, desiredScale,
                sizeX, sizeY, sizeZ, out _, out Vector3 targetHalfExtents))
            return;
        targetObjectHalfExtents = MaxComponents(targetHalfExtents, 0.005f);
        targetObjectLongestHalfExtent = MaxComponent(targetObjectHalfExtents);
        activeTargetUsesUniformScale = ApproximatelyUniform(activePattern.targetScaleRatio);
        if (activeTargetUsesUniformScale)
        {
            // Uniform N-times mode uses the selected object's longest visible
            // X/Y/Z dimension as its diameter. The tabletop ring is therefore
            // a true circle and is independent of the model's aspect ratio.
            float radius = Mathf.Max(0.04f, targetObjectLongestHalfExtent + activePattern.ringPaddingMeters);
            ringRadii = new Vector2(radius, radius);
        }
        else
        {
            ringRadii = new Vector2(
                Mathf.Max(0.04f, targetObjectHalfExtents.x + activePattern.ringPaddingMeters),
                Mathf.Max(0.04f, targetObjectHalfExtents.z + activePattern.ringPaddingMeters));
        }
        targetObjectHalfHeight = targetObjectHalfExtents.y;
        Debug.Log(
            $"[ScalePlacementChallenge] Pattern {activePattern.id} ring rebuilt " +
            $"object={activePattern.targetObjectId} measurement=object_local_axes " +
            $"targetScale={activePattern.targetScaleRatio:F3} baselineScale={baselineScale:F3} " +
            $"desiredScale={desiredScale:F3} targetHalfExtents={targetObjectHalfExtents:F3} " +
            $"ringRadii=({ringRadii.x:F3},{ringRadii.y:F3})");
    }

    private bool EvaluateCurrentTarget()
    {
        if (deskOrigin == null || activeEntry == null || activePattern == null)
            return false;

        GetPatternAxes(out Vector3 right, out Vector3 up, out Vector3 forward);
        if (!TryGetVisualHalfExtents(activeEntry.warpedObject, right, up, forward, out Vector3 visualCenter, out _))
            return false;
        Transform scaleSource = activeEntry.warpedScaleSource != null ? activeEntry.warpedScaleSource : activeEntry.warpedObject;
        Vector3 expectedCenter = GetExpectedObjectCenter(up);
        Vector3 visualDelta = visualCenter - expectedCenter;
        Vector3 pivotDelta = activeEntry.warpedObject.position - expectedCenter;
        float visualPositionErrorX = Mathf.Abs(Vector3.Dot(visualDelta, right));
        float visualPositionErrorZ = Mathf.Abs(Vector3.Dot(visualDelta, forward));
        float pivotPositionErrorX = Mathf.Abs(Vector3.Dot(pivotDelta, right));
        float pivotPositionErrorZ = Mathf.Abs(Vector3.Dot(pivotDelta, forward));
        Vector3 positionTolerance = activePattern.positionToleranceMeters;
        // Deliberately ignore height. The task is placement inside a target on
        // the tabletop plane, so only RedirectionOrigin-relative X/Z can block
        // completion.
        bool visualCenterCorrect = visualPositionErrorX <= positionTolerance.x &&
                                   visualPositionErrorZ <= positionTolerance.z;
        bool trackedPivotCorrect = pivotPositionErrorX <= positionTolerance.x &&
                                   pivotPositionErrorZ <= positionTolerance.z;
        bool positionCorrect = visualCenterCorrect || trackedPivotCorrect;

        // All three scene targets are deformed by changing this exact
        // Transform.localScale. Judge the requested ratio directly so mesh
        // asymmetry, renderer hierarchy, and tracker rotation cannot make B/C
        // disagree with A. Renderer dimensions remain responsible only for the
        // physical ring diameter.
        CaptureBaselineScale(activeEntry);
        bool sizeCorrect = false;
        Vector3 currentRatio = Vector3.one;
        if (baselineScaleByEntry.TryGetValue(activeEntry, out Vector3 baselineScale))
        {
            currentRatio = AbsScaleRatio(scaleSource.localScale, baselineScale);
            Vector3 ratioTolerance = Vector3.Scale(activePattern.targetScaleRatio, activePattern.scaleToleranceRatio);
            ratioTolerance = MaxComponents(ratioTolerance, 0.001f);
            sizeCorrect = Mathf.Abs(currentRatio.x - activePattern.targetScaleRatio.x) <= ratioTolerance.x &&
                          Mathf.Abs(currentRatio.y - activePattern.targetScaleRatio.y) <= ratioTolerance.y &&
                          Mathf.Abs(currentRatio.z - activePattern.targetScaleRatio.z) <= ratioTolerance.z;
        }

        if (!diagnosticStateInitialized ||
            positionCorrect != lastDiagnosticPositionCorrect ||
            sizeCorrect != lastDiagnosticSizeCorrect)
        {
            Debug.Log(
                $"[ScalePlacementChallenge] Pattern {activePattern.id} evaluation " +
                $"object={activePattern.targetObjectId} position={(positionCorrect ? "OK" : "NG")} " +
                $"visualErrorXZ=({visualPositionErrorX:F3},{visualPositionErrorZ:F3}) " +
                $"pivotErrorXZ=({pivotPositionErrorX:F3},{pivotPositionErrorZ:F3}) " +
                $"toleranceXZ=({positionTolerance.x:F3},{positionTolerance.z:F3}) " +
                $"size={(sizeCorrect ? "OK" : "NG")} currentRatio={currentRatio:F3} " +
                $"targetRatio={activePattern.targetScaleRatio:F3} toleranceRatio={activePattern.scaleToleranceRatio:F3}");
            diagnosticStateInitialized = true;
            lastDiagnosticPositionCorrect = positionCorrect;
            lastDiagnosticSizeCorrect = sizeCorrect;
        }

        return positionCorrect && sizeCorrect;
    }

    private Vector3 GetRingCenter()
    {
        if (deskOrigin == null || activePattern == null) return transform.position;
        GetPatternAxes(out Vector3 right, out Vector3 up, out Vector3 forward);
        Transform origin = redirectionOrigin != null ? redirectionOrigin : deskOrigin;
        Vector3 basePosition = origin.position;
        float baseHeight = Vector3.Dot(basePosition - deskOrigin.position, up);
        basePosition += up * (tabletopHeight - baseHeight);
        return basePosition
            + right * activePattern.deskLocalPosition.x
            + forward * activePattern.deskLocalPosition.y
            + up * tabletopLiftMeters;
    }

    private Vector3 GetExpectedObjectCenter(Vector3 up)
    {
        return GetRingCenter() + up * (targetObjectHalfHeight - tabletopLiftMeters);
    }

    private void GetPatternAxes(out Vector3 right, out Vector3 up, out Vector3 forward)
    {
        Transform origin = redirectionOrigin != null ? redirectionOrigin : deskOrigin;
        up = deskOrigin != null ? deskOrigin.up.normalized : Vector3.up;
        forward = origin != null ? Vector3.ProjectOnPlane(origin.forward, up) : Vector3.forward;
        if (forward.sqrMagnitude < 1e-6f)
            forward = deskOrigin != null ? Vector3.ProjectOnPlane(deskOrigin.forward, up) : Vector3.forward;
        forward.Normalize();
        Vector3 originRight = origin != null ? Vector3.ProjectOnPlane(origin.right, up) : Vector3.zero;
        right = Vector3.Cross(up, forward).normalized;
        if (originRight.sqrMagnitude > 1e-6f && Vector3.Dot(right, originRight) < 0f)
            right = -right;
    }

    private static void GetObjectMeasurementAxes(
        Transform scaleSource,
        out Vector3 axisX,
        out Vector3 axisY,
        out Vector3 axisZ)
    {
        if (scaleSource == null)
        {
            axisX = Vector3.right;
            axisY = Vector3.up;
            axisZ = Vector3.forward;
            return;
        }

        // Renderer dimensions must be measured in axes that rotate with the
        // selected object. Measuring an elongated object in fixed world axes
        // changes its apparent X/Y/Z bounds whenever its tracker rotates; A
        // looked correct only because its shape was close to symmetric.
        axisX = scaleSource.right.normalized;
        axisY = Vector3.ProjectOnPlane(scaleSource.up, axisX).normalized;
        if (axisY.sqrMagnitude < 1e-6f)
            axisY = scaleSource.up.normalized;
        axisZ = Vector3.Cross(axisX, axisY).normalized;
        Vector3 sourceForward = scaleSource.forward.normalized;
        if (Vector3.Dot(axisZ, sourceForward) < 0f)
            axisZ = -axisZ;
    }

    private void CreateRing()
    {
        if (ring != null) return;
        GameObject go = new GameObject("Scale Placement Target Ring");
        go.transform.SetParent(transform, false);
        ring = go.AddComponent<LineRenderer>();
        ring.useWorldSpace = true;
        ring.loop = true;
        ring.positionCount = Mathf.Clamp(ringSegments, 24, 128);
        ring.startWidth = ringWidth;
        ring.endWidth = ringWidth;
        ring.numCapVertices = 4;
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        ringMaterial = new Material(shader) { name = "Scale Placement Ring Material" };
        ringMaterial.renderQueue = 3100;
        ring.sharedMaterial = ringMaterial;
    }

    private void UpdateRingPose()
    {
        if (ring == null || deskOrigin == null || activePattern == null) return;
        int count = Mathf.Clamp(ringSegments, 24, 128);
        if (ring.positionCount != count) ring.positionCount = count;
        ring.startWidth = ringWidth;
        ring.endWidth = ringWidth;
        Vector3 center = GetRingCenter();
        GetPatternAxes(out Vector3 right, out _, out Vector3 forward);
        for (int i = 0; i < count; i++)
        {
            float angle = (Mathf.PI * 2f * i) / count;
            ring.SetPosition(i, center + right * (Mathf.Cos(angle) * ringRadii.x) + forward * (Mathf.Sin(angle) * ringRadii.y));
        }
    }

    private void UpdateRingColor(Color color)
    {
        if (ring == null) return;
        ring.startColor = color;
        ring.endColor = color;
        if (ringMaterial != null)
        {
            if (ringMaterial.HasProperty("_BaseColor")) ringMaterial.SetColor("_BaseColor", color);
            if (ringMaterial.HasProperty("_Color")) ringMaterial.SetColor("_Color", color);
        }
    }

    private bool TryGetVisualHalfExtents(Transform root, Vector3 right, Vector3 up, Vector3 forward, out Vector3 center, out Vector3 halfExtents)
    {
        return TryGetProjectedVisualBounds(root, null, Vector3.one, false, right, up, forward, out center, out halfExtents);
    }

    private bool TryGetVisualHalfExtentsAtScale(
        Transform root,
        Transform scaleSource,
        Vector3 desiredLocalScale,
        Vector3 right,
        Vector3 up,
        Vector3 forward,
        out Vector3 center,
        out Vector3 halfExtents)
    {
        return TryGetProjectedVisualBounds(
            root, scaleSource, desiredLocalScale, true,
            right, up, forward, out center, out halfExtents);
    }

    private bool TryGetProjectedVisualBounds(
        Transform root,
        Transform scaleSource,
        Vector3 desiredLocalScale,
        bool overrideScale,
        Vector3 right,
        Vector3 up,
        Vector3 forward,
        out Vector3 center,
        out Vector3 halfExtents)
    {
        center = root != null ? root.position : Vector3.zero;
        halfExtents = Vector3.zero;
        if (root == null)
            return false;

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(false);
        bool found = false;
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        float maxZ = float.NegativeInfinity;

        Matrix4x4 desiredScaleSourceLocalToWorld = Matrix4x4.identity;
        if (overrideScale && scaleSource != null)
        {
            Matrix4x4 parentLocalToWorld = scaleSource.parent != null
                ? scaleSource.parent.localToWorldMatrix
                : Matrix4x4.identity;
            desiredScaleSourceLocalToWorld = parentLocalToWorld * Matrix4x4.TRS(
                scaleSource.localPosition, scaleSource.localRotation, desiredLocalScale);
        }

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (!IsChallengeObjectRenderer(renderer))
                continue;

            Matrix4x4 rendererLocalToWorld = renderer.localToWorldMatrix;
            if (overrideScale && scaleSource != null &&
                (renderer.transform == scaleSource || renderer.transform.IsChildOf(scaleSource)))
            {
                Matrix4x4 rendererLocalToScaleSource = scaleSource.worldToLocalMatrix * renderer.localToWorldMatrix;
                rendererLocalToWorld = desiredScaleSourceLocalToWorld * rendererLocalToScaleSource;
            }

            Bounds localBounds = renderer.localBounds;
            Vector3 c = localBounds.center;
            Vector3 e = localBounds.extents;
            for (int mask = 0; mask < 8; mask++)
            {
                Vector3 localCorner = c + new Vector3(
                    (mask & 1) == 0 ? -e.x : e.x,
                    (mask & 2) == 0 ? -e.y : e.y,
                    (mask & 4) == 0 ? -e.z : e.z);
                Vector3 worldCorner = rendererLocalToWorld.MultiplyPoint3x4(localCorner);
                float x = Vector3.Dot(worldCorner, right);
                float y = Vector3.Dot(worldCorner, up);
                float z = Vector3.Dot(worldCorner, forward);
                minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
                minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
                minZ = Mathf.Min(minZ, z); maxZ = Mathf.Max(maxZ, z);
                found = true;
            }
        }

        if (!found)
            return false;
        center = right * ((minX + maxX) * 0.5f)
               + up * ((minY + maxY) * 0.5f)
               + forward * ((minZ + maxZ) * 0.5f);
        halfExtents = new Vector3(
            (maxX - minX) * 0.5f,
            (maxY - minY) * 0.5f,
            (maxZ - minZ) * 0.5f);
        return halfExtents.sqrMagnitude > 1e-8f;
    }

    private void LoadAllPatternSettings()
    {
        if (!persistPatternSettings || patterns == null || string.IsNullOrWhiteSpace(patternSettingsPrefsKeyPrefix))
            return;
        for (int i = 0; i < patterns.Length; i++)
        {
            Pattern pattern = patterns[i];
            if (pattern == null || string.IsNullOrWhiteSpace(pattern.id)) continue;
            string json = PlayerPrefs.GetString(patternSettingsPrefsKeyPrefix + pattern.id.ToUpperInvariant(), "");
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                SavedPatternSettings saved = JsonUtility.FromJson<SavedPatternSettings>(json);
                if (!string.IsNullOrWhiteSpace(saved.targetObjectId))
                    pattern.targetObjectId = saved.targetObjectId.Trim();
                pattern.targetScaleRatio = MaxComponents(saved.targetScaleRatio, 0.01f);
                pattern.scaleToleranceRatio = MaxComponents(saved.scaleToleranceRatio, 0f);
                pattern.positionToleranceMeters = MaxComponents(saved.positionToleranceMeters, 0.001f);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ScalePlacementChallenge] Failed to load pattern {pattern.id}: {e.Message}");
            }
        }
    }

    private void SavePatternSettings(Pattern pattern)
    {
        if (!persistPatternSettings || pattern == null || string.IsNullOrWhiteSpace(patternSettingsPrefsKeyPrefix))
            return;
        SavedPatternSettings saved = new SavedPatternSettings
        {
            targetObjectId = pattern.targetObjectId,
            targetScaleRatio = pattern.targetScaleRatio,
            scaleToleranceRatio = pattern.scaleToleranceRatio,
            positionToleranceMeters = pattern.positionToleranceMeters
        };
        PlayerPrefs.SetString(patternSettingsPrefsKeyPrefix + pattern.id.ToUpperInvariant(), JsonUtility.ToJson(saved));
        PlayerPrefs.Save();
    }

    private static Vector3 MaxComponents(Vector3 value, float minimum)
    {
        return new Vector3(
            Mathf.Max(minimum, value.x),
            Mathf.Max(minimum, value.y),
            Mathf.Max(minimum, value.z));
    }

    private static float MaxComponent(Vector3 value)
    {
        return Mathf.Max(value.x, Mathf.Max(value.y, value.z));
    }

    private static bool ApproximatelyUniform(Vector3 value)
    {
        const float epsilon = 0.0001f;
        return Mathf.Abs(value.x - value.y) <= epsilon &&
               Mathf.Abs(value.x - value.z) <= epsilon;
    }

    private static Vector3 AbsScaleRatio(Vector3 current, Vector3 baseline)
    {
        return new Vector3(
            Mathf.Abs(baseline.x) > 1e-6f ? Mathf.Abs(current.x / baseline.x) : 1f,
            Mathf.Abs(baseline.y) > 1e-6f ? Mathf.Abs(current.y / baseline.y) : 1f,
            Mathf.Abs(baseline.z) > 1e-6f ? Mathf.Abs(current.z / baseline.z) : 1f);
    }

    private static bool IsChallengeObjectRenderer(Renderer renderer)
    {
        if (renderer == null || !renderer.enabled || renderer is LineRenderer || renderer.GetComponentInParent<DeformHandle>() != null)
            return false;
        string rendererName = renderer.name.ToLowerInvariant();
        return !rendererName.Contains("axis") &&
               !rendererName.Contains("debug") &&
               !rendererName.Contains("coordinate");
    }

    private void OnDestroy()
    {
        if (ringMaterial != null) Destroy(ringMaterial);
    }
}
