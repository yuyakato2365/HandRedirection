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
