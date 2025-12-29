/*

using UnityEngine;

/// <summary>
/// Go-Goインタラクション（高さYを保持）
/// カメラ中心からの水平距離に応じて非線形リーチ拡張を行う
/// </summary>
public class GoGoInteractionController_NoY : MonoBehaviour
{
    [Header("参照設定")]
    public Transform cameraCenter;       // CenterEyeAnchor
    public Transform leftHandOriginal;   // LeftHandAnchor
    public Transform rightHandOriginal;  // RightHandAnchor
    public Transform leftHandRedirector; // HandRedirector_L
    public Transform rightHandRedirector;// HandRedirector_R

    [Header("Go-Goパラメータ")]
    [Tooltip("拡張開始距離（m）")]
    public float threshold = 0.4f; // 腕を少し伸ばした位置
    [Tooltip("非線形スケール係数")]
    public float alpha = 0.5f;     // 拡張度合い
    [Tooltip("非線形指数 (2〜3推奨)")]
    public float exponent = 2.0f;

    void Update()
    {
        if (cameraCenter == null) return;

        UpdateHand(leftHandOriginal, leftHandRedirector);
        UpdateHand(rightHandOriginal, rightHandRedirector);
    }

    void UpdateHand(Transform original, Transform redirector)
    {
        if (original == null || redirector == null) return;

        // --- カメラ中心から手へのベクトル ---
        Vector3 dir = original.position - cameraCenter.position;

        // 高さ（Y）を固定するため、Y成分を無視して水平距離を計算
        Vector3 dirXZ = new Vector3(dir.x, 0f, dir.z);
        float d = dirXZ.magnitude;

        // Go-Goスケーリング
        float scaledDistance;
        if (d < threshold)
        {
            scaledDistance = d;
        }
        else
        {
            float extra = Mathf.Pow((d - threshold), exponent) * alpha;
            scaledDistance = threshold + extra;
        }

        // 水平面での新しい位置を計算（Yは元の高さを維持）
        Vector3 mappedXZ = cameraCenter.position + dirXZ.normalized * scaledDistance;
        float newY = original.position.y;

        redirector.position = new Vector3(mappedXZ.x, newY, mappedXZ.z);
        redirector.rotation = original.rotation;
    }
}
*/


/*
using UnityEngine;

/// <summary>
/// HMD中心のラジアルGo-Goワープ + 物体近傍でのローカル恒等補正付きハンドリダイレクション。
/// 
/// ・cameraCenter      : HMD / CenterEyeAnchor
/// ・cubeReal          : 実物キューブのHMDローカル座標（QuestRightEyeQrTracker.cubeRelativeToHmd）
/// ・cubeWarped        : ワープ後の見かけ上のキューブ
/// ・left/rightHandOriginal  : 元の手（OVRHand / コントローラ）
/// ・left/rightHandRedirector: ワープ後の手（表示用）
/// 
/// 位置関係：
///  H_real : HMDローカルの実手位置
///  O_real : HMDローカルの実物体位置（cubeReal.localPosition）
///  F(.)   : HMD中心ラジアルGo-Go写像
/// 
///  手の最終位置：
///   H_base = F(H_real)
///   O_vr   = F(O_real)
///   H_obj  = O_vr + (H_real - O_real)  // 物体ローカルで恒等写像
///   d      = |H_real - O_real|
///   α(d)   = 1 (近い) ～ 0 (遠い) のブレンド係数
///   H_vr   = (1-α) * H_base + α * H_obj
/// </summary>
public class GoGoInteractionController_NoY : MonoBehaviour
{
    [Header("HMD / CameraCenter")]
    [Tooltip("HMD / CenterEyeAnchor など")]
    public Transform cameraCenter;

    [Header("Hands (original & redirected)")]
    [Tooltip("元の左手（OVRHand / コントローラなど）")]
    public Transform leftHandOriginal;

    [Tooltip("元の右手（OVRHand / コントローラなど）")]
    public Transform rightHandOriginal;

    [Tooltip("写像後の左手（表示用）")]
    public Transform leftHandRedirector;

    [Tooltip("写像後の右手（表示用）")]
    public Transform rightHandRedirector;

    [Header("Object-aware Warp (実物キューブ)")]
    [Tooltip("実物キューブのHMDローカル座標。QuestRightEyeQrTracker.cubeRelativeToHmd を指定する。")]
    public Transform cubeReal;   // O_real（cameraCenter を親にしておくのが前提）

    [Tooltip("写像後のキューブ（見かけ上の位置 O_vr）。表示用のCubeを割り当てる。")]
    public Transform cubeWarped; // O_vr

    [Header("Go-Goパラメータ (世界ワープ F)")]
    [Tooltip("拡張開始距離（m）")]
    public float threshold = 0.4f; // 腕を少し伸ばした位置

    [Tooltip("非線形スケール係数")]
    public float alpha = 0.5f;     // 拡張度合い

    [Tooltip("非線形指数 (2〜3推奨)")]
    public float exponent = 2.0f;

    [Header("物体近傍の補間範囲")]
    [Tooltip("この距離以下なら完全に物体ローカル恒等 (α=1)")]
    public float nearRadius = 0.05f;

    [Tooltip("この距離以上なら完全に世界ワープのみ (α=0)")]
    public float farRadius = 0.20f;

    void Update()
    {
        if (cameraCenter == null) return;

        // 手の写像（Object-aware）
        UpdateHandWithObjectAwareWarp(leftHandOriginal, leftHandRedirector);
        UpdateHandWithObjectAwareWarp(rightHandOriginal, rightHandRedirector);

        // 物体（キューブ）の写像（常にFでワープ）
        UpdateCube();
    }

    // ----------------------------------------------------------------------
    // 手の写像：世界ワープ F + 物体近傍でのローカル恒等補正
    // ----------------------------------------------------------------------

    void UpdateHandWithObjectAwareWarp(Transform original, Transform redirector)
    {
        if (original == null || redirector == null) return;

        // --- 実世界の手位置（HMDローカル） ---
        Vector3 hRealLocal = cameraCenter.InverseTransformPoint(original.position);

        // --- 世界ワープ F をかけた手位置（HMDローカル） ---
        Vector3 hBaseLocal = GoGoWarpLocal(hRealLocal);

        Vector3 hFinalLocal = hBaseLocal;

        // --- 実物体情報があれば Object-aware 補正 ---
        if (cubeReal != null)
        {
            // cubeReal が cameraCenter の子なら localPosition が O_real そのもの。
            // そうでない場合も一応 InverseTransformPoint でHMDローカルに変換しておく。
            Vector3 oRealLocal = cubeReal.parent == cameraCenter
                ? cubeReal.localPosition
                : cameraCenter.InverseTransformPoint(cubeReal.position);

            // 手と物体の距離 d（HMDローカル）
            float d = (hRealLocal - oRealLocal).magnitude;

            float objAlpha = ComputeObjectBlend(d); // 0～1

            if (objAlpha > 0f)
            {
                // 物体にも F を適用した位置 O_vr
                Vector3 oWarpedLocal = GoGoWarpLocal(oRealLocal);

                // 物体ローカルで恒等になる手位置：H_obj = O_vr + (H_real - O_real)
                Vector3 hObjLocal = oWarpedLocal + (hRealLocal - oRealLocal);

                // 世界ワープ F(h_real) と 物体ローカル恒等 hObj をブレンド
                hFinalLocal = Vector3.Lerp(hBaseLocal, hObjLocal, objAlpha);
            }
        }

        // HMDローカル → ワールド座標に戻す
        Vector3 hWorld = cameraCenter.TransformPoint(hFinalLocal);
        redirector.position = hWorld;
        redirector.rotation = original.rotation; // 回転はとりあえずそのまま
    }

    // ----------------------------------------------------------------------
    // 物体（キューブ）の写像：常に世界ワープ F のみ
    // ----------------------------------------------------------------------

    void UpdateCube()
    {
        if (cubeReal == null || cubeWarped == null || cameraCenter == null) return;

        Vector3 oRealLocal = cubeReal.parent == cameraCenter
            ? cubeReal.localPosition
            : cameraCenter.InverseTransformPoint(cubeReal.position);

        Vector3 oWarpedLocal = GoGoWarpLocal(oRealLocal);

        cubeWarped.position = cameraCenter.TransformPoint(oWarpedLocal);
        cubeWarped.rotation = cubeReal.rotation;
    }

    // ----------------------------------------------------------------------
    // HMDローカル空間での Go-Go写像 F(x)
    // ----------------------------------------------------------------------

    /// <summary>
    /// HMDローカル座標の点 localPos に対して、ラジアルGo-Go写像 F を適用したHMDローカル位置を返す。
    /// Y（高さ）は元の値を維持し、水平距離のみを非線形に伸ばす。
    /// </summary>
    Vector3 GoGoWarpLocal(Vector3 localPos)
    {
        Vector3 dir = localPos;

        // 高さ Y はそのまま、水平距離だけ見る
        Vector3 dirXZ = new Vector3(dir.x, 0f, dir.z);
        float d = dirXZ.magnitude;

        if (d <= Mathf.Epsilon)
        {
            // 原点付近はそのまま
            return localPos;
        }

        float scaledDistance;
        if (d < threshold)
        {
            scaledDistance = d;
        }
        else
        {
            float extra = Mathf.Pow((d - threshold), exponent) * alpha;
            scaledDistance = threshold + extra;
        }

        Vector3 warpedXZ = dirXZ.normalized * scaledDistance;
        float y = localPos.y;

        return new Vector3(warpedXZ.x, y, warpedXZ.z);
    }

    // ----------------------------------------------------------------------
    // 物体近傍でのブレンド係数 α(d)（smoothstep）
    // ----------------------------------------------------------------------

    float ComputeObjectBlend(float d)
    {
        if (d <= nearRadius) return 1f;   // 完全に物体ローカル恒等
        if (d >= farRadius) return 0f;    // 完全に世界ワープのみ

        float t = (d - nearRadius) / (farRadius - nearRadius);
        // smoothstep(0,1,t) の 1 - smoothstep 版（近いほどα=1, 遠いほどα=0）
        float s = t * t * (3f - 2f * t); // 0→1
        return 1f - s;
    }
}
*/


using UnityEngine;

/// <summary>
/// HMD中心のラジアルGo-Goワープ + 物体近傍でのローカル恒等補正付きハンドリダイレクション。
///
/// 【重要】
/// 従来は cameraCenter.InverseTransformPoint() で「HMDローカル」に落としてから XZ を水平と見なしていたため、
/// HMDのピッチ（上下を向く）で “XZ平面そのもの” が傾き、d=|XZ| が変わって前後に伸び縮みして見える問題が出る。
///
/// 本修正では、ピッチ/ロールを除いた “Yaw-only フレーム” を基準にして水平XZを定義し、
/// 頭の上下で距離が変に変わらないようにする。
/// </summary>
public class GoGoInteractionController_NoY : MonoBehaviour
{
    [Header("HMD / CameraCenter")]
    [Tooltip("HMD / CenterEyeAnchor など")]
    public Transform cameraCenter;

    [Header("Hands (original & redirected)")]
    [Tooltip("元の左手（OVRHand / コントローラなど）")]
    public Transform leftHandOriginal;

    [Tooltip("元の右手（OVRHand / コントローラなど）")]
    public Transform rightHandOriginal;

    [Tooltip("写像後の左手（表示用）")]
    public Transform leftHandRedirector;

    [Tooltip("写像後の右手（表示用）")]
    public Transform rightHandRedirector;

    [Header("Object-aware Warp (Real Cube)")]
    [Tooltip("実物キューブ（QuestRightEyeQrTracker の cubeRelativeToHmd を指定する想定）")]
    public Transform cubeReal;

    [Tooltip("ワープ後の見かけ上のキューブ")]
    public Transform cubeWarped;

    [Header("Go-Go parameters (Warp F)")]
    [Tooltip("拡張開始距離（m）")]
    public float threshold = 0.4f;

    [Tooltip("非線形スケール係数")]
    public float alpha = 0.5f;

    [Tooltip("非線形指数 (2〜3推奨)")]
    public float exponent = 2.0f;

    [Header("Object-aware blend radii")]
    [Tooltip("この距離以内は完全に物体ローカル恒等（=接触整合を優先）")]
    public float nearRadius = 0.05f;

    [Tooltip("この距離以遠は完全に世界ワープのみ（=Go-Goを優先）")]
    public float farRadius = 0.20f;

    [Header("Frame Option")]
    [Tooltip("Yaw-only（ピッチ/ロール除去）で水平XZを定義する。頭の上下で前後に伸び縮みする不自然さを消すためON推奨。")]
    public bool useYawOnlyFrame = true;

    void Update()
    {
        if (cameraCenter == null) return;

        // 手の写像（Object-aware）
        UpdateHandWithObjectAwareWarp(leftHandOriginal, leftHandRedirector);
        UpdateHandWithObjectAwareWarp(rightHandOriginal, rightHandRedirector);

        // 物体（キューブ）の写像（常にFでワープ）
        UpdateCube();
    }

    // ----------------------------------------------------------------------
    // Hand: H_real -> F(H_real) を基本に、物体近傍では恒等補正へブレンド
    // ----------------------------------------------------------------------
    void UpdateHandWithObjectAwareWarp(Transform original, Transform redirector)
    {
        if (original == null || redirector == null) return;

        // 実世界の手位置（ワープ用ローカル：Yaw-only推奨）
        Vector3 hRealLocal = WorldToLocalForWarp(original.position);

        // 世界ワープ F をかけた手位置（同じローカル基準）
        Vector3 hBaseLocal = GoGoWarpLocal(hRealLocal);

        Vector3 hFinalLocal = hBaseLocal;

        // 実物体情報があれば Object-aware 補正
        if (cubeReal != null)
        {
            // 実物体位置もワールドから同じローカル基準へ
            Vector3 oRealLocal = WorldToLocalForWarp(cubeReal.position);

            // 手と物体の距離 d（ワープ用ローカル空間）
            float d = (hRealLocal - oRealLocal).magnitude;

            float objAlpha = ComputeObjectBlend(d); // 0～1

            if (objAlpha > 0f)
            {
                // 物体にも F を適用した位置 O_vr
                Vector3 oWarpedLocal = GoGoWarpLocal(oRealLocal);

                // 物体ローカルで恒等になる手位置：H_obj = O_vr + (H_real - O_real)
                Vector3 hObjLocal = oWarpedLocal + (hRealLocal - oRealLocal);

                // 世界ワープ F(h_real) と 物体ローカル恒等 hObj をブレンド
                hFinalLocal = Vector3.Lerp(hBaseLocal, hObjLocal, objAlpha);
            }
        }

        // ローカル → ワールドへ戻す（Yaw-only推奨）
        Vector3 hWorld = LocalForWarpToWorld(hFinalLocal);

        redirector.position = hWorld;
        redirector.rotation = original.rotation;
    }

    // ----------------------------------------------------------------------
    // Cube: O_real -> F(O_real)
    // ----------------------------------------------------------------------
    void UpdateCube()
    {
        if (cubeReal == null || cubeWarped == null || cameraCenter == null) return;

        Vector3 oRealLocal = WorldToLocalForWarp(cubeReal.position);
        Vector3 oWarpedLocal = GoGoWarpLocal(oRealLocal);

        cubeWarped.position = LocalForWarpToWorld(oWarpedLocal);
        cubeWarped.rotation = cubeReal.rotation;
    }

    // ----------------------------------------------------------------------
    // ワープ用の座標変換（Yaw-onlyフレーム）
    // ----------------------------------------------------------------------
    Quaternion GetYawOnlyRotation()
    {
        Vector3 e = cameraCenter.rotation.eulerAngles;
        return Quaternion.Euler(0f, e.y, 0f);
    }

    /// <summary>
    /// ワールド座標 → ワープ用ローカル座標へ変換する。
    /// useYawOnlyFrame=true の場合、ピッチ/ロールを除いたYawのみの座標系でローカル化する。
    /// </summary>
    Vector3 WorldToLocalForWarp(Vector3 worldPos)
    {
        if (cameraCenter == null) return worldPos;

        if (!useYawOnlyFrame)
        {
            // 従来：HMDローカル（ピッチ/ロールも含む）→ 頭の上下でXZ平面が傾き得る
            return cameraCenter.InverseTransformPoint(worldPos);
        }

        Quaternion yawRot = GetYawOnlyRotation();
        return Quaternion.Inverse(yawRot) * (worldPos - cameraCenter.position);
    }

    /// <summary>
    /// ワープ用ローカル座標 → ワールド座標へ変換する。
    /// </summary>
    Vector3 LocalForWarpToWorld(Vector3 localPos)
    {
        if (cameraCenter == null) return localPos;

        if (!useYawOnlyFrame)
        {
            return cameraCenter.TransformPoint(localPos);
        }

        Quaternion yawRot = GetYawOnlyRotation();
        return cameraCenter.position + (yawRot * localPos);
    }

    // ----------------------------------------------------------------------
    // Go-Go warp F (in "warp-local" space)
    // ----------------------------------------------------------------------
    /// <summary>
    /// ローカル点 localPos に対して、ラジアルGo-Go写像 F を適用したローカル位置を返す。
    /// Y（高さ）は元の値を維持し、水平距離のみを非線形に伸ばす。
    /// </summary>
    Vector3 GoGoWarpLocal(Vector3 localPos)
    {
        Vector3 dir = localPos;

        // 高さ Y はそのまま、水平距離だけ見る
        Vector3 dirXZ = new Vector3(dir.x, 0f, dir.z);
        float d = dirXZ.magnitude;

        if (d <= Mathf.Epsilon)
        {
            // 原点付近はそのまま
            return localPos;
        }

        float scaledDistance;
        if (d < threshold)
        {
            scaledDistance = d;
        }
        else
        {
            float extra = Mathf.Pow((d - threshold), exponent) * alpha;
            scaledDistance = threshold + extra;
        }

        Vector3 warpedXZ = dirXZ.normalized * scaledDistance;
        return new Vector3(warpedXZ.x, localPos.y, warpedXZ.z);
    }

    // ----------------------------------------------------------------------
    // 物体近傍でのブレンド係数 α(d)（smoothstep）
    // ----------------------------------------------------------------------
    float ComputeObjectBlend(float d)
    {
        if (d <= nearRadius) return 1f;   // 完全に物体ローカル恒等
        if (d >= farRadius) return 0f;    // 完全に世界ワープのみ

        float t = (d - nearRadius) / (farRadius - nearRadius);
        // smoothstep(0,1,t) の 1 - smoothstep 版（近いほどα=1, 遠いほどα=0）
        float s = t * t * (3f - 2f * t); // 0→1
        return 1f - s;
    }
}
