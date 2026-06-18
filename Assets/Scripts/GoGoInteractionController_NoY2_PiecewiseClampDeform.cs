using System;
using System.Collections.Generic;
using UnityEngine;

public class GoGoInteractionController_NoY3 : MonoBehaviour
{
    [Serializable]
    public class WarpObjectEntry
    {
        [Header("Identity")]
        public string name = "Object";
        public bool enabled = true;

        [Header("Real pose source")]
        public Transform realWorldSource;
        public Transform realObject;

        [Header("Warped visual")]
        public Transform warpedObject;
        [Tooltip("Transform whose localScale represents the actual deformed visual size. If unset, warpedObject is used. Set this to the scaled mesh/child when DeformableCubeController scales a child instead of the root.")]
        public Transform warpedScaleSource;

        [Header("Shape")]
        public Vector3 baseHalfExtents = new Vector3(0.5f, 0.5f, 0.5f);
        public bool autoScaleBaseHalfExtents = true;

        [Header("Gaze Target Sphere")]
        [Tooltip("Use a fixed gaze target sphere radius instead of deriving it from the visible Renderer bounds.")]
        public bool overrideGazeTargetRadius = false;
        [Min(0f)] public float gazeTargetRadius = 0.1f;

        [NonSerialized] public Vector3 baseWarpedScale = Vector3.one;
        [NonSerialized] public Vector3 initialBaseHalfExtents = Vector3.one;
        [NonSerialized] public Vector3 initialShapeSourceScale = Vector3.one;
        [NonSerialized] public bool baseScaleInitialized = false;
        [NonSerialized] public Vector3 committedRatio = Vector3.one;
        [NonSerialized] public DeformableCubeController deformCtrl;
        [NonSerialized] public Action<Vector3> deformEndHandler;
    }

    [Header("HMD / CameraCenter")]
    public Transform cameraCenter;

    [Header("Desk / Origin")]
    public Transform deskOrigin;
    [Tooltip("Optional separate origin for redirection mapping. If unset, deskOrigin is used.")]
    public Transform redirectionOrigin;
    public bool useRedirectionOriginWhenAvailable = false;
    public bool suppressRedirection;

    [Header("Desk Mapping")]
    public float deskWidthScale = 1.0f;
    public float deskDepthScale = 1.0f;
    public float deskEntryBlendHalfWidth = 0.05f;

    public enum DeskAxisMode
    {
        Standard,
        SwapXZ
    }

    [Tooltip("Use SwapXZ when desk local x/z are interpreted in the opposite way from the intended width/depth warp axes.")]
    public DeskAxisMode deskAxisMode = DeskAxisMode.Standard;
    public bool invertDeskWarpX = false;
    public bool invertDeskWarpZ = false;

    [Header("Hands")]
    public Transform leftHandOriginal;
    public Transform leftHandRedirector;
    public Transform rightHandOriginal;
    public Transform rightHandRedirector;

    [Header("IndexTip Points (optional)")]
    public Transform leftIndexTipPoint;
    public Transform rightIndexTipPoint;

    [Header("Target Objects (multi-object)")]
    public List<WarpObjectEntry> objects = new List<WarpObjectEntry>();

    [Header("Legacy single-object fallback")]
    public Transform cubeRealWorldSource;
    public Transform cubeReal;
    public Transform cubeWarped;
    public Vector3 baseHalfExtents = new Vector3(0.5f, 0.5f, 0.5f);

    [Header("Linear warp (fallback)")]
    public float linearK = 0.0f;
    public bool linearWarpAffectX = true;
    public bool linearWarpAffectY = false;
    public bool linearWarpAffectZ = true;

    [Header("Object-local mapping (G) / Blend")]
    public bool useSurfaceDistanceForBlend = true;
    public bool blendDistanceIncludeY = true;
    [Tooltip("Scale the blend surface and Near/Far shell per axis with the warped object's committed scale ratio.")]
    public bool scaleBlendSurfaceByCommittedRatio = true;
    [Tooltip("Sync the object-local hand mapping ratio from the warped visual scale.")]
    public bool syncCommittedRatioFromWarpedScale = true;
    [Tooltip("Keep the hand mapping ratio fixed while an object is being resized, then apply the final scale when resizing ends.")]
    public bool freezeCommittedRatioWhileDeforming = true;
    public float nearRadius = 0.12f;
    public float farRadius = 0.30f;

    [Header("HMD Forward Gaze / Blend Radii")]
    [Tooltip("Use the HMD forward ray to apply nearRadius/farRadius only to the object being viewed.")]
    public bool useHmdForwardGazeRadii = true;
    [Tooltip("Near radius used for objects outside the HMD forward gaze.")]
    public float nonGazedNearRadius = 0.05f;
    [Tooltip("Far radius used for objects outside the HMD forward gaze.")]
    public float nonGazedFarRadius = 0.10f;
    public float gazeMaxDistance = 10f;
    [Tooltip("Local pitch offset for the HMD gaze direction. Positive values tilt the gaze downward.")]
    public float gazePitchDownDegrees = 0f;
    [Tooltip("Seconds used to blend between gazed and non-gazed radii. Set to zero for an immediate switch.")]
    public float gazeTransitionSeconds = 0.15f;
    [Tooltip("Log the gazed object and non-gazed objects whenever the HMD forward gaze target changes.")]
    public bool logHmdGazeStateChanges = true;
    [Tooltip("Repeat the current gaze state at this interval. Set to zero to log only when the target changes.")]
    public float gazeLogIntervalSeconds = 1f;

    [Header("HMD Gaze Debug Visualization")]
    public bool showHmdGazeDebugVisuals = false;
    [Min(0.0005f)] public float gazeOutlineWidth = 0.004f;
    public Color gazeOutlineColor = new Color(1f, 0f, 0f, 0.65f);
    public Color nearRadiusColor = new Color(1f, 0.75f, 0f, 0.14f);
    public Color farRadiusColor = new Color(0f, 0.5f, 1f, 0.09f);
    [Min(0.0005f)] public float gazeRayWidth = 0.003f;
    [Min(0f), Tooltip("Radius of the HMD gaze test. Zero uses a line; a positive value uses a thick cylindrical gaze and matching visualization.")]
    public float gazeDetectionRadius = 0.01f;
    public Color gazeRayColor = new Color(0f, 1f, 1f, 0.8f);
    public Color gazeRayHitColor = new Color(1f, 0.2f, 0.1f, 0.9f);

    [Header("Remote FarRadius (UDP knob)")]
    public bool enableRemoteFarRadius = true;
    public UdpKnobReceiver knobReceiver;
    public float farRadiusAddMax = 1.0f;
    public float farEpsilon = 0.001f;

    [Header("Frame Option")]
    public bool useYawOnlyFrame = true;
    public float sideRangeMargin = 0f;

    [Header("Multi-object selection")]
    public float minBetaToEngage = 0.01f;
    public float switchMargin = 0.05f;
    public bool useSelectionHysteresis = true;

    private int _lastSelectedIndexLeft = -1;
    private int _lastSelectedIndexRight = -1;
    private readonly Dictionary<Transform, float> _gazeWeights = new Dictionary<Transform, float>();
    private Transform _currentGazedObject;
    private Renderer _currentGazedRenderer;
    private Bounds _currentGazedBounds;
    private Transform _lastLoggedGazedObject;
    private bool _hasLoggedGazeState;
    private float _nextGazeLogTime;
    private GameObject _gazeDebugRoot;
    private LineRenderer _gazeRayLine;
    private GameObject _gazeRayCylinder;
    private LineRenderer[] _gazeOutlineLines;
    private readonly Dictionary<Transform, RadiusDebugVisual> _radiusDebugVisuals = new Dictionary<Transform, RadiusDebugVisual>();
    private Material _gazeOutlineMaterial;
    private Material _gazeRayMaterial;
    private Material _nearRadiusMaterial;
    private Material _farRadiusMaterial;
    private float _currentGazeHitDistance;

    private sealed class RadiusDebugVisual
    {
        public GameObject root;
        public GameObject nearSphere;
        public GameObject farSphere;
    }

    private static readonly int[,] BoxEdgeIndices =
    {
        { 0, 1 }, { 1, 3 }, { 3, 2 }, { 2, 0 },
        { 4, 5 }, { 5, 7 }, { 7, 6 }, { 6, 4 },
        { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 }
    };

    void Awake()
    {
        InitializeAllEntries();
        RegisterDeformCallbacks();
    }

    void OnDestroy()
    {
        UnregisterDeformCallbacks();
        DestroyGazeDebugVisuals();
    }

    void OnDisable()
    {
        ResetRedirectorsToOriginalHands();
        if (_gazeDebugRoot != null)
            _gazeDebugRoot.SetActive(false);
    }

    public void ResetRedirectorsToOriginalHands()
    {
        CopyHandPose(leftHandOriginal, leftHandRedirector);
        CopyHandPose(rightHandOriginal, rightHandRedirector);
    }

    public void SetHmdGazeDebugVisuals(bool enabled)
    {
        showHmdGazeDebugVisuals = enabled;
        if (!enabled && _gazeDebugRoot != null)
            _gazeDebugRoot.SetActive(false);
    }

    public void ToggleHmdGazeDebugVisuals()
    {
        SetHmdGazeDebugVisuals(!showHmdGazeDebugVisuals);
    }

    void LateUpdate()
    {
        if (!UseDeskMapping() && cameraCenter == null) return;
        if (suppressRedirection)
        {
            ResetRedirectorsToOriginalHands();
            return;
        }

        if (enableRemoteFarRadius && knobReceiver != null)
        {
            float add = Mathf.Clamp01(knobReceiver.knob01) * farRadiusAddMax;
            float targetFar = nearRadius + add;
            farRadius = Mathf.Max(targetFar, nearRadius + farEpsilon);
        }

        RefreshAutoScaledBaseHalfExtents();
        RefreshCommittedRatiosFromWarpedScales();
        EnsureAllWarpedObjectsDetached();
        UpdateAllWarpedObjectVisuals();
        UpdateHmdForwardGazeWeights();

        if (leftHandOriginal != null && leftHandRedirector != null)
            UpdateHand(leftHandOriginal, leftHandRedirector, leftIndexTipPoint, ref _lastSelectedIndexLeft);

        if (rightHandOriginal != null && rightHandRedirector != null)
            UpdateHand(rightHandOriginal, rightHandRedirector, rightIndexTipPoint, ref _lastSelectedIndexRight);
    }

    bool UseDeskMapping() => GetWarpOrigin() != null;

    void RefreshAutoScaledBaseHalfExtents()
    {
        List<WarpObjectEntry> active = EnumerateActiveEntries();
        for (int i = 0; i < active.Count; i++)
            ApplyAutoScaledBaseHalfExtents(active[i]);
    }

    void RefreshCommittedRatiosFromWarpedScales()
    {
        if (!syncCommittedRatioFromWarpedScale)
            return;

        List<WarpObjectEntry> active = EnumerateActiveEntries();
        for (int i = 0; i < active.Count; i++)
            RefreshCommittedRatioFromWarpedScale(active[i]);
    }

    Transform GetWarpOrigin()
    {
        if (useRedirectionOriginWhenAvailable && redirectionOrigin != null)
            return redirectionOrigin;

        return deskOrigin;
    }

    List<WarpObjectEntry> EnumerateActiveEntries()
    {
        List<WarpObjectEntry> active = new List<WarpObjectEntry>();

        if (objects != null)
        {
            for (int i = 0; i < objects.Count; i++)
            {
                WarpObjectEntry entry = objects[i];
                if (entry == null || !entry.enabled) continue;
                active.Add(entry);
            }
        }

        if (active.Count == 0)
        {
            WarpObjectEntry legacy = BuildLegacyEntryIfPossible();
            if (legacy != null)
                active.Add(legacy);
        }

        return active;
    }

    WarpObjectEntry BuildLegacyEntryIfPossible()
    {
        if (cubeRealWorldSource == null && cubeReal == null && cubeWarped == null)
            return null;

        WarpObjectEntry legacy = new WarpObjectEntry();
        legacy.name = "LegacyCube";
        legacy.realWorldSource = cubeRealWorldSource;
        legacy.realObject = cubeReal;
        legacy.warpedObject = cubeWarped;
        legacy.baseHalfExtents = baseHalfExtents;
        legacy.initialBaseHalfExtents = baseHalfExtents;
        legacy.initialShapeSourceScale = GetShapeSourceScale(legacy);
        legacy.committedRatio = Vector3.one;

        Transform legacyScaleSource = GetWarpedScaleSource(legacy);
        if (legacyScaleSource != null)
        {
            legacy.baseWarpedScale = legacyScaleSource.localScale;
            legacy.baseScaleInitialized = true;
        }

        return legacy;
    }

    void InitializeAllEntries()
    {
        List<WarpObjectEntry> active = EnumerateActiveEntries();
        for (int i = 0; i < active.Count; i++)
            InitializeEntry(active[i]);
    }

    void InitializeEntry(WarpObjectEntry entry)
    {
        if (entry == null) return;

        entry.initialBaseHalfExtents = entry.baseHalfExtents;
        entry.initialShapeSourceScale = GetShapeSourceScale(entry);

        Transform scaleSource = GetWarpedScaleSource(entry);
        if (scaleSource != null)
        {
            entry.baseWarpedScale = scaleSource.localScale;
            entry.baseScaleInitialized = true;
        }
        else
        {
            entry.baseWarpedScale = Vector3.one;
            entry.baseScaleInitialized = false;
        }

        entry.committedRatio = Vector3.one;
        ApplyAutoScaledBaseHalfExtents(entry);
    }

    void RegisterDeformCallbacks()
    {
        List<WarpObjectEntry> active = EnumerateActiveEntries();
        for (int i = 0; i < active.Count; i++)
        {
            WarpObjectEntry entry = active[i];
            if (entry == null || entry.warpedObject == null) continue;

            entry.deformCtrl = entry.warpedObject.GetComponent<DeformableCubeController>();
            if (entry.deformCtrl == null) continue;

            WarpObjectEntry captured = entry;
            entry.deformEndHandler = _ => CommitRatio(captured);
            entry.deformCtrl.OnDeformEnd -= entry.deformEndHandler;
            entry.deformCtrl.OnDeformEnd += entry.deformEndHandler;
        }
    }

    void UnregisterDeformCallbacks()
    {
        List<WarpObjectEntry> active = EnumerateActiveEntries();
        for (int i = 0; i < active.Count; i++)
        {
            WarpObjectEntry entry = active[i];
            if (entry == null || entry.deformCtrl == null || entry.deformEndHandler == null) continue;

            entry.deformCtrl.OnDeformEnd -= entry.deformEndHandler;
            entry.deformEndHandler = null;
            entry.deformCtrl = null;
        }
    }

    void CommitRatio(WarpObjectEntry entry)
    {
        if (entry == null || !entry.baseScaleInitialized) return;

        Transform scaleSource = GetWarpedScaleSource(entry);
        if (scaleSource == null) return;

        entry.committedRatio = ComputeWarpedScaleRatio(entry, scaleSource.localScale);
    }

    void RefreshCommittedRatioFromWarpedScale(WarpObjectEntry entry)
    {
        if (entry == null || !entry.baseScaleInitialized)
            return;
        if (freezeCommittedRatioWhileDeforming && IsEntryDeforming(entry))
            return;

        Transform scaleSource = GetWarpedScaleSource(entry);
        if (scaleSource == null) return;

        entry.committedRatio = ComputeWarpedScaleRatio(entry, scaleSource.localScale);
    }

    static Transform GetWarpedScaleSource(WarpObjectEntry entry)
    {
        if (entry == null)
            return null;
        return entry.warpedScaleSource != null ? entry.warpedScaleSource : entry.warpedObject;
    }

    static Vector3 ComputeWarpedScaleRatio(WarpObjectEntry entry, Vector3 currentLocalScale)
    {
        float bx = Mathf.Abs(entry.baseWarpedScale.x) < 1e-6f ? 1e-6f : entry.baseWarpedScale.x;
        float by = Mathf.Abs(entry.baseWarpedScale.y) < 1e-6f ? 1e-6f : entry.baseWarpedScale.y;
        float bz = Mathf.Abs(entry.baseWarpedScale.z) < 1e-6f ? 1e-6f : entry.baseWarpedScale.z;

        return new Vector3(
            currentLocalScale.x / bx,
            currentLocalScale.y / by,
            currentLocalScale.z / bz
        );
    }

    void ApplyAutoScaledBaseHalfExtents(WarpObjectEntry entry)
    {
        if (entry == null || !entry.autoScaleBaseHalfExtents)
            return;

        Vector3 currentScale = GetShapeSourceScale(entry);
        Vector3 baseScale = entry.initialShapeSourceScale;
        Vector3 ratio = new Vector3(
            SafeScaleRatio(currentScale.x, baseScale.x),
            SafeScaleRatio(currentScale.y, baseScale.y),
            SafeScaleRatio(currentScale.z, baseScale.z)
        );

        entry.baseHalfExtents = new Vector3(
            Mathf.Abs(entry.initialBaseHalfExtents.x * ratio.x),
            Mathf.Abs(entry.initialBaseHalfExtents.y * ratio.y),
            Mathf.Abs(entry.initialBaseHalfExtents.z * ratio.z)
        );
    }

    static float SafeScaleRatio(float current, float basis)
    {
        basis = Mathf.Abs(basis) < 1e-6f ? 1e-6f : basis;
        return current / basis;
    }

    static Vector3 GetShapeSourceScale(WarpObjectEntry entry)
    {
        Transform source = entry.realObject != null
            ? entry.realObject
            : (entry.realWorldSource != null ? entry.realWorldSource : entry.warpedObject);

        if (source == null)
            return Vector3.one;

        return source.lossyScale;
    }

    void EnsureAllWarpedObjectsDetached()
    {
        List<WarpObjectEntry> active = EnumerateActiveEntries();
        for (int i = 0; i < active.Count; i++)
            EnsureWarpedObjectDetached(active[i]);
    }

    void EnsureWarpedObjectDetached(WarpObjectEntry entry)
    {
        if (entry == null || entry.warpedObject == null || cameraCenter == null) return;
        if (entry.warpedObject.parent == cameraCenter)
            entry.warpedObject.SetParent(null, true);
    }

    float GetCameraYawDeg()
    {
        Vector3 fwd = cameraCenter.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-8f) return 0f;
        fwd.Normalize();
        return Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
    }

    Vector3 WorldToLocalForWarp(Vector3 world)
    {
        if (UseDeskMapping())
            return WorldToDeskLocal(world);

        Vector3 rel = world - cameraCenter.position;
        if (!useYawOnlyFrame)
            return Quaternion.Inverse(cameraCenter.rotation) * rel;

        float yaw = GetCameraYawDeg();
        Quaternion invYaw = Quaternion.Inverse(Quaternion.Euler(0f, yaw, 0f));
        return invYaw * rel;
    }

    Vector3 LocalToWorldForWarp(Vector3 local)
    {
        if (UseDeskMapping())
            return DeskLocalToWorld(local);

        if (!useYawOnlyFrame)
            return cameraCenter.position + (cameraCenter.rotation * local);

        float yaw = GetCameraYawDeg();
        Quaternion yawQ = Quaternion.Euler(0f, yaw, 0f);
        return cameraCenter.position + (yawQ * local);
    }

    Vector3 WorldToDeskLocal(Vector3 world)
    {
        Transform origin = GetWarpOrigin();
        Vector3 deskLocal;

        if (origin == null)
            deskLocal = world;
        else if (cameraCenter != null)
        {
            Vector3 headInDesk = origin.InverseTransformPoint(cameraCenter.position);
            Vector3 handFromHeadWorld = world - cameraCenter.position;
            Vector3 handFromHeadDesk = Quaternion.Inverse(origin.rotation) * handFromHeadWorld;
            deskLocal = headInDesk + handFromHeadDesk;
        }
        else
        {
            deskLocal = origin.InverseTransformPoint(world);
        }

        return DeskLocalToWarpLocal(deskLocal);
    }

    Vector3 DeskLocalToWorld(Vector3 local)
    {
        Transform origin = GetWarpOrigin();
        Vector3 deskLocal = WarpLocalToDeskLocal(local);
        if (origin == null)
            return deskLocal;
        return origin.TransformPoint(deskLocal);
    }

    Vector3 DeskLocalToWarpLocal(Vector3 deskLocal)
    {
        Vector3 warpLocal = deskAxisMode == DeskAxisMode.SwapXZ
            ? new Vector3(deskLocal.z, deskLocal.y, deskLocal.x)
            : deskLocal;

        if (invertDeskWarpX)
            warpLocal.x = -warpLocal.x;
        if (invertDeskWarpZ)
            warpLocal.z = -warpLocal.z;

        return warpLocal;
    }

    Vector3 WarpLocalToDeskLocal(Vector3 warpLocal)
    {
        if (invertDeskWarpX)
            warpLocal.x = -warpLocal.x;
        if (invertDeskWarpZ)
            warpLocal.z = -warpLocal.z;

        if (deskAxisMode == DeskAxisMode.SwapXZ)
            return new Vector3(warpLocal.z, warpLocal.y, warpLocal.x);

        return warpLocal;
    }

    Vector3 LinearWarpLocal(Vector3 pLocal)
    {
        if (UseDeskMapping())
        {
            float scaleZ = Mathf.Lerp(1f, deskDepthScale, ComputeDeskEntryWeight(pLocal.z));
            return new Vector3(pLocal.x * deskWidthScale, pLocal.y, pLocal.z * scaleZ);
        }

        float s = 1f + linearK;
        return new Vector3(
            linearWarpAffectX ? pLocal.x * s : pLocal.x,
            linearWarpAffectY ? pLocal.y * s : pLocal.y,
            linearWarpAffectZ ? pLocal.z * s : pLocal.z
        );
    }

    float ComputeDeskEntryWeight(float zLocal)
    {
        float halfWidth = Mathf.Max(1e-4f, deskEntryBlendHalfWidth);
        float t = Mathf.InverseLerp(-halfWidth, halfWidth, zLocal);
        return t * t * (3f - 2f * t);
    }

    bool TryGetRealPose(WarpObjectEntry entry, out Vector3 objectPosW, out Quaternion objectRotW)
    {
        if (entry != null && entry.realWorldSource != null)
        {
            objectPosW = entry.realWorldSource.position;
            objectRotW = entry.realWorldSource.rotation;
            return true;
        }

        if (entry != null && entry.realObject != null)
        {
            objectPosW = entry.realObject.position;
            objectRotW = entry.realObject.rotation;
            return true;
        }

        objectPosW = default;
        objectRotW = default;
        return false;
    }

    void UpdateAllWarpedObjectVisuals()
    {
        List<WarpObjectEntry> active = EnumerateActiveEntries();
        for (int i = 0; i < active.Count; i++)
            UpdateWarpedObjectVisual(active[i]);
    }

    void UpdateHmdForwardGazeWeights()
    {
        List<WarpObjectEntry> active = EnumerateActiveEntries();
        Transform gazedObject = useHmdForwardGazeRadii
            ? FindHmdForwardGazeTarget(active, out _currentGazedRenderer, out _currentGazedBounds, out _currentGazeHitDistance)
            : null;
        if (!useHmdForwardGazeRadii)
        {
            _currentGazedRenderer = null;
            _currentGazeHitDistance = 0f;
        }
        _currentGazedObject = gazedObject;
        float step = gazeTransitionSeconds <= 0f
            ? 1f
            : Time.deltaTime / Mathf.Max(gazeTransitionSeconds, 1e-4f);

        for (int i = 0; i < active.Count; i++)
        {
            WarpObjectEntry entry = active[i];
            Transform warpedObject = entry != null ? entry.warpedObject : null;
            if (warpedObject == null)
                continue;

            if (IsEntryDeforming(entry))
            {
                _gazeWeights[warpedObject] = 1f;
                continue;
            }

            float targetWeight = !useHmdForwardGazeRadii || warpedObject == gazedObject ? 1f : 0f;
            _gazeWeights.TryGetValue(warpedObject, out float currentWeight);
            _gazeWeights[warpedObject] = Mathf.MoveTowards(currentWeight, targetWeight, step);
        }

        LogHmdGazeStateIfChanged(active, gazedObject);
        UpdateGazeDebugVisuals(active, gazedObject);
    }

    void LogHmdGazeStateIfChanged(List<WarpObjectEntry> active, Transform gazedObject)
    {
        if (!logHmdGazeStateChanges || !useHmdForwardGazeRadii)
            return;
        bool targetChanged = !_hasLoggedGazeState || gazedObject != _lastLoggedGazedObject;
        bool periodicLogDue = gazeLogIntervalSeconds > 0f && Time.unscaledTime >= _nextGazeLogTime;
        if (!targetChanged && !periodicLogDue)
            return;

        _hasLoggedGazeState = true;
        _lastLoggedGazedObject = gazedObject;
        _nextGazeLogTime = Time.unscaledTime + Mathf.Max(0f, gazeLogIntervalSeconds);

        string gazedName = "None";
        List<string> nonGazedNames = new List<string>();
        for (int i = 0; i < active.Count; i++)
        {
            WarpObjectEntry entry = active[i];
            if (entry == null || entry.warpedObject == null)
                continue;

            string entryName = string.IsNullOrWhiteSpace(entry.name) ? entry.warpedObject.name : entry.name;
            GetEffectiveBlendRadii(entry, out float effectiveNear, out float effectiveFar, out float gazeWeight);
            string state = $"{entryName}(weight={gazeWeight:F2}, near={effectiveNear:F3}, far={effectiveFar:F3})";
            if (entry.warpedObject == gazedObject)
                gazedName = state;
            else
                nonGazedNames.Add(state);
        }

        string nonGazed = nonGazedNames.Count > 0 ? string.Join(", ", nonGazedNames) : "None";
        Debug.Log($"[HMD Gaze] GAZED: {gazedName} | NOT GAZED: {nonGazed}", this);
    }

    Transform FindHmdForwardGazeTarget(
        List<WarpObjectEntry> active,
        out Renderer gazedRenderer,
        out Bounds gazedBounds,
        out float hitDistance)
    {
        gazedRenderer = null;
        gazedBounds = default;
        hitDistance = 0f;
        if (cameraCenter == null)
            return null;

        for (int i = 0; i < active.Count; i++)
        {
            WarpObjectEntry entry = active[i];
            if (!IsEntryDeforming(entry) || entry.warpedObject == null)
                continue;

            if (TryGetWarpedVisualBounds(entry.warpedObject, out gazedRenderer, out gazedBounds))
                hitDistance = Mathf.Min(Vector3.Distance(cameraCenter.position, gazedBounds.center), Mathf.Max(0f, gazeMaxDistance));
            else
                hitDistance = Mathf.Max(0f, gazeMaxDistance);
            return entry.warpedObject;
        }

        Ray gazeRay = new Ray(cameraCenter.position, GetHmdGazeDirection());
        float closestDistance = Mathf.Max(0f, gazeMaxDistance);
        Transform closestObject = null;
        Renderer closestRenderer = null;
        Bounds closestBounds = default;

        for (int i = 0; i < active.Count; i++)
        {
            WarpObjectEntry entry = active[i];
            if (entry == null || entry.warpedObject == null)
                continue;

            if (TryIntersectWarpedVisualSphere(
                entry,
                gazeRay,
                closestDistance,
                out float distance,
                out Renderer hitRenderer,
                out Bounds hitBounds))
            {
                closestDistance = distance;
                closestObject = entry.warpedObject;
                closestRenderer = hitRenderer;
                closestBounds = hitBounds;
            }
        }

        gazedRenderer = closestRenderer;
        gazedBounds = closestBounds;
        hitDistance = closestObject != null ? closestDistance : Mathf.Max(0f, gazeMaxDistance);
        return closestObject;
    }

    bool TryIntersectWarpedVisualSphere(
        WarpObjectEntry entry,
        Ray gazeRay,
        float maxDistance,
        out float hitDistance,
        out Renderer hitRenderer,
        out Bounds meshBounds)
    {
        hitDistance = maxDistance;
        hitRenderer = null;
        meshBounds = default;
        if (entry == null || entry.warpedObject == null
            || !TryGetWarpedVisualBounds(entry.warpedObject, out hitRenderer, out meshBounds))
            return false;

        float targetRadius = entry.overrideGazeTargetRadius
            ? Mathf.Max(0f, entry.gazeTargetRadius)
            : meshBounds.extents.magnitude;
        return TryIntersectBoundsSphere(meshBounds.center, targetRadius, gazeRay, maxDistance, out hitDistance);
    }

    bool TryGetWarpedVisualBounds(Transform warpedObject, out Renderer representativeRenderer, out Bounds meshBounds)
    {
        representativeRenderer = null;
        meshBounds = default;

        Renderer[] renderers = warpedObject.GetComponentsInChildren<Renderer>(false);
        bool hasBounds = false;
        float largestRendererSize = -1f;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (!IsGazeCandidateRenderer(renderer, warpedObject))
                continue;

            Bounds rendererBounds = renderer.bounds;
            if (!hasBounds)
            {
                meshBounds = rendererBounds;
                hasBounds = true;
            }
            else
            {
                meshBounds.Encapsulate(rendererBounds);
            }

            float rendererSize = rendererBounds.size.sqrMagnitude;
            if (rendererSize > largestRendererSize)
            {
                largestRendererSize = rendererSize;
                representativeRenderer = renderer;
            }
        }

        return hasBounds;
    }

    bool IsEntryDeforming(WarpObjectEntry entry)
    {
        if (entry == null || entry.warpedObject == null)
            return false;

        if (entry.deformCtrl == null)
            entry.deformCtrl = entry.warpedObject.GetComponent<DeformableCubeController>();
        return entry.deformCtrl != null && entry.deformCtrl.IsDeforming;
    }

    bool TryIntersectBoundsSphere(
        Vector3 sphereCenter,
        float targetRadius,
        Ray gazeRay,
        float maxDistance,
        out float hitDistance)
    {
        Vector3 centerOffset = sphereCenter - gazeRay.origin;
        float centerProjection = Vector3.Dot(centerOffset, gazeRay.direction);
        float radius = Mathf.Max(0f, targetRadius) + Mathf.Max(0f, gazeDetectionRadius);
        float centerDistanceSquared = centerOffset.sqrMagnitude - centerProjection * centerProjection;
        float radiusSquared = radius * radius;

        hitDistance = maxDistance;
        if (centerDistanceSquared > radiusSquared)
            return false;

        float halfChord = Mathf.Sqrt(Mathf.Max(0f, radiusSquared - centerDistanceSquared));
        float nearDistance = centerProjection - halfChord;
        float farDistance = centerProjection + halfChord;
        float distance = nearDistance >= 0f ? nearDistance : farDistance;
        if (distance < 0f || distance > maxDistance)
            return false;

        hitDistance = distance;
        return true;
    }

    Vector3 GetHmdGazeDirection()
    {
        if (cameraCenter == null)
            return Vector3.forward;

        return (cameraCenter.rotation * Quaternion.Euler(gazePitchDownDegrees, 0f, 0f) * Vector3.forward).normalized;
    }

    void UpdateGazeDebugVisuals(List<WarpObjectEntry> active, Transform gazedObject)
    {
        if (!showHmdGazeDebugVisuals)
        {
            if (_gazeDebugRoot != null)
                _gazeDebugRoot.SetActive(false);
            return;
        }

        EnsureGazeDebugVisuals();
        _gazeDebugRoot.SetActive(true);
        UpdateTransparentMaterialColor(_gazeOutlineMaterial, gazeOutlineColor);
        UpdateTransparentMaterialColor(_gazeRayMaterial, gazeRayColor);
        UpdateTransparentMaterialColor(_nearRadiusMaterial, nearRadiusColor);
        UpdateTransparentMaterialColor(_farRadiusMaterial, farRadiusColor);

        foreach (RadiusDebugVisual visual in _radiusDebugVisuals.Values)
        {
            if (visual.root != null)
                visual.root.SetActive(false);
        }

        for (int i = 0; i < active.Count; i++)
        {
            WarpObjectEntry entry = active[i];
            if (entry == null || entry.warpedObject == null)
                continue;

            RadiusDebugVisual visual = GetOrCreateRadiusDebugVisual(entry.warpedObject);
            visual.root.SetActive(true);

            GetEffectiveBlendRadii(entry, out float effectiveNear, out float effectiveFar, out _);
            Vector3 center = entry.warpedObject.position;
            Quaternion rotation = entry.warpedObject.rotation;
            UpdateRadiusSphere(visual.nearSphere, center, rotation, GetRecognitionRadii(entry, effectiveNear));
            UpdateRadiusSphere(visual.farSphere, center, rotation, GetRecognitionRadii(entry, effectiveFar));
        }

        bool hasGazedRenderer = gazedObject != null && _currentGazedRenderer != null;
        UpdateGazeRay(hasGazedRenderer);
        SetGazeOutlineVisible(hasGazedRenderer);
        if (hasGazedRenderer)
            UpdateGazeOutline(_currentGazedBounds);
    }

    RadiusDebugVisual GetOrCreateRadiusDebugVisual(Transform warpedObject)
    {
        if (_radiusDebugVisuals.TryGetValue(warpedObject, out RadiusDebugVisual visual))
            return visual;

        GameObject root = new GameObject($"{warpedObject.name} Blend Radii");
        root.hideFlags = HideFlags.DontSave;
        root.transform.SetParent(_gazeDebugRoot.transform, false);

        visual = new RadiusDebugVisual
        {
            root = root,
            nearSphere = CreateRadiusSphere("Near Radius", _nearRadiusMaterial, root.transform),
            farSphere = CreateRadiusSphere("Far Radius", _farRadiusMaterial, root.transform)
        };
        _radiusDebugVisuals.Add(warpedObject, visual);
        return visual;
    }

    Vector3 GetRecognitionRadii(WarpObjectEntry entry, float shellRadius)
    {
        Vector3 ratio = scaleBlendSurfaceByCommittedRatio ? AbsRatio(entry.committedRatio) : Vector3.one;
        return new Vector3(
            (entry.baseHalfExtents.x + sideRangeMargin + shellRadius) * ratio.x,
            (entry.baseHalfExtents.y + sideRangeMargin + shellRadius) * ratio.y,
            (entry.baseHalfExtents.z + sideRangeMargin + shellRadius) * ratio.z);
    }

    void EnsureGazeDebugVisuals()
    {
        if (_gazeDebugRoot != null)
            return;

        _gazeDebugRoot = new GameObject("HMD Gaze Debug Visuals");
        _gazeDebugRoot.hideFlags = HideFlags.DontSave;

        _gazeOutlineMaterial = CreateTransparentUnlitMaterial(gazeOutlineColor, 20);
        _gazeRayMaterial = CreateTransparentUnlitMaterial(gazeRayColor, 30);
        _nearRadiusMaterial = CreateTransparentUnlitMaterial(nearRadiusColor, 10);
        _farRadiusMaterial = CreateTransparentUnlitMaterial(farRadiusColor, 0);

        _gazeOutlineLines = new LineRenderer[12];

        GameObject rayObject = new GameObject("HMD Forward Gaze Ray");
        rayObject.transform.SetParent(_gazeDebugRoot.transform, false);
        _gazeRayLine = rayObject.AddComponent<LineRenderer>();
        _gazeRayLine.useWorldSpace = true;
        _gazeRayLine.positionCount = 2;
        _gazeRayLine.numCapVertices = 2;
        _gazeRayLine.sharedMaterial = _gazeRayMaterial;
        _gazeRayLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _gazeRayLine.receiveShadows = false;

        _gazeRayCylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        _gazeRayCylinder.name = "HMD Forward Gaze Cylinder";
        _gazeRayCylinder.hideFlags = HideFlags.DontSave;
        _gazeRayCylinder.layer = LayerMask.NameToLayer("Ignore Raycast");
        _gazeRayCylinder.transform.SetParent(_gazeDebugRoot.transform, false);
        Collider rayCollider = _gazeRayCylinder.GetComponent<Collider>();
        if (rayCollider != null)
        {
            rayCollider.enabled = false;
            Destroy(rayCollider);
        }
        Renderer rayRenderer = _gazeRayCylinder.GetComponent<Renderer>();
        rayRenderer.sharedMaterial = _gazeRayMaterial;
        rayRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rayRenderer.receiveShadows = false;

        for (int i = 0; i < _gazeOutlineLines.Length; i++)
        {
            GameObject edgeObject = new GameObject($"Gaze Edge {i + 1}");
            edgeObject.transform.SetParent(_gazeDebugRoot.transform, false);
            LineRenderer line = edgeObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.loop = false;
            line.numCapVertices = 2;
            line.startWidth = gazeOutlineWidth;
            line.endWidth = gazeOutlineWidth;
            line.startColor = gazeOutlineColor;
            line.endColor = gazeOutlineColor;
            line.sharedMaterial = _gazeOutlineMaterial;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            _gazeOutlineLines[i] = line;
        }

    }

    GameObject CreateRadiusSphere(string objectName, Material material, Transform parent)
    {
        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = objectName;
        sphere.hideFlags = HideFlags.DontSave;
        sphere.layer = LayerMask.NameToLayer("Ignore Raycast");
        sphere.transform.SetParent(parent, false);

        Collider collider = sphere.GetComponent<Collider>();
        if (collider != null)
        {
            collider.enabled = false;
            Destroy(collider);
        }

        Renderer renderer = sphere.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return sphere;
    }

    void SetGazeOutlineVisible(bool visible)
    {
        if (_gazeOutlineLines == null)
            return;

        for (int i = 0; i < _gazeOutlineLines.Length; i++)
            _gazeOutlineLines[i].enabled = visible;
    }

    void UpdateGazeRay(bool hasHit)
    {
        if (_gazeRayLine == null || cameraCenter == null)
            return;

        float distance = hasHit ? _currentGazeHitDistance : Mathf.Max(0f, gazeMaxDistance);
        Color color = hasHit ? gazeRayHitColor : gazeRayColor;
        Vector3 start = cameraCenter.position;
        Vector3 direction = GetHmdGazeDirection();
        Vector3 end = start + direction * distance;
        float detectionRadius = Mathf.Max(0f, gazeDetectionRadius);
        bool drawCylinder = detectionRadius > 1e-5f;

        UpdateTransparentMaterialColor(_gazeRayMaterial, color);
        _gazeRayLine.enabled = !drawCylinder;
        _gazeRayLine.startWidth = gazeRayWidth;
        _gazeRayLine.endWidth = gazeRayWidth;
        _gazeRayLine.startColor = color;
        _gazeRayLine.endColor = color;
        _gazeRayLine.SetPosition(0, start);
        _gazeRayLine.SetPosition(1, end);

        if (_gazeRayCylinder == null)
            return;

        _gazeRayCylinder.SetActive(drawCylinder);
        if (!drawCylinder)
            return;

        _gazeRayCylinder.transform.position = (start + end) * 0.5f;
        _gazeRayCylinder.transform.rotation = Quaternion.FromToRotation(Vector3.up, direction);
        _gazeRayCylinder.transform.localScale = new Vector3(
            detectionRadius * 2f,
            distance * 0.5f,
            detectionRadius * 2f);
    }

    void UpdateGazeOutline(Bounds bounds)
    {
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;
        Vector3[] corners =
        {
            new Vector3(min.x, min.y, min.z),
            new Vector3(max.x, min.y, min.z),
            new Vector3(min.x, max.y, min.z),
            new Vector3(max.x, max.y, min.z),
            new Vector3(min.x, min.y, max.z),
            new Vector3(max.x, min.y, max.z),
            new Vector3(min.x, max.y, max.z),
            new Vector3(max.x, max.y, max.z)
        };

        for (int i = 0; i < _gazeOutlineLines.Length; i++)
        {
            LineRenderer line = _gazeOutlineLines[i];
            line.startWidth = gazeOutlineWidth;
            line.endWidth = gazeOutlineWidth;
            line.startColor = gazeOutlineColor;
            line.endColor = gazeOutlineColor;
            line.SetPosition(0, corners[BoxEdgeIndices[i, 0]]);
            line.SetPosition(1, corners[BoxEdgeIndices[i, 1]]);
        }
    }

    static void UpdateRadiusSphere(GameObject sphere, Vector3 center, Quaternion rotation, Vector3 radii)
    {
        sphere.transform.position = center;
        sphere.transform.rotation = rotation;
        sphere.transform.localScale = new Vector3(
            Mathf.Max(0f, radii.x) * 2f,
            Mathf.Max(0f, radii.y) * 2f,
            Mathf.Max(0f, radii.z) * 2f);
    }

    static Material CreateTransparentUnlitMaterial(Color color, int queueOffset)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find("Sprites/Default");

        Material material = new Material(shader)
        {
            name = "HMD Gaze Debug Material",
            hideFlags = HideFlags.DontSave,
            renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + queueOffset
        };
        material.SetOverrideTag("RenderType", "Transparent");
        if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
        if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);
        if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.SetShaderPassEnabled("ShadowCaster", false);
        UpdateTransparentMaterialColor(material, color);
        return material;
    }

    static void UpdateTransparentMaterialColor(Material material, Color color)
    {
        if (material == null)
            return;
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
    }

    void DestroyGazeDebugVisuals()
    {
        if (_gazeDebugRoot != null)
            Destroy(_gazeDebugRoot);
        if (_gazeOutlineMaterial != null)
            Destroy(_gazeOutlineMaterial);
        if (_gazeRayMaterial != null)
            Destroy(_gazeRayMaterial);
        if (_nearRadiusMaterial != null)
            Destroy(_nearRadiusMaterial);
        if (_farRadiusMaterial != null)
            Destroy(_farRadiusMaterial);
        _gazeDebugRoot = null;
        _gazeRayLine = null;
        _gazeRayCylinder = null;
        _gazeOutlineLines = null;
        _radiusDebugVisuals.Clear();
        _gazeOutlineMaterial = null;
        _gazeRayMaterial = null;
        _nearRadiusMaterial = null;
        _farRadiusMaterial = null;
    }

    static bool IsActiveMeshRenderer(Renderer renderer)
    {
        return renderer != null
            && (renderer is MeshRenderer || renderer is SkinnedMeshRenderer)
            && renderer.enabled
            && !renderer.forceRenderingOff
            && renderer.gameObject.activeInHierarchy;
    }

    bool IsGazeCandidateRenderer(Renderer renderer, Transform warpedObject)
    {
        if (!IsActiveMeshRenderer(renderer))
            return false;

        if (_gazeDebugRoot != null && renderer.transform.IsChildOf(_gazeDebugRoot.transform))
            return false;

        DeformHandle handle = renderer.GetComponentInParent<DeformHandle>();
        return handle == null
            || (handle.transform != warpedObject && !handle.transform.IsChildOf(warpedObject));
    }

    void UpdateWarpedObjectVisual(WarpObjectEntry entry)
    {
        if (entry == null || entry.warpedObject == null) return;
        if (!TryGetRealPose(entry, out Vector3 objectPosW, out Quaternion objectRotW)) return;

        Vector3 objectLocal = WorldToLocalForWarp(objectPosW);
        Vector3 objectLocalWarped = LinearWarpLocal(objectLocal);
        Vector3 objectPosWarpW = LocalToWorldForWarp(objectLocalWarped);

        entry.warpedObject.position = objectPosWarpW;
        entry.warpedObject.rotation = objectRotW;
    }

    Vector3 MapPointWorld(Vector3 pointW, ref int lastSelectedIndex)
    {
        Vector3 pLocal = WorldToLocalForWarp(pointW);
        Vector3 pLocalWarped = LinearWarpLocal(pLocal);
        Vector3 pFW = LocalToWorldForWarp(pLocalWarped);

        List<WarpObjectEntry> active = EnumerateActiveEntries();
        if (active.Count == 0) return pFW;

        int bestIndex = -1;
        float bestBeta = -1f;
        Vector3 bestMapped = pFW;

        float prevBeta = -1f;
        Vector3 prevMapped = pFW;

        for (int i = 0; i < active.Count; i++)
        {
            if (!TryMapPointWorldByObject(pointW, active[i], out Vector3 gPointW, out float beta))
                continue;

            if (beta > bestBeta)
            {
                bestBeta = beta;
                bestIndex = i;
                bestMapped = gPointW;
            }

            if (i == lastSelectedIndex)
            {
                prevBeta = beta;
                prevMapped = gPointW;
            }
        }

        if (bestIndex < 0 || bestBeta < minBetaToEngage)
        {
            lastSelectedIndex = -1;
            return pFW;
        }

        int selectedIndex = bestIndex;
        float selectedBeta = bestBeta;
        Vector3 selectedMapped = bestMapped;

        if (useSelectionHysteresis && lastSelectedIndex >= 0 && lastSelectedIndex < active.Count && prevBeta >= minBetaToEngage)
        {
            if (bestIndex != lastSelectedIndex && bestBeta < prevBeta + switchMargin)
            {
                selectedIndex = lastSelectedIndex;
                selectedBeta = prevBeta;
                selectedMapped = prevMapped;
            }
        }

        lastSelectedIndex = selectedIndex;
        return Vector3.Lerp(pFW, selectedMapped, Mathf.Clamp01(selectedBeta));
    }

    bool TryMapPointWorldByObject(Vector3 pointW, WarpObjectEntry entry, out Vector3 mappedPointW, out float beta)
    {
        mappedPointW = pointW;
        beta = 0f;

        if (entry == null || !entry.enabled || entry.warpedObject == null)
            return false;

        if (!TryGetRealPose(entry, out Vector3 objectPosW, out Quaternion objectRotW))
            return false;

        Vector3 ratio = entry.committedRatio;
        Vector3 deltaRealW = pointW - objectPosW;
        Vector3 deltaRealLocal = Quaternion.Inverse(objectRotW) * deltaRealW;
        Vector3 deltaWarpLocal = ApplyPiecewiseClampDeform(deltaRealLocal, entry.baseHalfExtents, ratio);
        mappedPointW = entry.warpedObject.position + (entry.warpedObject.rotation * deltaWarpLocal);

        float d = ComputeBlendDistance(deltaRealLocal, entry.baseHalfExtents, ratio);
        beta = ComputeObjectBlend(d, entry);
        return true;
    }

    void UpdateHand(Transform original, Transform redirector, Transform indexTipPoint, ref int lastSelectedIndex)
    {
        if (indexTipPoint == null)
        {
            Vector3 hMapped = MapPointWorld(original.position, ref lastSelectedIndex);
            redirector.position = hMapped;
            redirector.rotation = original.rotation;
            return;
        }

        Vector3 t = indexTipPoint.position;
        Quaternion r = original.rotation;

        Vector3 tM = MapPointWorld(t, ref lastSelectedIndex);

        Vector3 w = original.position;
        Vector3 vLocal = Quaternion.Inverse(r) * (t - w);
        Vector3 wPlaced = tM - (r * vLocal);

        redirector.position = wPlaced;
        redirector.rotation = r;
    }

    static void CopyHandPose(Transform original, Transform redirector)
    {
        if (original == null || redirector == null)
            return;

        redirector.SetPositionAndRotation(original.position, original.rotation);
    }

    Vector3 ApplyPiecewiseClampDeform(Vector3 deltaRealLocal, Vector3 halfExtents, Vector3 ratio)
    {
        return new Vector3(
            MapAxisPiecewise(deltaRealLocal.x, halfExtents.x + sideRangeMargin, ratio.x),
            MapAxisPiecewise(deltaRealLocal.y, halfExtents.y + sideRangeMargin, ratio.y),
            MapAxisPiecewise(deltaRealLocal.z, halfExtents.z + sideRangeMargin, ratio.z)
        );
    }

    static float MapAxisPiecewise(float u, float halfExtent, float n)
    {
        if (halfExtent <= 1e-6f) return u;

        float a = Mathf.Abs(u);
        float s = (u >= 0f) ? 1f : -1f;
        if (a <= halfExtent) return n * u;
        return s * (n * halfExtent + (a - halfExtent));
    }

    float ComputeBlendDistance(Vector3 deltaRealLocal, Vector3 halfExtents, Vector3 ratio)
    {
        float ax = Mathf.Abs(deltaRealLocal.x);
        float ay = Mathf.Abs(deltaRealLocal.y);
        float az = Mathf.Abs(deltaRealLocal.z);

        if (!blendDistanceIncludeY)
            ay = 0f;

        if (!useSurfaceDistanceForBlend)
            return Mathf.Sqrt(ax * ax + ay * ay + az * az);

        Vector3 blendRatio = scaleBlendSurfaceByCommittedRatio ? AbsRatio(ratio) : Vector3.one;
        float hx = (halfExtents.x + sideRangeMargin) * blendRatio.x;
        float hy = (halfExtents.y + sideRangeMargin) * blendRatio.y;
        float hz = (halfExtents.z + sideRangeMargin) * blendRatio.z;

        float ex = Mathf.Max(ax - hx, 0f) / Mathf.Max(blendRatio.x, 1e-5f);
        float ey = blendDistanceIncludeY
            ? Mathf.Max(ay - hy, 0f) / Mathf.Max(blendRatio.y, 1e-5f)
            : 0f;
        float ez = Mathf.Max(az - hz, 0f) / Mathf.Max(blendRatio.z, 1e-5f);
        return Mathf.Sqrt(ex * ex + ey * ey + ez * ez);
    }

    static Vector3 AbsRatio(Vector3 ratio)
    {
        return new Vector3(Mathf.Abs(ratio.x), Mathf.Abs(ratio.y), Mathf.Abs(ratio.z));
    }

    float ComputeObjectBlend(float d, WarpObjectEntry entry)
    {
        GetEffectiveBlendRadii(entry, out float effectiveNearRadius, out float effectiveFarRadius, out _);

        if (d <= effectiveNearRadius) return 1f;
        if (d >= effectiveFarRadius) return 0f;

        float t = (d - effectiveNearRadius) / (effectiveFarRadius - effectiveNearRadius);
        float s = t * t * (3f - 2f * t);
        return 1f - s;
    }

    void GetEffectiveBlendRadii(WarpObjectEntry entry, out float effectiveNearRadius, out float effectiveFarRadius, out float gazeWeight)
    {
        gazeWeight = useHmdForwardGazeRadii ? 0f : 1f;
        if (useHmdForwardGazeRadii && entry != null && entry.warpedObject != null)
            _gazeWeights.TryGetValue(entry.warpedObject, out gazeWeight);

        effectiveNearRadius = Mathf.Max(0f, Mathf.Lerp(nonGazedNearRadius, nearRadius, gazeWeight));
        effectiveFarRadius = Mathf.Lerp(nonGazedFarRadius, farRadius, gazeWeight);
        effectiveFarRadius = Mathf.Max(effectiveFarRadius, effectiveNearRadius + Mathf.Max(farEpsilon, 1e-5f));
    }
}
