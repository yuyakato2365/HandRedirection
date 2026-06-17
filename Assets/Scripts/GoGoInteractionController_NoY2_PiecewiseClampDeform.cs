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

        [Header("Shape")]
        public Vector3 baseHalfExtents = new Vector3(0.5f, 0.5f, 0.5f);
        public bool autoScaleBaseHalfExtents = true;

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
    [Tooltip("Keep off when only the VR visual should grow. If on, the blend/recognition surface grows with the warped visual scale.")]
    public bool scaleBlendSurfaceByCommittedRatio = false;
    [Tooltip("Continuously sync the object-local hand mapping ratio from the current warped visual scale, including while the object is being stretched.")]
    public bool syncCommittedRatioFromWarpedScale = true;
    public float nearRadius = 0.12f;
    public float farRadius = 0.30f;

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

    void Awake()
    {
        InitializeAllEntries();
        RegisterDeformCallbacks();
    }

    void OnDestroy()
    {
        UnregisterDeformCallbacks();
    }

    void OnDisable()
    {
        ResetRedirectorsToOriginalHands();
    }

    public void ResetRedirectorsToOriginalHands()
    {
        CopyHandPose(leftHandOriginal, leftHandRedirector);
        CopyHandPose(rightHandOriginal, rightHandRedirector);
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

        if (cubeWarped != null)
        {
            legacy.baseWarpedScale = cubeWarped.localScale;
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

        if (entry.warpedObject != null)
        {
            entry.baseWarpedScale = entry.warpedObject.localScale;
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
            entry.deformEndHandler = finalLocalScale => CommitRatio(captured, finalLocalScale);
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

    void CommitRatio(WarpObjectEntry entry, Vector3 currentLocalScale)
    {
        if (entry == null || !entry.baseScaleInitialized) return;

        entry.committedRatio = ComputeWarpedScaleRatio(entry, currentLocalScale);
    }

    void RefreshCommittedRatioFromWarpedScale(WarpObjectEntry entry)
    {
        if (entry == null || entry.warpedObject == null || !entry.baseScaleInitialized)
            return;

        entry.committedRatio = ComputeWarpedScaleRatio(entry, entry.warpedObject.localScale);
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
        beta = ComputeObjectBlend(d);
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

        Vector3 blendRatio = scaleBlendSurfaceByCommittedRatio ? ratio : Vector3.one;
        float hx = (halfExtents.x + sideRangeMargin) * blendRatio.x;
        float hy = (halfExtents.y + sideRangeMargin) * blendRatio.y;
        float hz = (halfExtents.z + sideRangeMargin) * blendRatio.z;

        float ex = Mathf.Max(ax - hx, 0f);
        float ey = blendDistanceIncludeY ? Mathf.Max(ay - hy, 0f) : 0f;
        float ez = Mathf.Max(az - hz, 0f);
        return Mathf.Sqrt(ex * ex + ey * ey + ez * ez);
    }

    float ComputeObjectBlend(float d)
    {
        if (d <= nearRadius) return 1f;
        if (d >= farRadius) return 0f;

        float t = (d - nearRadius) / (farRadius - nearRadius);
        float s = t * t * (3f - 2f * t);
        return 1f - s;
    }
}
