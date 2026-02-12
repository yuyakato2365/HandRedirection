/*
using System;
using UnityEngine;

/// <summary>
/// HMD中心の単純線形ワープ(F) と、物体近傍のローカル写像(G) をブレンドするコントローラ。
///
/// ★更新（ユーザー指示）
/// 「側面範囲外は恒等（1倍）」ではなく、側面範囲内だけ n 倍し、範囲外は
///     u' = sign(u) * (n * h) + sign(u) * (|u| - h)
/// のように “境界で連続＆外側は傾き1” になる写像にする。
/// （= 範囲外は「恒等」ではなく「平行移動＋傾き1」）
/// X/Y/Z 全軸で同様に適用。
///
/// - ピンチ（変形）中は ratio を更新せず、変形終了時のみコミット（=コミット方式）。
/// - nearRadius / farRadius は実距離[m]として固定（スクリプト側で変更しない）。
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
    public float farRadius  = 0.30f;

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

        EnsureCubeWarpedDetached();

        UpdateCubeWarpedVisual();

        if (leftHandOriginal != null && leftHandRedirector != null)
            UpdateHand(leftHandOriginal, leftHandRedirector);

        if (rightHandOriginal != null && rightHandRedirector != null)
            UpdateHand(rightHandOriginal, rightHandRedirector);
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

    // ----------------------------
    // Main hand update
    // ----------------------------
    void UpdateHand(Transform original, Transform redirector)
    {
        // F
        Vector3 hLocal = WorldToLocalForWarp(original.position);
        Vector3 hLocalWarped = LinearWarpLocal(hLocal);
        Vector3 hWarpW = LocalToWorldForWarp(hLocalWarped);

        if (!TryGetCubeRealPose(out Vector3 cubePosW, out Quaternion cubeRotW))
        {
            redirector.position = hWarpW;
            redirector.rotation = original.rotation;
            return;
        }

        Vector3 ratio = _committedRatio;

        // delta in cubeReal local frame (rotation only)
        Vector3 deltaRealW = original.position - cubePosW;
        Vector3 deltaRealLocal = Quaternion.Inverse(cubeRotW) * deltaRealW;

        // G (★ここが今回の変更点)
        Vector3 deltaWarpLocal = ApplyPiecewiseClampDeform(deltaRealLocal, ratio);

        Vector3 hObjW = cubeWarped.position + (cubeWarped.rotation * deltaWarpLocal);

        // Blend beta(d)
        float d = ComputeBlendDistance(deltaRealLocal, ratio);
        float beta = ComputeObjectBlend(d);

        redirector.position = Vector3.Lerp(hWarpW, hObjW, beta);
        redirector.rotation = original.rotation;
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

    /// <summary>
    /// 1軸の写像：
    ///   if |u| <= h:      u' = n*u
    ///   else:             u' = sign(u) * (n*h) + sign(u) * (|u|-h)
    ///                  = u + sign(u)*(n-1)*h
    /// つまり境界で連続、外側の傾きは 1。
    /// </summary>
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

        // surface distance to deformed box: halfExtents' = baseHalfExtents * ratio
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
using UnityEngine;

/// <summary>
/// HMD中心の単純線形ワープ(F) と、物体近傍のローカル写像(G) をブレンドするコントローラ。
///
/// ★更新（ユーザー指示）
/// 「側面範囲外は恒等（1倍）」ではなく、側面範囲内だけ n 倍し、範囲外は
///     u' = sign(u) * (n * h) + sign(u) * (|u| - h)
/// のように “境界で連続＆外側は傾き1” になる写像にする。
/// （= 範囲外は「恒等」ではなく「平行移動＋傾き1」）
/// X/Y/Z 全軸で同様に適用。
///
/// - ピンチ（変形）中は ratio を更新せず、変形終了時のみコミット（=コミット方式）。
/// - nearRadius / farRadius は実距離[m]として固定（通常はInspectorで設定）。
///   ※ただし、enableRemoteFarRadius が true の場合は farRadius を外部入力（UDP knob）で更新する。
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

        // ★追加：farRadius を外部入力(UDP)で更新
        if (enableRemoteFarRadius && knobReceiver != null)
        {
            float add = Mathf.Clamp01(knobReceiver.knob01) * farRadiusAddMax; // 0..farRadiusAddMax
            float targetFar = nearRadius + add;                               // near + 0..+1 (既定)
            farRadius = Mathf.Max(targetFar, nearRadius + farEpsilon);        // 安全のため near < far を保証
        }

        EnsureCubeWarpedDetached();

        UpdateCubeWarpedVisual();

        if (leftHandOriginal != null && leftHandRedirector != null)
            UpdateHand(leftHandOriginal, leftHandRedirector);

        if (rightHandOriginal != null && rightHandRedirector != null)
            UpdateHand(rightHandOriginal, rightHandRedirector);
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

    // ----------------------------
    // Main hand update
    // ----------------------------
    void UpdateHand(Transform original, Transform redirector)
    {
        // F
        Vector3 hLocal = WorldToLocalForWarp(original.position);
        Vector3 hLocalWarped = LinearWarpLocal(hLocal);
        Vector3 hWarpW = LocalToWorldForWarp(hLocalWarped);

        if (!TryGetCubeRealPose(out Vector3 cubePosW, out Quaternion cubeRotW))
        {
            redirector.position = hWarpW;
            redirector.rotation = original.rotation;
            return;
        }

        Vector3 ratio = _committedRatio;

        // delta in cubeReal local frame (rotation only)
        Vector3 deltaRealW = original.position - cubePosW;
        Vector3 deltaRealLocal = Quaternion.Inverse(cubeRotW) * deltaRealW;

        // G (★ここが今回の変更点)
        Vector3 deltaWarpLocal = ApplyPiecewiseClampDeform(deltaRealLocal, ratio);

        Vector3 hObjW = cubeWarped.position + (cubeWarped.rotation * deltaWarpLocal);

        // Blend beta(d)
        float d = ComputeBlendDistance(deltaRealLocal, ratio);
        float beta = ComputeObjectBlend(d);

        redirector.position = Vector3.Lerp(hWarpW, hObjW, beta);
        redirector.rotation = original.rotation;
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

    /// <summary>
    /// 1軸の写像：
    ///   if |u| <= h:      u' = n*u
    ///   else:             u' = sign(u) * (n*h) + sign(u) * (|u| - h)
    ///                  = u + sign(u)*(n-1)*h
    /// つまり境界で連続、外側の傾きは 1。
    /// </summary>
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

        // surface distance to deformed box: halfExtents' = baseHalfExtents * ratio
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
