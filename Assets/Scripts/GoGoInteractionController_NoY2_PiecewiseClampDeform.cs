/*
// GoGoInteractionController_NoY3.cs
using System;
using UnityEngine;

/// <summary>
/// HMD中心の単純線形ワープ(F) と、物体近傍のローカル写像(G) をブレンドするコントローラ。
/// - ピンチ（変形）中は ratio を更新せず、変形終了時のみコミット（=コミット方式）。
/// - enableRemoteFarRadius が true の場合は farRadius を外部入力（UDP knob）で更新する。
///
/// ★追加（今回）
/// 指先点(Transform) が設定されている場合：
///   wrist と fingertip を両方写像して指先一致になるように redirector を平行移動する。
///   （非線形写像でも破綻しにくい：t' = f(t), w' = f(w) を使う）
/// </summary>
public class GoGoInteractionController_NoY3 : MonoBehaviour
{
    [Header("HMD / CameraCenter")]
    public Transform cameraCenter;

    [Header("Hands")]
    public Transform leftHandOriginal;
    public Transform leftHandRedirector;
    public Transform rightHandOriginal;
    public Transform rightHandRedirector;

    [Header("IndexTip Points (optional)")]
    [Tooltip("左手：指先点（空のGameObject等）。Hitchhike側が更新するTransformをアサイン。未設定なら従来の手首点のみで動作。")]
    public Transform leftIndexTipPoint;

    [Tooltip("右手：指先点（空のGameObject等）。Hitchhike側が更新するTransformをアサイン。未設定なら従来の手首点のみで動作。")]
    public Transform rightIndexTipPoint;

    [Header("Real cube pose source")]
    [Tooltip("推奨：ワールドに置いた推定結果（cubeWorld等）。設定されていればこちらを優先。")]
    public Transform cubeRealWorldSource;

    [Header("Cube objects")]
    [Tooltip("実物Cubeを表す（もしくは推定結果と一致させたい）Transform。未設定なら cubeRealWorldSource を使用。")]
    public Transform cubeReal;

    [Tooltip("VR側に表示する（変形される）Cube。HMDの子にしないこと。")]
    public Transform cubeWarped;

    [Header("Linear warp (F)")]
    [Tooltip("単純線形ワープの強さ（例：0.2 → 1.2倍）")]
    public float linearK = 0.0f;

    [Tooltip("F が影響する軸（デフォルト: XZ。Yも必要ならON）")]
    public bool linearWarpAffectX = true;
    public bool linearWarpAffectY = false;
    public bool linearWarpAffectZ = true;

    [Header("Object-local mapping (G) / Blend")]
    [Tooltip("実物Cubeの半サイズ（cubeRealローカル座標）。例: Cube(1m)なら (0.5,0.5,0.5)")]
    public Vector3 baseHalfExtents = new Vector3(0.5f, 0.5f, 0.5f);

    [Tooltip("nearRadius/farRadius の距離dを '変形後の箱表面' からの距離で評価する（推奨）")]
    public bool useSurfaceDistanceForBlend = true;

    [Tooltip("ブレンド距離dの計算にY成分も含める（推奨：true）。falseだとXZのみ。")]
    public bool blendDistanceIncludeY = true;

    [Tooltip("d <= nearRadius でG=1, d >= farRadius でG=0（実距離[m]）")]
    public float nearRadius = 0.12f;
    public float farRadius = 0.30f;

    [Header("Remote FarRadius (UDP knob)")]
    [Tooltip("UDPで受けた knob01(0..1) に応じて farRadius を nearRadius + (0..1)*farRadiusAddMax に更新する")]
    public bool enableRemoteFarRadius = true;

    [Tooltip("シーン上の UdpKnobReceiver をここにアサイン")]
    public UdpKnobReceiver knobReceiver;

    [Tooltip("knob01(0..1) を nearRadius に足す最大量[m]。要求通り +0..+1 にしたいなら 1.0")]
    public float farRadiusAddMax = 1.0f;

    [Tooltip("nearRadius < farRadius を保証するための最小差分[m]")]
    public float farEpsilon = 0.001f;

    [Header("Frame Option")]
    [Tooltip("Yaw-only（HMDのYawだけ）をローカル座標系として扱う（回頭でローカル軸がぶれにくい）")]
    public bool useYawOnlyFrame = true;

    [Tooltip("側面範囲（=内側で n 倍が効く範囲）を外側に拡張するマージン[m]。0なら元と同じ。")]
    public float sideRangeMargin = 0f;

    // ----------------------------
    // Internal state (commit ratio)
    // ----------------------------
    Vector3 baseWarpedScale;
    bool _baseScaleInitialized = false;
    Vector3 _committedRatio = Vector3.one;

    // Optional deform controller hook
    DeformableCubeController _deformCtrl;

    void Awake()
    {
        if (cubeWarped != null)
        {
            baseWarpedScale = cubeWarped.localScale;
            _baseScaleInitialized = true;
        }
        _committedRatio = Vector3.one;

        if (cubeWarped != null)
        {
            _deformCtrl = cubeWarped.GetComponent<DeformableCubeController>();
            if (_deformCtrl != null)
            {
                _deformCtrl.OnDeformEnd -= HandleDeformEnd;
                _deformCtrl.OnDeformEnd += HandleDeformEnd;
            }
        }
    }

    void OnDestroy()
    {
        if (_deformCtrl != null)
            _deformCtrl.OnDeformEnd -= HandleDeformEnd;
    }

    void LateUpdate()
    {
        if (cameraCenter == null) return;

        // farRadius を外部入力(UDP)で更新
        if (enableRemoteFarRadius && knobReceiver != null)
        {
            float add = Mathf.Clamp01(knobReceiver.knob01) * farRadiusAddMax;
            float targetFar = nearRadius + add;
            farRadius = Mathf.Max(targetFar, nearRadius + farEpsilon);
        }

        EnsureCubeWarpedDetached();
        UpdateCubeWarpedVisual();

        if (leftHandOriginal != null && leftHandRedirector != null)
            UpdateHand(leftHandOriginal, leftHandRedirector, leftIndexTipPoint);

        if (rightHandOriginal != null && rightHandRedirector != null)
            UpdateHand(rightHandOriginal, rightHandRedirector, rightIndexTipPoint);
    }

    // ----------------------------
    // Ratio commit
    // ----------------------------
    void HandleDeformEnd(Vector3 finalLocalScale) => CommitRatio(finalLocalScale);

    void CommitRatio(Vector3 currentLocalScale)
    {
        if (!_baseScaleInitialized) return;

        float bx = Mathf.Abs(baseWarpedScale.x) < 1e-6f ? 1e-6f : baseWarpedScale.x;
        float by = Mathf.Abs(baseWarpedScale.y) < 1e-6f ? 1e-6f : baseWarpedScale.y;
        float bz = Mathf.Abs(baseWarpedScale.z) < 1e-6f ? 1e-6f : baseWarpedScale.z;

        _committedRatio = new Vector3(
            currentLocalScale.x / bx,
            currentLocalScale.y / by,
            currentLocalScale.z / bz
        );
    }

    // ----------------------------
    // Parenting safety
    // ----------------------------
    void EnsureCubeWarpedDetached()
    {
        if (cubeWarped == null) return;
        if (cameraCenter == null) return;

        if (cubeWarped.parent == cameraCenter)
            cubeWarped.SetParent(null, worldPositionStays: true);
    }

    // ----------------------------
    // Coordinate frames (HMD local)
    // ----------------------------
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
        Vector3 rel = world - cameraCenter.position;

        if (!useYawOnlyFrame)
            return Quaternion.Inverse(cameraCenter.rotation) * rel;

        float yaw = GetCameraYawDeg();
        Quaternion invYaw = Quaternion.Inverse(Quaternion.Euler(0f, yaw, 0f));
        return invYaw * rel;
    }

    Vector3 LocalToWorldForWarp(Vector3 local)
    {
        if (!useYawOnlyFrame)
            return cameraCenter.position + (cameraCenter.rotation * local);

        float yaw = GetCameraYawDeg();
        Quaternion yawQ = Quaternion.Euler(0f, yaw, 0f);
        return cameraCenter.position + (yawQ * local);
    }

    // ----------------------------
    // Linear warp (F)
    // ----------------------------
    Vector3 LinearWarpLocal(Vector3 pLocal)
    {
        float s = 1f + linearK;

        return new Vector3(
            linearWarpAffectX ? pLocal.x * s : pLocal.x,
            linearWarpAffectY ? pLocal.y * s : pLocal.y,
            linearWarpAffectZ ? pLocal.z * s : pLocal.z
        );
    }

    // ----------------------------
    // Cube pose + visual update
    // ----------------------------
    bool TryGetCubeRealPose(out Vector3 cubePosW, out Quaternion cubeRotW)
    {
        if (cubeRealWorldSource != null)
        {
            cubePosW = cubeRealWorldSource.position;
            cubeRotW = cubeRealWorldSource.rotation;
            return true;
        }

        if (cubeReal != null)
        {
            cubePosW = cubeReal.position;
            cubeRotW = cubeReal.rotation;
            return true;
        }

        cubePosW = default;
        cubeRotW = default;
        return false;
    }

    void UpdateCubeWarpedVisual()
    {
        if (cubeWarped == null) return;
        if (!TryGetCubeRealPose(out Vector3 cubePosW, out Quaternion cubeRotW)) return;

        Vector3 cubeLocal = WorldToLocalForWarp(cubePosW);
        Vector3 cubeLocalWarped = LinearWarpLocal(cubeLocal);
        Vector3 cubePosWarpW = LocalToWorldForWarp(cubeLocalWarped);

        cubeWarped.position = cubePosWarpW;
        cubeWarped.rotation = cubeRotW;
    }

    // ============================================================
    // ★追加：任意点の写像（F/G/Blend を点に適用）
    // ============================================================
    Vector3 MapPointWorld(Vector3 pointW)
    {
        // F
        Vector3 pLocal = WorldToLocalForWarp(pointW);
        Vector3 pLocalWarped = LinearWarpLocal(pLocal);
        Vector3 pFW = LocalToWorldForWarp(pLocalWarped);

        // Cubeが無いならFのみ
        if (!TryGetCubeRealPose(out Vector3 cubePosW, out Quaternion cubeRotW))
            return pFW;

        Vector3 ratio = _committedRatio;

        // G
        Vector3 deltaRealW = pointW - cubePosW;
        Vector3 deltaRealLocal = Quaternion.Inverse(cubeRotW) * deltaRealW;

        Vector3 deltaWarpLocal = ApplyPiecewiseClampDeform(deltaRealLocal, ratio);
        Vector3 pGW = cubeWarped.position + (cubeWarped.rotation * deltaWarpLocal);

        // Blend
        float d = ComputeBlendDistance(deltaRealLocal, ratio);
        float beta = ComputeObjectBlend(d);

        return Vector3.Lerp(pFW, pGW, beta);
    }

    // ----------------------------
    // Main hand update
    // ----------------------------
    void UpdateHand(Transform original, Transform redirector, Transform indexTipPoint)
    {
        // 指先点が未設定なら、従来通り「手首点のみ」を写像
        if (indexTipPoint == null)
        {
            Vector3 hMapped = MapPointWorld(original.position);
            redirector.position = hMapped;
            redirector.rotation = original.rotation;
            return;
        }

        // 元空間の手首・指先
        Vector3 w = original.position;
        Vector3 t = indexTipPoint.position;
        Quaternion R = original.rotation;

        // 2点写像
        Vector3 wM = MapPointWorld(w);
        Vector3 tM = MapPointWorld(t);

        // 手の形を保つため：元の wrist->tip をローカルベクトルで保持
        Vector3 vLocal = Quaternion.Inverse(R) * (t - w);

        // 指先が tM に一致するように、手首（ルート）を逆算して配置
        Vector3 wPlaced = tM - (R * vLocal);

        redirector.position = wPlaced;
        redirector.rotation = R;

        // ※必要なら wM と wPlaced のブレンドで「手首の暴れ」を抑えられるが、
        // まずはこのまま（指先一致優先）で試すのが良い。
        // redirector.position = Vector3.Lerp(wM, wPlaced, 1.0f);
    }

    // ----------------------------
    // G: Piecewise clamp deform (continuous; slope n inside, slope 1 outside)
    // ----------------------------
    Vector3 ApplyPiecewiseClampDeform(Vector3 deltaRealLocal, Vector3 ratio)
    {
        return new Vector3(
            MapAxisPiecewise(deltaRealLocal.x, baseHalfExtents.x + sideRangeMargin, ratio.x),
            MapAxisPiecewise(deltaRealLocal.y, baseHalfExtents.y + sideRangeMargin, ratio.y),
            MapAxisPiecewise(deltaRealLocal.z, baseHalfExtents.z + sideRangeMargin, ratio.z)
        );
    }

    static float MapAxisPiecewise(float u, float halfExtent, float n)
    {
        if (halfExtent <= 1e-6f) return u;

        float a = Mathf.Abs(u);
        float s = (u >= 0f) ? 1f : -1f;

        if (a <= halfExtent)
        {
            return n * u;
        }

        return s * (n * halfExtent + (a - halfExtent));
    }

    // ----------------------------
    // Blend distance / weight
    // ----------------------------
    float ComputeBlendDistance(Vector3 deltaRealLocal, Vector3 ratio)
    {
        float ax = Mathf.Abs(deltaRealLocal.x);
        float ay = Mathf.Abs(deltaRealLocal.y);
        float az = Mathf.Abs(deltaRealLocal.z);

        if (!blendDistanceIncludeY)
            ay = 0f;

        if (!useSurfaceDistanceForBlend)
            return Mathf.Sqrt(ax * ax + ay * ay + az * az);

        float hx = (baseHalfExtents.x + sideRangeMargin) * ratio.x;
        float hy = (baseHalfExtents.y + sideRangeMargin) * ratio.y;
        float hz = (baseHalfExtents.z + sideRangeMargin) * ratio.z;

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
        float s = t * t * (3f - 2f * t); // smoothstep
        return 1f - s;
    }
}
*/

/*
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// HMD中心の単純線形ワープ(F) と、物体近傍のローカル写像(G) をブレンドするコントローラ。
/// - ピンチ（変形）中は ratio を更新せず、変形終了時のみコミット（=コミット方式）。
/// - enableRemoteFarRadius が true の場合は farRadius を外部入力（UDP knob）で更新する。
/// - 複数オブジェクト対応版：objects に任意個の対象を登録できる。
/// - 各点の写像では、全オブジェクトの G_i を beta_i で合成して適用する。
/// </summary>
public class GoGoInteractionController_NoY3 : MonoBehaviour
{
    [Serializable]
    public class WarpObjectEntry
    {
        [Header("Identity")]
        public string name = "Object";
        public bool enabled = true;

        [Header("Real pose source")]
        [Tooltip("推奨：ワールドに置いた推定結果。設定されていればこちらを優先。")]
        public Transform realWorldSource;

        [Tooltip("実物オブジェクトを表す Transform。realWorldSource が未設定のときに使用。")]
        public Transform realObject;

        [Header("Warped visual")]
        [Tooltip("VR側に表示する（変形される）オブジェクト。HMDの子にしないこと。")]
        public Transform warpedObject;

        [Header("Shape")]
        [Tooltip("実物オブジェクトの半サイズ（realObject ローカル座標系）。例: Cube(1m)なら (0.5,0.5,0.5)")]
        public Vector3 baseHalfExtents = new Vector3(0.5f, 0.5f, 0.5f);

        [NonSerialized] public Vector3 baseWarpedScale = Vector3.one;
        [NonSerialized] public bool baseScaleInitialized = false;
        [NonSerialized] public Vector3 committedRatio = Vector3.one;
        [NonSerialized] public DeformableCubeController deformCtrl;
        [NonSerialized] public Action<Vector3> deformEndHandler;
    }

    [Header("HMD / CameraCenter")]
    public Transform cameraCenter;

    [Header("Hands")]
    public Transform leftHandOriginal;
    public Transform leftHandRedirector;
    public Transform rightHandOriginal;
    public Transform rightHandRedirector;

    [Header("IndexTip Points (optional)")]
    [Tooltip("左手：指先点。未設定なら従来の手首点のみで動作。")]
    public Transform leftIndexTipPoint;

    [Tooltip("右手：指先点。未設定なら従来の手首点のみで動作。")]
    public Transform rightIndexTipPoint;

    [Header("Target Objects (multi-object)")]
    [Tooltip("対応させる対象オブジェクト群。要素数は任意。")]
    public List<WarpObjectEntry> objects = new List<WarpObjectEntry>();

    [Header("Legacy single-object fallback (objects が空のときだけ使用)")]
    [Tooltip("推奨：ワールドに置いた推定結果（cubeWorld等）。設定されていればこちらを優先。")]
    public Transform cubeRealWorldSource;

    [Tooltip("実物Cubeを表す Transform。realWorldSource が未設定のときに使用。")]
    public Transform cubeReal;

    [Tooltip("VR側に表示する（変形される）Cube。HMDの子にしないこと。")]
    public Transform cubeWarped;

    [Tooltip("実物Cubeの半サイズ（cubeRealローカル座標）。例: Cube(1m)なら (0.5,0.5,0.5)")]
    public Vector3 baseHalfExtents = new Vector3(0.5f, 0.5f, 0.5f);

    [Header("Linear warp (F)")]
    [Tooltip("単純線形ワープの強さ（例：0.2 → 1.2倍）")]
    public float linearK = 0.0f;

    [Tooltip("F が影響する軸（デフォルト: XZ。Yも必要ならON）")]
    public bool linearWarpAffectX = true;
    public bool linearWarpAffectY = false;
    public bool linearWarpAffectZ = true;

    [Header("Object-local mapping (G) / Blend")]
    [Tooltip("nearRadius/farRadius の距離dを '変形後の箱表面' からの距離で評価する（推奨）")]
    public bool useSurfaceDistanceForBlend = true;

    [Tooltip("ブレンド距離dの計算にY成分も含める（推奨：true）。falseだとXZのみ。")]
    public bool blendDistanceIncludeY = true;

    [Tooltip("d <= nearRadius でG=1, d >= farRadius でG=0（実距離[m]）")]
    public float nearRadius = 0.12f;
    public float farRadius = 0.30f;

    [Header("Remote FarRadius (UDP knob)")]
    [Tooltip("UDPで受けた knob01(0..1) に応じて farRadius を nearRadius + (0..1)*farRadiusAddMax に更新する")]
    public bool enableRemoteFarRadius = true;

    [Tooltip("シーン上の UdpKnobReceiver をここにアサイン")]
    public UdpKnobReceiver knobReceiver;

    [Tooltip("knob01(0..1) を nearRadius に足す最大量[m]")]
    public float farRadiusAddMax = 1.0f;

    [Tooltip("nearRadius < farRadius を保証するための最小差分[m]")]
    public float farEpsilon = 0.001f;

    [Header("Frame Option")]
    [Tooltip("Yaw-only（HMDのYawだけ）をローカル座標系として扱う（回頭でローカル軸がぶれにくい）")]
    public bool useYawOnlyFrame = true;

    [Tooltip("側面範囲（=内側で n 倍が効く範囲）を外側に拡張するマージン[m]。0なら元と同じ。")]
    public float sideRangeMargin = 0f;

    void Awake()
    {
        InitializeAllEntries();
        RegisterDeformCallbacks();
    }

    void OnDestroy()
    {
        UnregisterDeformCallbacks();
    }

    void LateUpdate()
    {
        if (cameraCenter == null) return;

        if (enableRemoteFarRadius && knobReceiver != null)
        {
            float add = Mathf.Clamp01(knobReceiver.knob01) * farRadiusAddMax;
            float targetFar = nearRadius + add;
            farRadius = Mathf.Max(targetFar, nearRadius + farEpsilon);
        }

        EnsureAllWarpedObjectsDetached();
        UpdateAllWarpedObjectVisuals();

        if (leftHandOriginal != null && leftHandRedirector != null)
            UpdateHand(leftHandOriginal, leftHandRedirector, leftIndexTipPoint);

        if (rightHandOriginal != null && rightHandRedirector != null)
            UpdateHand(rightHandOriginal, rightHandRedirector, rightIndexTipPoint);
    }

    // ----------------------------
    // Object entry management
    // ----------------------------
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

        float bx = Mathf.Abs(entry.baseWarpedScale.x) < 1e-6f ? 1e-6f : entry.baseWarpedScale.x;
        float by = Mathf.Abs(entry.baseWarpedScale.y) < 1e-6f ? 1e-6f : entry.baseWarpedScale.y;
        float bz = Mathf.Abs(entry.baseWarpedScale.z) < 1e-6f ? 1e-6f : entry.baseWarpedScale.z;

        entry.committedRatio = new Vector3(
            currentLocalScale.x / bx,
            currentLocalScale.y / by,
            currentLocalScale.z / bz
        );
    }

    // ----------------------------
    // Parenting safety
    // ----------------------------
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
            entry.warpedObject.SetParent(null, worldPositionStays: true);
    }

    // ----------------------------
    // Coordinate frames (HMD local)
    // ----------------------------
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
        Vector3 rel = world - cameraCenter.position;

        if (!useYawOnlyFrame)
            return Quaternion.Inverse(cameraCenter.rotation) * rel;

        float yaw = GetCameraYawDeg();
        Quaternion invYaw = Quaternion.Inverse(Quaternion.Euler(0f, yaw, 0f));
        return invYaw * rel;
    }

    Vector3 LocalToWorldForWarp(Vector3 local)
    {
        if (!useYawOnlyFrame)
            return cameraCenter.position + (cameraCenter.rotation * local);

        float yaw = GetCameraYawDeg();
        Quaternion yawQ = Quaternion.Euler(0f, yaw, 0f);
        return cameraCenter.position + (yawQ * local);
    }

    // ----------------------------
    // Linear warp (F)
    // ----------------------------
    Vector3 LinearWarpLocal(Vector3 pLocal)
    {
        float s = 1f + linearK;

        return new Vector3(
            linearWarpAffectX ? pLocal.x * s : pLocal.x,
            linearWarpAffectY ? pLocal.y * s : pLocal.y,
            linearWarpAffectZ ? pLocal.z * s : pLocal.z
        );
    }

    // ----------------------------
    // Object pose + visual update
    // ----------------------------
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

    // ----------------------------
    // 任意点の写像（F/G/Blend を点に適用）
    // 複数 object がある場合は、全 object の寄与を合成する。
    // ----------------------------
    Vector3 MapPointWorld(Vector3 pointW)
    {
        // F
        Vector3 pLocal = WorldToLocalForWarp(pointW);
        Vector3 pLocalWarped = LinearWarpLocal(pLocal);
        Vector3 pFW = LocalToWorldForWarp(pLocalWarped);

        List<WarpObjectEntry> active = EnumerateActiveEntries();

        float sumBeta = 0f;
        float remain = 1f; // Π(1 - beta_i)
        Vector3 weightedObjectTarget = Vector3.zero;
        bool hasAny = false;

        for (int i = 0; i < active.Count; i++)
        {
            if (!TryMapPointWorldByObject(pointW, active[i], out Vector3 gPointW, out float beta))
                continue;

            if (beta <= 0f) continue;

            hasAny = true;
            sumBeta += beta;
            weightedObjectTarget += beta * gPointW;
            remain *= (1f - Mathf.Clamp01(beta));
        }

        if (!hasAny || sumBeta <= 1e-6f)
            return pFW;

        weightedObjectTarget /= sumBeta;

        // 全オブジェクトの総合影響量
        float alpha = 1f - remain;

        // F と「全 object の代表 target」をブレンド
        return Vector3.Lerp(pFW, weightedObjectTarget, alpha);
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

    // ----------------------------
    // Main hand update
    // ----------------------------
    void UpdateHand(Transform original, Transform redirector, Transform indexTipPoint)
    {
        if (indexTipPoint == null)
        {
            Vector3 hMapped = MapPointWorld(original.position);
            redirector.position = hMapped;
            redirector.rotation = original.rotation;
            return;
        }

        Vector3 w = original.position;
        Vector3 t = indexTipPoint.position;
        Quaternion r = original.rotation;

        Vector3 wM = MapPointWorld(w);
        Vector3 tM = MapPointWorld(t);

        Vector3 vLocal = Quaternion.Inverse(r) * (t - w);
        Vector3 wPlaced = tM - (r * vLocal);

        redirector.position = wPlaced;
        redirector.rotation = r;

        // 必要なら手首安定化用にこちらへ変更:
        // redirector.position = Vector3.Lerp(wM, wPlaced, 1.0f);
    }

    // ----------------------------
    // G: Piecewise clamp deform
    // ----------------------------
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

        if (a <= halfExtent)
            return n * u;

        return s * (n * halfExtent + (a - halfExtent));
    }

    // ----------------------------
    // Blend distance / weight
    // ----------------------------
    float ComputeBlendDistance(Vector3 deltaRealLocal, Vector3 halfExtents, Vector3 ratio)
    {
        float ax = Mathf.Abs(deltaRealLocal.x);
        float ay = Mathf.Abs(deltaRealLocal.y);
        float az = Mathf.Abs(deltaRealLocal.z);

        if (!blendDistanceIncludeY)
            ay = 0f;

        if (!useSurfaceDistanceForBlend)
            return Mathf.Sqrt(ax * ax + ay * ay + az * az);

        float hx = (halfExtents.x + sideRangeMargin) * ratio.x;
        float hy = (halfExtents.y + sideRangeMargin) * ratio.y;
        float hz = (halfExtents.z + sideRangeMargin) * ratio.z;

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
*/

using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// HMD中心の単純線形ワープ(F) と、物体近傍のローカル写像(G) をブレンドするコントローラ。
/// - ピンチ（変形）中は ratio を更新せず、変形終了時のみコミット（=コミット方式）。
/// - enableRemoteFarRadius が true の場合は farRadius を外部入力（UDP knob）で更新する。
/// - 複数オブジェクト対応版：objects に任意個の対象を登録できる。
/// - 各点の写像では、「最も支配的な1個の object」を選んで適用する。
///   （平均化しないので、中間点に吸われにくい）
/// </summary>
public class GoGoInteractionController_NoY3 : MonoBehaviour
{
    [Serializable]
    public class WarpObjectEntry
    {
        [Header("Identity")]
        public string name = "Object";
        public bool enabled = true;

        [Header("Real pose source")]
        [Tooltip("推奨：ワールドに置いた推定結果。設定されていればこちらを優先。")]
        public Transform realWorldSource;

        [Tooltip("実物オブジェクトを表す Transform。realWorldSource が未設定のときに使用。")]
        public Transform realObject;

        [Header("Warped visual")]
        [Tooltip("VR側に表示する（変形される）オブジェクト。HMDの子にしないこと。")]
        public Transform warpedObject;

        [Header("Shape")]
        [Tooltip("実物オブジェクトの半サイズ（realObject ローカル座標系）。例: Cube(1m)なら (0.5,0.5,0.5)")]
        public Vector3 baseHalfExtents = new Vector3(0.5f, 0.5f, 0.5f);

        [NonSerialized] public Vector3 baseWarpedScale = Vector3.one;
        [NonSerialized] public bool baseScaleInitialized = false;
        [NonSerialized] public Vector3 committedRatio = Vector3.one;
        [NonSerialized] public DeformableCubeController deformCtrl;
        [NonSerialized] public Action<Vector3> deformEndHandler;
    }

    [Header("HMD / CameraCenter")]
    public Transform cameraCenter;

    [Header("Hands")]
    public Transform leftHandOriginal;
    public Transform leftHandRedirector;
    public Transform rightHandOriginal;
    public Transform rightHandRedirector;

    [Header("IndexTip Points (optional)")]
    [Tooltip("左手：指先点。未設定なら従来の手首点のみで動作。")]
    public Transform leftIndexTipPoint;

    [Tooltip("右手：指先点。未設定なら従来の手首点のみで動作。")]
    public Transform rightIndexTipPoint;

    [Header("Target Objects (multi-object)")]
    [Tooltip("対応させる対象オブジェクト群。要素数は任意。")]
    public List<WarpObjectEntry> objects = new List<WarpObjectEntry>();

    [Header("Legacy single-object fallback (objects が空のときだけ使用)")]
    [Tooltip("推奨：ワールドに置いた推定結果（cubeWorld等）。設定されていればこちらを優先。")]
    public Transform cubeRealWorldSource;

    [Tooltip("実物Cubeを表す Transform。realWorldSource が未設定のときに使用。")]
    public Transform cubeReal;

    [Tooltip("VR側に表示する（変形される）Cube。HMDの子にしないこと。")]
    public Transform cubeWarped;

    [Tooltip("実物Cubeの半サイズ（cubeRealローカル座標）。例: Cube(1m)なら (0.5,0.5,0.5)")]
    public Vector3 baseHalfExtents = new Vector3(0.5f, 0.5f, 0.5f);

    [Header("Linear warp (F)")]
    [Tooltip("単純線形ワープの強さ（例：0.2 → 1.2倍）")]
    public float linearK = 0.0f;

    [Tooltip("F が影響する軸（デフォルト: XZ。Yも必要ならON）")]
    public bool linearWarpAffectX = true;
    public bool linearWarpAffectY = false;
    public bool linearWarpAffectZ = true;

    [Header("Object-local mapping (G) / Blend")]
    [Tooltip("nearRadius/farRadius の距離dを '変形後の箱表面' からの距離で評価する（推奨）")]
    public bool useSurfaceDistanceForBlend = true;

    [Tooltip("ブレンド距離dの計算にY成分も含める（推奨：true）。falseだとXZのみ。")]
    public bool blendDistanceIncludeY = true;

    [Tooltip("d <= nearRadius でG=1, d >= farRadius でG=0（実距離[m]）")]
    public float nearRadius = 0.12f;
    public float farRadius = 0.30f;

    [Header("Remote FarRadius (UDP knob)")]
    [Tooltip("UDPで受けた knob01(0..1) に応じて farRadius を nearRadius + (0..1)*farRadiusAddMax に更新する")]
    public bool enableRemoteFarRadius = true;

    [Tooltip("シーン上の UdpKnobReceiver をここにアサイン")]
    public UdpKnobReceiver knobReceiver;

    [Tooltip("knob01(0..1) を nearRadius に足す最大量[m]")]
    public float farRadiusAddMax = 1.0f;

    [Tooltip("nearRadius < farRadius を保証するための最小差分[m]")]
    public float farEpsilon = 0.001f;

    [Header("Frame Option")]
    [Tooltip("Yaw-only（HMDのYawだけ）をローカル座標系として扱う（回頭でローカル軸がぶれにくい）")]
    public bool useYawOnlyFrame = true;

    [Tooltip("側面範囲（=内側で n 倍が効く範囲）を外側に拡張するマージン[m]。0なら元と同じ。")]
    public float sideRangeMargin = 0f;

    [Header("Multi-object selection")]
    [Tooltip("この値未満の beta は無視する")]
    public float minBetaToEngage = 0.01f;

    [Tooltip("他候補が現在候補よりこの値だけ強いときに切り替える")]
    public float switchMargin = 0.05f;

    [Tooltip("前回選択中の object を少し維持してバタつきを減らす")]
    public bool useSelectionHysteresis = true;

    private int _lastSelectedIndexLeft = -1;
    private int _lastSelectedIndexRight = -1;
    private int _lastSelectedIndexGeneric = -1;

    void Awake()
    {
        InitializeAllEntries();
        RegisterDeformCallbacks();
    }

    void OnDestroy()
    {
        UnregisterDeformCallbacks();
    }

    void LateUpdate()
    {
        if (cameraCenter == null) return;

        if (enableRemoteFarRadius && knobReceiver != null)
        {
            float add = Mathf.Clamp01(knobReceiver.knob01) * farRadiusAddMax;
            float targetFar = nearRadius + add;
            farRadius = Mathf.Max(targetFar, nearRadius + farEpsilon);
        }

        EnsureAllWarpedObjectsDetached();
        UpdateAllWarpedObjectVisuals();

        if (leftHandOriginal != null && leftHandRedirector != null)
            UpdateHand(leftHandOriginal, leftHandRedirector, leftIndexTipPoint, ref _lastSelectedIndexLeft);

        if (rightHandOriginal != null && rightHandRedirector != null)
            UpdateHand(rightHandOriginal, rightHandRedirector, rightIndexTipPoint, ref _lastSelectedIndexRight);
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

        float bx = Mathf.Abs(entry.baseWarpedScale.x) < 1e-6f ? 1e-6f : entry.baseWarpedScale.x;
        float by = Mathf.Abs(entry.baseWarpedScale.y) < 1e-6f ? 1e-6f : entry.baseWarpedScale.y;
        float bz = Mathf.Abs(entry.baseWarpedScale.z) < 1e-6f ? 1e-6f : entry.baseWarpedScale.z;

        entry.committedRatio = new Vector3(
            currentLocalScale.x / bx,
            currentLocalScale.y / by,
            currentLocalScale.z / bz
        );
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
            entry.warpedObject.SetParent(null, worldPositionStays: true);
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
        Vector3 rel = world - cameraCenter.position;

        if (!useYawOnlyFrame)
            return Quaternion.Inverse(cameraCenter.rotation) * rel;

        float yaw = GetCameraYawDeg();
        Quaternion invYaw = Quaternion.Inverse(Quaternion.Euler(0f, yaw, 0f));
        return invYaw * rel;
    }

    Vector3 LocalToWorldForWarp(Vector3 local)
    {
        if (!useYawOnlyFrame)
            return cameraCenter.position + (cameraCenter.rotation * local);

        float yaw = GetCameraYawDeg();
        Quaternion yawQ = Quaternion.Euler(0f, yaw, 0f);
        return cameraCenter.position + (yawQ * local);
    }

    Vector3 LinearWarpLocal(Vector3 pLocal)
    {
        float s = 1f + linearK;

        return new Vector3(
            linearWarpAffectX ? pLocal.x * s : pLocal.x,
            linearWarpAffectY ? pLocal.y * s : pLocal.y,
            linearWarpAffectZ ? pLocal.z * s : pLocal.z
        );
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

        Vector3 w = original.position;
        Vector3 t = indexTipPoint.position;
        Quaternion r = original.rotation;

        Vector3 wM = MapPointWorld(w, ref lastSelectedIndex);
        Vector3 tM = MapPointWorld(t, ref lastSelectedIndex);

        Vector3 vLocal = Quaternion.Inverse(r) * (t - w);
        Vector3 wPlaced = tM - (r * vLocal);

        redirector.position = wPlaced;
        redirector.rotation = r;
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

        if (a <= halfExtent)
            return n * u;

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

        float hx = (halfExtents.x + sideRangeMargin) * ratio.x;
        float hy = (halfExtents.y + sideRangeMargin) * ratio.y;
        float hz = (halfExtents.z + sideRangeMargin) * ratio.z;

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