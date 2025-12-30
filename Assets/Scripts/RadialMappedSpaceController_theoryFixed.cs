/*
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
public class GoGoInteractionController_NoY2 : MonoBehaviour
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
    [Tooltip("拡張開始距離 D（m）。d<=D では恒等写像")]
    public float threshold = 0.04f;

    [Tooltip("非線形係数 k。Go-Goは d>D で  d' = d + k*(d-D)^p  として伸長する（kは単位を持つ）")]
    public float alpha = 4.0f;

    [Tooltip("指数 p（2推奨）。p>1 なら D で滑らかに始まる")]
    public float exponent = 2.0f;

    [Tooltip("d' >= d を保証する（伸長のみ）。ON推奨")]
    public bool clampToAtLeastIdentity = true;

    [Header("Object-aware blend radii")]
    [Tooltip("この距離以内は完全に物体ローカル恒等（=接触整合を優先）")]
    public float nearRadius = 0.2f;

    [Tooltip("この距離以遠は完全に世界ワープのみ（=Go-Goを優先）")]
    public float farRadius = 0.25f;

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
            // 手と物体の距離 d（水平距離で判定：Go-Goと同じ基準に揃える）
            Vector3 delta = hRealLocal - oRealLocal;
            delta.y = 0f;
            float d = delta.magnitude;

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

        // --- Go-Goの基本式 ---
        // 近傍（d<=D）は恒等写像: d' = d
        // 遠方（d>D）は伸長:       d' = d + k*(d-D)^p
        // ※従来の "D + k*(d-D)^p" は多くの範囲で d'<d になり得て、
        //   「伸長」ではなく「圧縮」に見える（＝あなたが見ている不自然さの主因）
        float scaledDistance;
        if (d <= threshold)
        {
            scaledDistance = d;
        }
        else
        {
            float extra = alpha * Mathf.Pow(d - threshold, exponent);
            scaledDistance = d + extra;

            // 伸長-only を保証したい場合
            if (clampToAtLeastIdentity)
            {
                scaledDistance = Mathf.Max(scaledDistance, d);
            }
        }

        Vector3 warpedXZ = dirXZ.normalized * scaledDistance;
        return new Vector3(warpedXZ.x, localPos.y, warpedXZ.z);
    }

    // ----------------------------------------------------------------------
    // 物体近傍でのブレンド係数 α(d)（smoothstep）
    // ----------------------------------------------------------------------
    /*
    float ComputeObjectBlend(float d)
    {
        if (d <= nearRadius) return 1f;   // 完全に物体ローカル恒等
        if (d >= farRadius) return 0f;    // 完全に世界ワープのみ

        float t = (d - nearRadius) / (farRadius - nearRadius);
        // smoothstep(0,1,t) の 1 - smoothstep 版（近いほどα=1, 遠いほどα=0）
        float s = t * t * (3f - 2f * t); // 0→1
        return 1f - s;
    }
    */
/*
    float ComputeObjectBlend(float d)
    {
        float u = Mathf.InverseLerp(nearRadius, farRadius, d); // near=0, far=1
        u = Mathf.Clamp01(u);

        float p = 3f; // ←ここを触るだけ（例: 1=線形, 2〜4=近くを強める）
        float alpha = Mathf.Pow(1f - u, p); // nearで1, farで0

        return alpha;
    }

}
*/


/*
using UnityEngine;

/// <summary>
/// HMD中心のラジアル“単純線形”ワープ + 物体近傍でのローカル恒等補正付きハンドリダイレクション。
///
/// 【線形ワープ】
/// 水平距離 d を常に α 倍する:
///   d' = α d
/// α=1で恒等、α>1で伸長。
///
/// 【重要】
/// 従来の cameraCenter.InverseTransformPoint() 方式だと、HMDピッチ/ロールで水平XZの定義が傾く問題が出るため、
/// 既定ではYaw-onlyフレームで水平XZを定義する。
/// </summary>
public class GoGoInteractionController_NoY2 : MonoBehaviour
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

    [Header("Warp parameters (Simple Linear)")]
    [Tooltip("線形スケール係数 α。d' = α d。 α=1で恒等、α>1で伸長。")]
    public float alpha = 1.5f;

    [Header("Object-aware blend radii")]
    [Tooltip("この距離以内は完全に物体ローカル恒等（=接触整合を優先）")]
    public float nearRadius = 0.2f;

    [Tooltip("この距離以遠は完全に世界ワープのみ（=Warpを優先）")]
    public float farRadius = 0.25f;

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
        Vector3 hBaseLocal = LinearWarpLocal(hRealLocal);

        Vector3 hFinalLocal = hBaseLocal;

        // 実物体情報があれば Object-aware 補正
        if (cubeReal != null)
        {
            Vector3 oRealLocal = WorldToLocalForWarp(cubeReal.position);

            // 手と物体の距離 d（水平距離で判定：Warpと同じ基準に揃える）
            Vector3 delta = hRealLocal - oRealLocal;
            delta.y = 0f;
            float d = delta.magnitude;

            float objAlpha = ComputeObjectBlend(d); // 0～1

            if (objAlpha > 0f)
            {
                // 物体にも F を適用した位置 O_vr
                Vector3 oWarpedLocal = LinearWarpLocal(oRealLocal);

                // 物体ローカルで恒等になる手位置：H_obj = O_vr + (H_real - O_real)
                Vector3 hObjLocal = oWarpedLocal + (hRealLocal - oRealLocal);

                // 世界ワープ F(h_real) と 物体ローカル恒等 hObj をブレンド
                hFinalLocal = Vector3.Lerp(hBaseLocal, hObjLocal, objAlpha);
            }
        }

        // ローカル → ワールドへ戻す
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
        Vector3 oWarpedLocal = LinearWarpLocal(oRealLocal);

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

    Vector3 WorldToLocalForWarp(Vector3 worldPos)
    {
        if (cameraCenter == null) return worldPos;

        if (!useYawOnlyFrame)
        {
            return cameraCenter.InverseTransformPoint(worldPos);
        }

        Quaternion yawRot = GetYawOnlyRotation();
        return Quaternion.Inverse(yawRot) * (worldPos - cameraCenter.position);
    }

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
    // Simple linear radial warp F (in "warp-local" space)
    // ----------------------------------------------------------------------
    /// <summary>
    /// 単純線形：水平XZ成分の距離を常に α 倍する（Yは保持）
    ///   (x, z) -> α(x, z)
    /// </summary>
    Vector3 LinearWarpLocal(Vector3 localPos)
    {
        Vector3 xz = new Vector3(localPos.x, 0f, localPos.z) * alpha;
        return new Vector3(xz.x, localPos.y, xz.z);

        // もし「3Dの完全スケール（Yも含めて）」にしたいなら下の1行に置き換え:
        // return localPos * alpha;
    }

    // ----------------------------------------------------------------------
    // 物体近傍でのブレンド係数 α_obj(d)
    // ----------------------------------------------------------------------
    float ComputeObjectBlend(float d)
    {
        float u = Mathf.InverseLerp(nearRadius, farRadius, d); // near=0, far=1
        u = Mathf.Clamp01(u);

        float p = 3f; // 1=線形, 2〜4=近くを強める
        float a = Mathf.Pow(1f - u, p); // nearで1, farで0

        return a;
    }
}
*/

using UnityEngine;

/// <summary>
/// HMD中心のラジアル“単純線形”ワープ + 物体近傍でのローカル恒等補正付きハンドリダイレクション。
///
/// 重要（今回の修正）:
/// - cubeReal に HMD子（cubeRelativeToHmd 等）を刺すと、トラッキング途切れ時に頭追従が発生し得る。
/// - そこで「ラッチ（最後に有効だったワールドPose）」を保持し、
///   “頭が動いたのに cubeReal.local が更新されていない” と判定したらラッチを使う。
///
/// 推奨:
/// - 可能なら cubeRealWorldSource に「ワールドに置いた推定結果（cubeWorld）」を刺すのが一番確実。
/// </summary>
public class GoGoInteractionController_NoY2 : MonoBehaviour
{
    [Header("HMD / CameraCenter")]
    [Tooltip("HMD / CenterEyeAnchor など")]
    public Transform cameraCenter;

    [Header("Hands (original & redirected)")]
    public Transform leftHandOriginal;
    public Transform rightHandOriginal;
    public Transform leftHandRedirector;
    public Transform rightHandRedirector;

    [Header("Object-aware Warp (Real Cube)")]
    [Tooltip("【推奨】ワールドに置かれた実物体推定（cubeWorld等）。これが設定されていればこちらを優先。")]
    public Transform cubeRealWorldSource;

    [Tooltip("互換用：HMD子の cubeRelativeToHmd 等を刺す場合はこちら。途切れ時はラッチで頭追従を防ぐ。")]
    public Transform cubeReal;

    [Tooltip("ワープ後の見かけ上のキューブ（※HMDの子にしない）")]
    public Transform cubeWarped;

    [Header("Warp parameters (Simple Linear)")]
    [Tooltip("線形スケール係数 α。d' = α d。 α=1で恒等、α>1で伸長。")]
    public float alpha = 1.5f;

    [Header("Object-aware blend radii")]
    public float nearRadius = 0.2f;
    public float farRadius = 0.25f;

    [Header("Frame Option")]
    [Tooltip("Yaw-only（ピッチ/ロール除去）で水平XZを定義する。")]
    public bool useYawOnlyFrame = true;

    [Header("Anti head-follow (latch)")]
    [Tooltip("頭がこれ以上動いたのに cubeReal.local が変化しないなら『途切れ』とみなす閾値（m）")]
    public float cameraMoveEps = 0.0025f;

    [Tooltip("頭がこれ以上回転したのに cubeReal.local が変化しないなら『途切れ』とみなす閾値（deg）")]
    public float cameraYawEpsDeg = 0.25f;

    [Tooltip("cubeReal.localPosition の更新判定閾値（m）")]
    public float cubeLocalMoveEps = 0.0005f;

    [Tooltip("cubeReal.localRotation の更新判定閾値（deg）")]
    public float cubeLocalRotEpsDeg = 0.10f;

    // ---- latch state ----
    bool _hasLatched;
    Vector3 _latchedPosW;
    Quaternion _latchedRotW;

    // prev for "stale" detection (HMD-relative input only)
    Vector3 _prevCamPos;
    float _prevCamYaw;
    Vector3 _prevCubeLocalPos;
    Quaternion _prevCubeLocalRot;
    bool _hasPrev;

    void Start()
    {
        // cubeWarped が HMD配下に置かれていたら初回に切る
        EnsureCubeWarpedDetached();
    }

    void Update()
    {
        if (cameraCenter == null) return;

        // 実行中に誰かが親子を戻しても頭追従しないように保険
        EnsureCubeWarpedDetached();

        // 手の写像（Object-aware）
        UpdateHandWithObjectAwareWarp(leftHandOriginal, leftHandRedirector);
        UpdateHandWithObjectAwareWarp(rightHandOriginal, rightHandRedirector);

        // 物体（キューブ）の写像
        UpdateCube();
    }

    // ----------------------------------------------------------------------
    // Hand: H_real -> F(H_real) を基本に、物体近傍では恒等補正へブレンド
    // ----------------------------------------------------------------------
    void UpdateHandWithObjectAwareWarp(Transform original, Transform redirector)
    {
        if (original == null || redirector == null) return;

        Vector3 hRealLocal = WorldToLocalForWarp(original.position);
        Vector3 hBaseLocal = LinearWarpLocal(hRealLocal);
        Vector3 hFinalLocal = hBaseLocal;

        if (TryGetCubeRealWorldPose(out Vector3 oWorld, out Quaternion oRot))
        {
            Vector3 oRealLocal = WorldToLocalForWarp(oWorld);

            Vector3 delta = hRealLocal - oRealLocal;
            delta.y = 0f;
            float d = delta.magnitude;

            float objAlpha = ComputeObjectBlend(d);
            if (objAlpha > 0f)
            {
                Vector3 oWarpedLocal = LinearWarpLocal(oRealLocal);
                Vector3 hObjLocal = oWarpedLocal + (hRealLocal - oRealLocal);
                hFinalLocal = Vector3.Lerp(hBaseLocal, hObjLocal, objAlpha);
            }
        }

        redirector.position = LocalForWarpToWorld(hFinalLocal);
        redirector.rotation = original.rotation;
    }

    // ----------------------------------------------------------------------
    // Cube: O_real -> F(O_real)
    // ----------------------------------------------------------------------
    void UpdateCube()
    {
        if (cubeWarped == null || cameraCenter == null) return;

        if (!TryGetCubeRealWorldPose(out Vector3 oWorld, out Quaternion oRot))
            return;

        Vector3 oRealLocal = WorldToLocalForWarp(oWorld);
        Vector3 oWarpedLocal = LinearWarpLocal(oRealLocal);

        cubeWarped.position = LocalForWarpToWorld(oWarpedLocal);
        cubeWarped.rotation = oRot;
    }

    // ----------------------------------------------------------------------
    // 重要：Cubeの「実ワールドPose」を取得（頭追従を防ぐ）
    // ----------------------------------------------------------------------
    bool TryGetCubeRealWorldPose(out Vector3 posW, out Quaternion rotW)
    {
        posW = Vector3.zero;
        rotW = Quaternion.identity;

        // ① 最強：ワールドソースがあるならそれを使う（推奨）
        if (cubeRealWorldSource != null)
        {
            posW = cubeRealWorldSource.position;
            rotW = cubeRealWorldSource.rotation;

            _latchedPosW = posW;
            _latchedRotW = rotW;
            _hasLatched = true;
            return true;
        }

        // ② 互換：HMD子の cubeReal を読む場合はラッチ＋途切れ検知
        if (cubeReal == null) return _hasLatched ? ReturnLatched(out posW, out rotW) : false;

        // 初回初期化
        if (!_hasPrev)
        {
            _prevCamPos = cameraCenter.position;
            _prevCamYaw = GetYawOnlyRotation().eulerAngles.y;
            _prevCubeLocalPos = cubeReal.localPosition;
            _prevCubeLocalRot = cubeReal.localRotation;
            _hasPrev = true;

            _latchedPosW = cubeReal.position;
            _latchedRotW = cubeReal.rotation;
            _hasLatched = true;

            posW = _latchedPosW;
            rotW = _latchedRotW;
            return true;
        }

        // 「頭が動いたか？」判定（Yawと位置）
        Vector3 camPos = cameraCenter.position;
        float camYaw = GetYawOnlyRotation().eulerAngles.y;

        float camMove = Vector3.Distance(camPos, _prevCamPos);
        float camYawDelta = Mathf.Abs(Mathf.DeltaAngle(camYaw, _prevCamYaw));
        bool cameraMoved = (camMove > cameraMoveEps) || (camYawDelta > cameraYawEpsDeg);

        // 「cubeReal.local が更新されたか？」判定
        Vector3 cubeLPos = cubeReal.localPosition;
        Quaternion cubeLRot = cubeReal.localRotation;

        float cubeLocalMove = Vector3.Distance(cubeLPos, _prevCubeLocalPos);
        float cubeLocalRotDelta = Quaternion.Angle(cubeLRot, _prevCubeLocalRot);
        bool cubeLocalUpdated = (cubeLocalMove > cubeLocalMoveEps) || (cubeLocalRotDelta > cubeLocalRotEpsDeg);

        // 途切れ推定：頭が動いたのに cubeReal.local が動いてない
        bool stale = cameraMoved && !cubeLocalUpdated;

        if (!stale)
        {
            // “生きてる” とみなしてワールドPoseをラッチ更新
            _latchedPosW = cubeReal.position;
            _latchedRotW = cubeReal.rotation;
            _hasLatched = true;
        }

        // prev 更新
        _prevCamPos = camPos;
        _prevCamYaw = camYaw;
        _prevCubeLocalPos = cubeLPos;
        _prevCubeLocalRot = cubeLRot;

        // 出力は常にラッチ（stale時に頭追従しない）
        return ReturnLatched(out posW, out rotW);
    }

    bool ReturnLatched(out Vector3 posW, out Quaternion rotW)
    {
        posW = _latchedPosW;
        rotW = _latchedRotW;
        return _hasLatched;
    }

    // cubeWarped が HMD配下なら切る（親子で頭追従になるのを防止）
    void EnsureCubeWarpedDetached()
    {
        if (cubeWarped == null || cameraCenter == null) return;

        if (cubeWarped.IsChildOf(cameraCenter))
        {
            cubeWarped.SetParent(null, true);
        }
    }

    // ----------------------------------------------------------------------
    // Yaw-only frame transforms
    // ----------------------------------------------------------------------
    Quaternion GetYawOnlyRotation()
    {
        Vector3 e = cameraCenter.rotation.eulerAngles;
        return Quaternion.Euler(0f, e.y, 0f);
    }

    Vector3 WorldToLocalForWarp(Vector3 worldPos)
    {
        if (!useYawOnlyFrame)
            return cameraCenter.InverseTransformPoint(worldPos);

        Quaternion yawRot = GetYawOnlyRotation();
        return Quaternion.Inverse(yawRot) * (worldPos - cameraCenter.position);
    }

    Vector3 LocalForWarpToWorld(Vector3 localPos)
    {
        if (!useYawOnlyFrame)
            return cameraCenter.TransformPoint(localPos);

        Quaternion yawRot = GetYawOnlyRotation();
        return cameraCenter.position + (yawRot * localPos);
    }

    // ----------------------------------------------------------------------
    // Simple linear radial warp
    // ----------------------------------------------------------------------
    Vector3 LinearWarpLocal(Vector3 localPos)
    {
        Vector3 xz = new Vector3(localPos.x, 0f, localPos.z) * alpha;
        return new Vector3(xz.x, localPos.y, xz.z);
    }

    float ComputeObjectBlend(float d)
    {
        float u = Mathf.InverseLerp(nearRadius, farRadius, d);
        u = Mathf.Clamp01(u);

        float p = 3f;
        return Mathf.Pow(1f - u, p);
    }
}
