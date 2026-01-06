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

/*
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
*/



/*
using UnityEngine;

/// <summary>
/// HMD中心のラジアル“単純線形”ワープ + 物体近傍でのローカル恒等補正付きハンドリダイレクション。
///
/// 今回のポイント：
/// - cubeWarped が拡大/非等方拡大されても、ブレンド判定距離 d を “変形追従距離” にして
///   nearRadius/farRadius の有効範囲が同じ形で広がるようにする。
///   （拡大後の表面付近でも objAlpha が残り、手ズレが減る）
///
/// 重要：
/// - cubeWarped は HMDの子にしない（頭追従の原因）
/// - cubeRealWorldSource が刺せるならそれが最優先（頭追従回避が最も確実）
/// </summary>
public class GoGoInteractionController_NoY2 : MonoBehaviour
{
    [Header("HMD / CameraCenter")]
    [Tooltip("HMDの中心（例：OVRCameraRig/CenterEyeAnchor など）")]
    public Transform cameraCenter;

    [Header("Hands")]
    [Tooltip("実世界左手（オリジナル）")]
    public Transform leftHandOriginal;
    [Tooltip("写像後の左手（表示用）")]
    public Transform leftHandRedirector;

    [Tooltip("実世界右手（オリジナル）")]
    public Transform rightHandOriginal;
    [Tooltip("写像後の右手（表示用）")]
    public Transform rightHandRedirector;

    [Header("Object-aware Warp (Real Cube)")]
    [Tooltip("推奨：ワールドに置いた推定結果（cubeWorld）。これが刺さっていればこちらを優先する。")]
    public Transform cubeRealWorldSource;

    [Tooltip("互換用：HMD子の cubeRelativeToHmd 等を刺す場合はこちら。途切れ時はラッチで頭追従を防ぐ。")]
    public Transform cubeReal;

    [Tooltip("ワープ後の見かけ上のキューブ（※HMDの子にしない）")]
    public Transform cubeWarped;

    [Header("Deform-aware Object Identity")]
    [Tooltip("true: 物体近傍の恒等補正＋ブレンド距離を、cubeWarped のスケール変形に追従させる（非等方スケール対応）")]
    public bool useDeformAwareObjectIdentity = true;

    [Tooltip("変形の基準スケール（起動時の cubeWarped の lossyScale を保存）。未変形が(1,1,1)なら基本そのままでOK")]
    public Vector3 baseWarpedScale = Vector3.one;

    bool _baseScaleInitialized = false;

    [Header("Warp F (Simple Linear)")]
    [Tooltip("線形ワープ係数。0なら恒等、1なら距離2倍…のように増える（※方向は保持）")]
    public float linearK = 1.0f;

    [Header("Object-aware Blend")]
    [Tooltip("この距離以内は完全に物体ローカル恒等（= 触覚整合を最優先）")]
    public float nearRadius = 0.2f;

    [Tooltip("この距離以遠は完全に世界ワープのみ（=線形ワープを優先）")]
    public float farRadius = 0.25f;

    [Header("Frame Option")]
    [Tooltip("Yaw-only（ピッチ/ロール除去）で水平XZを定義する。頭の上下で前後が伸び縮みする不自然さを消すためON推奨。")]
    public bool useYawOnlyFrame = true;

    // ---- ラッチ（途切れ対策） ----
    Vector3 _latchedPosW;
    Quaternion _latchedRotW;
    bool _latchedValid;

    // “local が更新されていない” 判定用
    Vector3 _prevCamPos;
    float _prevCamYaw;
    Vector3 _prevCubeLocalPos;
    Quaternion _prevCubeLocalRot;
    bool _hasPrev;

    void Start()
    {
        EnsureCubeWarpedDetached();

        // Deform-aware: 起動時のスケールを基準として保存（以降の比率計算に使用）
        if (cubeWarped != null && !_baseScaleInitialized)
        {
            baseWarpedScale = cubeWarped.lossyScale;
            _baseScaleInitialized = true;
        }
    }

    void Update()
    {
        if (cameraCenter == null) return;

        EnsureCubeWarpedDetached();

        UpdateHandWithObjectAwareWarp(leftHandOriginal, leftHandRedirector);
        UpdateHandWithObjectAwareWarp(rightHandOriginal, rightHandRedirector);

        UpdateCubeWarped();
    }

    // --------------------------------------------------------
    // Hand: H_real -> H_vr
    // --------------------------------------------------------
    void UpdateHandWithObjectAwareWarp(Transform original, Transform redirector)
    {
        if (original == null || redirector == null) return;

        // 実世界の手位置（warp-local）
        Vector3 hRealLocal = WorldToLocalForWarp(original.position);

        // 世界ワープFをかけた手位置
        Vector3 hBaseLocal = LinearWarpLocal(hRealLocal);

        Vector3 hFinalLocal = hBaseLocal;

        // 実物体情報があれば Object-aware 補正
        Transform cubeRealT = GetCubeRealTransformForWarp();
        if (cubeRealT != null)
        {
            Vector3 oRealLocal = WorldToLocalForWarp(cubeRealT.position);

            // ★ブレンド距離 d を「変形追従距離」にする（拡大しても範囲が広がる）
            float d;
            if (useDeformAwareObjectIdentity && cubeWarped != null)
            {
                d = ComputeDeformAwareBlendDistanceXZ(hRealLocal, oRealLocal, cubeRealT);
            }
            else
            {
                Vector3 delta = hRealLocal - oRealLocal;
                delta.y = 0f;
                d = delta.magnitude;
            }

            float objAlpha = ComputeObjectBlend(d);
            if (objAlpha > 0f)
            {
                Vector3 oWarpedLocal = LinearWarpLocal(oRealLocal);

                Vector3 hObjLocal;
                if (useDeformAwareObjectIdentity && cubeWarped != null)
                {
                    // ★変形追従：物体ローカル恒等もスケールに追従させる
                    hObjLocal = ComputeDeformAwareHandObjLocal(hRealLocal, oRealLocal, oWarpedLocal, cubeRealT);
                }
                else
                {
                    // 従来：平行移動だけの恒等補正（等形状前提）
                    hObjLocal = oWarpedLocal + (hRealLocal - oRealLocal);
                }

                hFinalLocal = Vector3.Lerp(hBaseLocal, hObjLocal, objAlpha);
            }
        }

        redirector.position = LocalForWarpToWorld(hFinalLocal);
        redirector.rotation = original.rotation;
    }

    // ----------------------------------------------------------------------
    // Deform-aware: distance / object identity (warp-local)
    // ----------------------------------------------------------------------
    Quaternion WorldToLocalRotForWarp(Quaternion worldRot)
    {
        if (cameraCenter == null) return worldRot;

        if (!useYawOnlyFrame)
        {
            return Quaternion.Inverse(cameraCenter.rotation) * worldRot;
        }

        Quaternion yawRot = GetYawOnlyRotation();
        return Quaternion.Inverse(yawRot) * worldRot;
    }

    Vector3 GetDeformRatio()
    {
        // ベーススケール未初期化の保険
        if (!_baseScaleInitialized)
        {
            if (cubeWarped != null)
            {
                baseWarpedScale = cubeWarped.lossyScale;
                _baseScaleInitialized = true;
            }
            else
            {
                baseWarpedScale = Vector3.one;
                _baseScaleInitialized = true;
            }
        }

        Vector3 current = (cubeWarped != null) ? cubeWarped.lossyScale : Vector3.one;

        Vector3 denom = new Vector3(
            Mathf.Max(1e-6f, baseWarpedScale.x),
            Mathf.Max(1e-6f, baseWarpedScale.y),
            Mathf.Max(1e-6f, baseWarpedScale.z)
        );

        return new Vector3(current.x / denom.x, current.y / denom.y, current.z / denom.z);
    }

    /// <summary>
    /// 変形追従の「ブレンド距離 d」(XZのみ)
    /// - cubeローカルでの差分 deltaLocal を作り
    /// - 変形比 ratio で割って “正規化距離” を作る（楕円体距離）
    /// → cubeWarped を拡大すると d が小さくなるので、near/far の実効範囲が拡大する
    /// </summary>
    float ComputeDeformAwareBlendDistanceXZ(Vector3 hRealLocal, Vector3 oRealLocal, Transform cubeRealT)
    {
        if (cubeRealT == null || cubeWarped == null)
        {
            Vector2 v = new Vector2(hRealLocal.x - oRealLocal.x, hRealLocal.z - oRealLocal.z);
            return v.magnitude;
        }

        Vector3 ratio = GetDeformRatio();

        Quaternion cubeRealRotLocal = WorldToLocalRotForWarp(cubeRealT.rotation);

        // cubeローカル差分
        Vector3 deltaLocal = Quaternion.Inverse(cubeRealRotLocal) * (hRealLocal - oRealLocal);

        // XZのみ（Yは距離に入れない）
        float rx = Mathf.Max(1e-6f, ratio.x);
        float rz = Mathf.Max(1e-6f, ratio.z);

        float dx = deltaLocal.x / rx;
        float dz = deltaLocal.z / rz;

        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// 変形追従の物体近傍恒等補正（warp-local）
    /// - cubeReal のローカルで手オフセットを取り
    /// - ratio（現在/基準）で非等方スケールし
    /// - cubeWarped の向きで戻して oWarpedLocal に加える
    /// </summary>
    Vector3 ComputeDeformAwareHandObjLocal(Vector3 hRealLocal, Vector3 oRealLocal, Vector3 oWarpedLocal, Transform cubeRealT)
    {
        if (cubeRealT == null || cubeWarped == null)
        {
            return oWarpedLocal + (hRealLocal - oRealLocal);
        }

        Vector3 ratio = GetDeformRatio();

        Quaternion cubeRealRotLocal = WorldToLocalRotForWarp(cubeRealT.rotation);
        Quaternion cubeWarpedRotLocal = WorldToLocalRotForWarp(cubeWarped.rotation);

        Vector3 deltaLocal = Quaternion.Inverse(cubeRealRotLocal) * (hRealLocal - oRealLocal);
        Vector3 deltaLocalScaled = Vector3.Scale(deltaLocal, ratio);

        return oWarpedLocal + (cubeWarpedRotLocal * deltaLocalScaled);
    }

    // --------------------------------------------------------
    // Cube: O_real -> O_vr (表示用)
    // --------------------------------------------------------
    void UpdateCubeWarped()
    {
        if (cubeWarped == null) return;

        Transform cubeRealT = GetCubeRealTransformForWarp();
        if (cubeRealT == null) return;

        Vector3 oRealLocal = WorldToLocalForWarp(cubeRealT.position);
        Vector3 oWarpedLocal = LinearWarpLocal(oRealLocal);

        cubeWarped.position = LocalForWarpToWorld(oWarpedLocal);
        cubeWarped.rotation = cubeRealT.rotation;
    }

    // ----------------------------------------------------------------------
    // ワープ用の座標変換（Yaw-onlyフレーム）
    // ----------------------------------------------------------------------
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
    // Linear Warp (in "warp-local" space)
    // ----------------------------------------------------------------------
    Vector3 LinearWarpLocal(Vector3 localPos)
    {
        // 水平方向だけをスケール（Yは保持）
        Vector3 pXZ = new Vector3(localPos.x, 0f, localPos.z);
        float d = pXZ.magnitude;

        if (d < 1e-6f) return localPos;

        Vector3 dir = pXZ / d;

        float d2 = d * (1f + linearK);   // 単純線形拡張
        Vector3 warpedXZ = dir * d2;

        return new Vector3(warpedXZ.x, localPos.y, warpedXZ.z);
    }

    // ----------------------------------------------------------------------
    // Utility: Yaw-only rotation
    // ----------------------------------------------------------------------
    Quaternion GetYawOnlyRotation()
    {
        Vector3 e = cameraCenter.rotation.eulerAngles;
        return Quaternion.Euler(0f, e.y, 0f);
    }

    // ----------------------------------------------------------------------
    // Cube real source selection + latch logic
    // ----------------------------------------------------------------------
    Transform GetCubeRealTransformForWarp()
    {
        // 1) 最優先：ワールドに置いた推定結果があればそれを使う
        if (cubeRealWorldSource != null)
        {
            _latchedPosW = cubeRealWorldSource.position;
            _latchedRotW = cubeRealWorldSource.rotation;
            _latchedValid = true;
            return cubeRealWorldSource;
        }

        // 2) 互換：HMD子の cubeReal を刺している場合、途切れ判定してラッチを使う
        if (cubeReal == null) return null;

        if (!_hasPrev)
        {
            _prevCamPos = cameraCenter.position;
            _prevCamYaw = GetYawOnlyRotation().eulerAngles.y;
            _prevCubeLocalPos = cubeReal.localPosition;
            _prevCubeLocalRot = cubeReal.localRotation;
            _hasPrev = true;

            _latchedPosW = cubeReal.position;
            _latchedRotW = cubeReal.rotation;
            _latchedValid = true;
            return cubeReal;
        }

        float camMoved = (cameraCenter.position - _prevCamPos).magnitude;
        float camYaw = GetYawOnlyRotation().eulerAngles.y;
        float yawDelta = Mathf.DeltaAngle(_prevCamYaw, camYaw);
        bool camChanged = (camMoved > 0.0005f) || (Mathf.Abs(yawDelta) > 0.05f);

        float cubeLocalMoved = (cubeReal.localPosition - _prevCubeLocalPos).magnitude;
        float cubeLocalRot = Quaternion.Angle(_prevCubeLocalRot, cubeReal.localRotation);
        bool cubeUpdated = (cubeLocalMoved > 0.0005f) || (cubeLocalRot > 0.05f);

        // カメラが動いたのに cubeLocal が更新されない → 途切れっぽい → ラッチ維持
        if (camChanged && !cubeUpdated && _latchedValid)
        {
            // 互換のため cubeReal のワールドPoseをラッチに固定
            cubeReal.position = _latchedPosW;
            cubeReal.rotation = _latchedRotW;
        }
        else
        {
            // 通常時：ラッチ更新
            _latchedPosW = cubeReal.position;
            _latchedRotW = cubeReal.rotation;
            _latchedValid = true;
        }

        _prevCamPos = cameraCenter.position;
        _prevCamYaw = camYaw;
        _prevCubeLocalPos = cubeReal.localPosition;
        _prevCubeLocalRot = cubeReal.localRotation;

        return cubeReal;
    }

    void EnsureCubeWarpedDetached()
    {
        if (cubeWarped == null || cameraCenter == null) return;

        Transform p = cubeWarped.parent;
        if (p != null && (p == cameraCenter || p.IsChildOf(cameraCenter)))
        {
            cubeWarped.SetParent(null, true);
        }
    }

    // ----------------------------------------------------------------------
    // Blend curve: near=1, far=0
    // ----------------------------------------------------------------------
    float ComputeObjectBlend(float d)
    {
        float u = Mathf.InverseLerp(nearRadius, farRadius, d);
        u = Mathf.Clamp01(u);

        // 近いほど1、遠いほど0（カーブは好みで）
        float p = 3f;
        return Mathf.Pow(1f - u, p);
    }
}
*/

/*
using UnityEngine;

public class GoGoInteractionController_NoY2 : MonoBehaviour
{
    [Header("HMD / CameraCenter")]
    public Transform cameraCenter;

    [Header("Hands")]
    public Transform leftHandOriginal;
    public Transform leftHandRedirector;
    public Transform rightHandOriginal;
    public Transform rightHandRedirector;

    [Header("Object-aware Warp (Real Cube)")]
    public Transform cubeRealWorldSource;
    public Transform cubeReal;
    public Transform cubeWarped;

    [Header("Deform Controller")]
    [Tooltip("DeformableCubeController（変形中かどうかを参照する）")]
    public DeformableCubeController deformController;

    [Header("Deform-aware Object Identity")]
    public bool useDeformAwareObjectIdentity = true;

    [Tooltip("未変形時の基準スケール（起動時に固定）")]
    public Vector3 baseWarpedScale = Vector3.one;

    [Header("Warp F (Simple Linear)")]
    public float linearK = 1.0f;

    [Header("Object-aware Blend")]
    public float nearRadius = 0.2f;
    public float farRadius = 0.25f;

    [Header("Frame Option")]
    public bool useYawOnlyFrame = true;

    // ---- ラッチ（途切れ対策） ----
    Vector3 _latchedPosW;
    Quaternion _latchedRotW;
    bool _latchedValid;

    Vector3 _prevCamPos;
    float _prevCamYaw;
    Vector3 _prevCubeLocalPos;
    Quaternion _prevCubeLocalRot;
    bool _hasPrev;

    // ---- Deform commit (発散防止) ----
    bool _baseScaleInitialized = false;

    // 「リダイレクションに反映済みの比率」＝変形中はこれを固定で使う
    Vector3 _committedRatio = Vector3.one;

    bool _wasDeforming = false;

    void OnEnable()
    {
        if (deformController != null)
        {
            deformController.OnDeformEnd += HandleDeformEnd;
        }
    }

    void OnDisable()
    {
        if (deformController != null)
        {
            deformController.OnDeformEnd -= HandleDeformEnd;
        }
    }

    void Start()
    {
        EnsureCubeWarpedDetached();

        // 基準スケールは起動時に固定（=「未変形」）
        if (cubeWarped != null && !_baseScaleInitialized)
        {
            baseWarpedScale = cubeWarped.localScale; // ★ localScale推奨（親スケール影響を避ける）
            _baseScaleInitialized = true;
        }

        // 初期コミット比率は 1
        _committedRatio = Vector3.one;
    }

    void Update()
    {
        if (cameraCenter == null) return;
        EnsureCubeWarpedDetached();

        // deform状態の遷移監視（イベントが取りにくい場合の保険）
        bool isDeformingNow = (deformController != null && deformController.IsDeforming);
        if (_wasDeforming && !isDeformingNow)
        {
            // イベント取りこぼし保険：解除直後にコミット
            CommitCurrentRatio();
        }
        _wasDeforming = isDeformingNow;

        UpdateHandWithObjectAwareWarp(leftHandOriginal, leftHandRedirector);
        UpdateHandWithObjectAwareWarp(rightHandOriginal, rightHandRedirector);

        UpdateCubeWarped();
    }

    // 変形終了イベント（解除後にだけリダイレクションへ反映）
    void HandleDeformEnd(Vector3 finalScale)
    {
        CommitCurrentRatio();
    }

    void CommitCurrentRatio()
    {
        if (cubeWarped == null) return;
        if (!_baseScaleInitialized)
        {
            baseWarpedScale = cubeWarped.localScale;
            _baseScaleInitialized = true;
        }

        Vector3 current = cubeWarped.localScale;
        Vector3 denom = new Vector3(
            Mathf.Max(1e-6f, baseWarpedScale.x),
            Mathf.Max(1e-6f, baseWarpedScale.y),
            Mathf.Max(1e-6f, baseWarpedScale.z)
        );

        _committedRatio = new Vector3(current.x / denom.x, current.y / denom.y, current.z / denom.z);
    }

    // --------------------------------------------------------
    // Hand: H_real -> H_vr
    // --------------------------------------------------------
    void UpdateHandWithObjectAwareWarp(Transform original, Transform redirector)
    {
        if (original == null || redirector == null) return;

        Vector3 hRealLocal = WorldToLocalForWarp(original.position);
        Vector3 hBaseLocal = LinearWarpLocal(hRealLocal);
        Vector3 hFinalLocal = hBaseLocal;

        Transform cubeRealT = GetCubeRealTransformForWarp();
        if (cubeRealT != null)
        {
            Vector3 oRealLocal = WorldToLocalForWarp(cubeRealT.position);

            // ★ここが発散対策：変形中は ratio を更新しない（コミット済みを固定使用）
            bool isDeformingNow = (deformController != null && deformController.IsDeforming);

            float d;
            if (useDeformAwareObjectIdentity && cubeWarped != null)
            {
                d = ComputeDeformAwareBlendDistanceXZ(hRealLocal, oRealLocal, cubeRealT, _committedRatio);
            }
            else
            {
                Vector3 delta = hRealLocal - oRealLocal;
                delta.y = 0f;
                d = delta.magnitude;
            }

            float objAlpha = ComputeObjectBlend(d);
            if (objAlpha > 0f)
            {
                Vector3 oWarpedLocal = LinearWarpLocal(oRealLocal);

                // 物体近傍恒等も常に「変形追従（ratio は committed 固定）」を使う
                Vector3 hObjLocal;
                if (useDeformAwareObjectIdentity && cubeWarped != null)
                {
                    hObjLocal = ComputeDeformAwareHandObjLocal(hRealLocal, oRealLocal, oWarpedLocal, cubeRealT, _committedRatio);
                }
                else
                {
                    hObjLocal = oWarpedLocal + (hRealLocal - oRealLocal);
                }

                hFinalLocal = Vector3.Lerp(hBaseLocal, hObjLocal, objAlpha);
            }
        }

        redirector.position = LocalForWarpToWorld(hFinalLocal);
        redirector.rotation = original.rotation;
    }

    // --------------------------------------------------------
    // Deform-aware helpers（committedRatio を使う）
    // --------------------------------------------------------
    Quaternion WorldToLocalRotForWarp(Quaternion worldRot)
    {
        if (cameraCenter == null) return worldRot;
        if (!useYawOnlyFrame) return Quaternion.Inverse(cameraCenter.rotation) * worldRot;

        Quaternion yawRot = GetYawOnlyRotation();
        return Quaternion.Inverse(yawRot) * worldRot;
    }

    float ComputeDeformAwareBlendDistanceXZ(Vector3 hRealLocal, Vector3 oRealLocal, Transform cubeRealT, Vector3 ratio)
    {
        Quaternion cubeRealRotLocal = WorldToLocalRotForWarp(cubeRealT.rotation);
        Vector3 deltaLocal = Quaternion.Inverse(cubeRealRotLocal) * (hRealLocal - oRealLocal);

        float rx = Mathf.Max(1e-6f, ratio.x);
        float rz = Mathf.Max(1e-6f, ratio.z);

        float dx = deltaLocal.x / rx;
        float dz = deltaLocal.z / rz;

        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    Vector3 ComputeDeformAwareHandObjLocal(Vector3 hRealLocal, Vector3 oRealLocal, Vector3 oWarpedLocal, Transform cubeRealT, Vector3 ratio)
    {
        Quaternion cubeRealRotLocal = WorldToLocalRotForWarp(cubeRealT.rotation);
        Quaternion cubeWarpedRotLocal = WorldToLocalRotForWarp(cubeWarped.rotation);

        Vector3 deltaLocal = Quaternion.Inverse(cubeRealRotLocal) * (hRealLocal - oRealLocal);
        Vector3 deltaLocalScaled = Vector3.Scale(deltaLocal, ratio);

        return oWarpedLocal + (cubeWarpedRotLocal * deltaLocalScaled);
    }

    // --------------------------------------------------------
    // Cube: O_real -> O_vr
    // --------------------------------------------------------
    void UpdateCubeWarped()
    {
        if (cubeWarped == null) return;

        Transform cubeRealT = GetCubeRealTransformForWarp();
        if (cubeRealT == null) return;

        Vector3 oRealLocal = WorldToLocalForWarp(cubeRealT.position);
        Vector3 oWarpedLocal = LinearWarpLocal(oRealLocal);

        cubeWarped.position = LocalForWarpToWorld(oWarpedLocal);
        cubeWarped.rotation = cubeRealT.rotation;
    }

    // --------------------------------------------------------
    // Warp frame transforms
    // --------------------------------------------------------
    Quaternion GetYawOnlyRotation()
    {
        Vector3 e = cameraCenter.rotation.eulerAngles;
        return Quaternion.Euler(0f, e.y, 0f);
    }

    Vector3 WorldToLocalForWarp(Vector3 worldPos)
    {
        if (cameraCenter == null) return worldPos;

        if (!useYawOnlyFrame)
            return cameraCenter.InverseTransformPoint(worldPos);

        Quaternion yawRot = GetYawOnlyRotation();
        return Quaternion.Inverse(yawRot) * (worldPos - cameraCenter.position);
    }

    Vector3 LocalForWarpToWorld(Vector3 localPos)
    {
        if (cameraCenter == null) return localPos;

        if (!useYawOnlyFrame)
            return cameraCenter.TransformPoint(localPos);

        Quaternion yawRot = GetYawOnlyRotation();
        return cameraCenter.position + (yawRot * localPos);
    }

    // --------------------------------------------------------
    // Linear Warp F
    // --------------------------------------------------------
    Vector3 LinearWarpLocal(Vector3 localPos)
    {
        Vector3 pXZ = new Vector3(localPos.x, 0f, localPos.z);
        float d = pXZ.magnitude;
        if (d < 1e-6f) return localPos;

        Vector3 dir = pXZ / d;
        float d2 = d * (1f + linearK);
        Vector3 warpedXZ = dir * d2;

        return new Vector3(warpedXZ.x, localPos.y, warpedXZ.z);
    }

    float ComputeObjectBlend(float d)
    {
        float u = Mathf.InverseLerp(nearRadius, farRadius, d);
        u = Mathf.Clamp01(u);
        float p = 3f;
        return Mathf.Pow(1f - u, p);
    }

    // --------------------------------------------------------
    // Cube real selection + latch
    // --------------------------------------------------------
    Transform GetCubeRealTransformForWarp()
    {
        if (cubeRealWorldSource != null)
        {
            _latchedPosW = cubeRealWorldSource.position;
            _latchedRotW = cubeRealWorldSource.rotation;
            _latchedValid = true;
            return cubeRealWorldSource;
        }

        if (cubeReal == null) return null;

        if (!_hasPrev)
        {
            _prevCamPos = cameraCenter.position;
            _prevCamYaw = GetYawOnlyRotation().eulerAngles.y;
            _prevCubeLocalPos = cubeReal.localPosition;
            _prevCubeLocalRot = cubeReal.localRotation;
            _hasPrev = true;

            _latchedPosW = cubeReal.position;
            _latchedRotW = cubeReal.rotation;
            _latchedValid = true;
            return cubeReal;
        }

        float camMoved = (cameraCenter.position - _prevCamPos).magnitude;
        float camYaw = GetYawOnlyRotation().eulerAngles.y;
        float yawDelta = Mathf.DeltaAngle(_prevCamYaw, camYaw);
        bool camChanged = (camMoved > 0.0005f) || (Mathf.Abs(yawDelta) > 0.05f);

        float cubeLocalMoved = (cubeReal.localPosition - _prevCubeLocalPos).magnitude;
        float cubeLocalRot = Quaternion.Angle(_prevCubeLocalRot, cubeReal.localRotation);
        bool cubeUpdated = (cubeLocalMoved > 0.0005f) || (cubeLocalRot > 0.05f);

        if (camChanged && !cubeUpdated && _latchedValid)
        {
            cubeReal.position = _latchedPosW;
            cubeReal.rotation = _latchedRotW;
        }
        else
        {
            _latchedPosW = cubeReal.position;
            _latchedRotW = cubeReal.rotation;
            _latchedValid = true;
        }

        _prevCamPos = cameraCenter.position;
        _prevCamYaw = camYaw;
        _prevCubeLocalPos = cubeReal.localPosition;
        _prevCubeLocalRot = cubeReal.localRotation;

        return cubeReal;
    }

    void EnsureCubeWarpedDetached()
    {
        if (cubeWarped == null || cameraCenter == null) return;

        Transform p = cubeWarped.parent;
        if (p != null && (p == cameraCenter || p.IsChildOf(cameraCenter)))
        {
            cubeWarped.SetParent(null, true);
        }
    }
}
*/










/*
using System;
using UnityEngine;

/// <summary>
/// HMD中心のラジアル“単純線形”ワープ + 物体近傍でのローカル恒等補正付きハンドリダイレクション。
///
/// 変更点（今回）:
/// - 物体近傍恒等写像(hObjLocal)を「変形追従の恒等写像」に統一。
///   オブジェクトが非等方スケールで変形したら、恒等写像も同じ線形変換（ratio）を受ける。
///
/// 発散防止方針:
/// - 変形中は ratio を更新しない（_committedRatio を固定）
/// -ピンチ解除後（変形終了）にだけ ratio をコミットして反映
/// </summary>
public class GoGoInteractionController_NoY2 : MonoBehaviour
{
    [Header("HMD / CameraCenter")]
    [Tooltip("HMDの中心（例：OVRCameraRig/CenterEyeAnchor など）")]
    public Transform cameraCenter;

    [Header("Hands")]
    [Tooltip("実世界左手（オリジナル）")]
    public Transform leftHandOriginal;
    [Tooltip("写像後の左手（表示用）")]
    public Transform leftHandRedirector;

    [Tooltip("実世界右手（オリジナル）")]
    public Transform rightHandOriginal;
    [Tooltip("写像後の右手（表示用）")]
    public Transform rightHandRedirector;

    [Header("Object-aware Warp (Real Cube)")]
    [Tooltip("推奨：ワールドに置いた推定結果（cubeWorld）。これが刺さっていればこちらを優先する。")]
    public Transform cubeRealWorldSource;

    [Tooltip("互換用：HMD子の cubeRelativeToHmd 等を刺す場合はこちら。途切れ時はラッチで頭追従を防ぐ。")]
    public Transform cubeReal;

    [Tooltip("ワープ後の見かけ上のキューブ（※HMDの子にしない）")]
    public Transform cubeWarped;

    [Header("Deform Controller")]
    [Tooltip("DeformableCubeController（変形中かどうかを参照する）")]
    public DeformableCubeController deformController;

    [Header("Deform-aware Identity / Blend")]
    [Tooltip("true: 物体近傍恒等補正＋ブレンド距離を、cubeWarped の変形（非等方スケール）に追従させる")]
    public bool useDeformAwareObjectIdentity = true;

    [Tooltip("未変形時の基準スケール（起動時に固定）。ratio = currentScale / baseWarpedScale")]
    public Vector3 baseWarpedScale = Vector3.one;

    [Header("Blend Distance Mode")]
    [Tooltip("true: 箱の“表面からの距離”でブレンド（変形時の体感が最も安定）。false: 中心距離ベース。")]
    public bool useSurfaceDistanceForBlend = true;

    [Tooltip("未変形時の半サイズ[m]（例：一辺1mなら(0.5,0.5,0.5)）")]
    public Vector3 baseHalfExtents = new Vector3(0.5f, 0.5f, 0.5f);

    [Header("Warp F (Simple Linear)")]
    [Tooltip("線形ワープ係数。0なら恒等、1なら距離2倍…のように増える（※方向は保持）")]
    public float linearK = 1.0f;

    [Header("Object-aware Blend")]
    [Tooltip("この距離以内は完全に物体ローカル恒等（=触覚整合を最優先）")]
    public float nearRadius = 0.2f;

    [Tooltip("この距離以遠は完全に世界ワープのみ（=線形ワープを優先）")]
    public float farRadius = 0.25f;

    [Header("Frame Option")]
    [Tooltip("Yaw-only（ピッチ/ロール除去）で水平XZを定義する。")]
    public bool useYawOnlyFrame = true;

    // ---- ラッチ（途切れ対策） ----
    Vector3 _latchedPosW;
    Quaternion _latchedRotW;
    bool _latchedValid;

    // “local が更新されていない” 判定用
    Vector3 _prevCamPos;
    float _prevCamYaw;
    Vector3 _prevCubeLocalPos;
    Quaternion _prevCubeLocalRot;
    bool _hasPrev;

    // ---- Deform commit（発散防止）----
    bool _baseScaleInitialized = false;
    Vector3 _committedRatio = Vector3.one;
    bool _wasDeforming = false;

    void OnEnable()
    {
        if (deformController != null)
            deformController.OnDeformEnd += HandleDeformEnd;
    }

    void OnDisable()
    {
        if (deformController != null)
            deformController.OnDeformEnd -= HandleDeformEnd;
    }

    void Start()
    {
        EnsureCubeWarpedDetached();

        // 基準スケールは起動時に固定（未変形）
        if (cubeWarped != null && !_baseScaleInitialized)
        {
            baseWarpedScale = cubeWarped.localScale; // ★ localScale推奨（親スケール影響を切る）
            _baseScaleInitialized = true;
        }

        _committedRatio = Vector3.one;
        _wasDeforming = (deformController != null && deformController.IsDeforming);
    }

    void Update()
    {
        if (cameraCenter == null) return;

        EnsureCubeWarpedDetached();

        // 変形状態の遷移監視（イベント取りこぼし保険）
        bool isDeformingNow = (deformController != null && deformController.IsDeforming);
        if (_wasDeforming && !isDeformingNow)
        {
            CommitCurrentRatio();
        }
        _wasDeforming = isDeformingNow;

        UpdateHandWithObjectAwareWarp(leftHandOriginal, leftHandRedirector);
        UpdateHandWithObjectAwareWarp(rightHandOriginal, rightHandRedirector);

        UpdateCubeWarped();
    }

    void HandleDeformEnd(Vector3 finalScale)
    {
        // 解除後にだけコミット
        CommitCurrentRatio();
    }

    void CommitCurrentRatio()
    {
        if (cubeWarped == null) return;

        if (!_baseScaleInitialized)
        {
            baseWarpedScale = cubeWarped.localScale;
            _baseScaleInitialized = true;
        }

        Vector3 current = cubeWarped.localScale;

        Vector3 denom = new Vector3(
            Mathf.Max(1e-6f, baseWarpedScale.x),
            Mathf.Max(1e-6f, baseWarpedScale.y),
            Mathf.Max(1e-6f, baseWarpedScale.z)
        );

        _committedRatio = new Vector3(current.x / denom.x, current.y / denom.y, current.z / denom.z);
    }

    // --------------------------------------------------------
    // Hand: H_real -> H_vr
    // --------------------------------------------------------
    void UpdateHandWithObjectAwareWarp(Transform original, Transform redirector)
    {
        if (original == null || redirector == null) return;

        // 実世界の手位置（warp-local）
        Vector3 hRealLocal = WorldToLocalForWarp(original.position);

        // 世界ワープFをかけた手位置
        Vector3 hBaseLocal = LinearWarpLocal(hRealLocal);

        Vector3 hFinalLocal = hBaseLocal;

        // 実物体情報があれば Object-aware 補正
        Transform cubeRealT = GetCubeRealTransformForWarp();
        if (cubeRealT != null)
        {
            Vector3 oRealLocal = WorldToLocalForWarp(cubeRealT.position);
            Vector3 oWarpedLocal = LinearWarpLocal(oRealLocal);

            // ★ブレンド距離 d は、変形追従（ratio は committed 固定）で計算
            float d = ComputeBlendDistance(hRealLocal, oRealLocal, cubeRealT, _committedRatio);

            float objAlpha = ComputeObjectBlend(d);
            if (objAlpha > 0f)
            {
                // ★物体近傍恒等写像を「変形追従の恒等写像」に統一（ここが今回の修正の本丸）
                Vector3 hObjLocal;
                if (useDeformAwareObjectIdentity && cubeWarped != null)
                {
                    hObjLocal = ComputeDeformedIdentityObjLocal(hRealLocal, oRealLocal, oWarpedLocal, cubeRealT, _committedRatio);
                }
                else
                {
                    // 非追従の場合（旧来）：平行移動だけの恒等
                    hObjLocal = oWarpedLocal + (hRealLocal - oRealLocal);
                }

                hFinalLocal = Vector3.Lerp(hBaseLocal, hObjLocal, objAlpha);
            }
        }

        redirector.position = LocalForWarpToWorld(hFinalLocal);
        redirector.rotation = original.rotation;
    }

    float ComputeBlendDistance(Vector3 hRealLocal, Vector3 oRealLocal, Transform cubeRealT, Vector3 ratio)
    {
        if (!useDeformAwareObjectIdentity || cubeWarped == null || cubeRealT == null)
        {
            Vector3 delta = hRealLocal - oRealLocal;
            delta.y = 0f;
            return delta.magnitude;
        }

        if (useSurfaceDistanceForBlend)
        {
            return ComputeSurfaceDistanceXZ(hRealLocal, oRealLocal, cubeRealT, ratio);
        }
        else
        {
            return ComputeCenterNormalizedDistanceXZ(hRealLocal, oRealLocal, cubeRealT, ratio);
        }
    }

    /// <summary>
    /// 中心距離ベース（楕円体正規化）：d = sqrt((dx/ratioX)^2 + (dz/ratioZ)^2)
    /// </summary>
    float ComputeCenterNormalizedDistanceXZ(Vector3 hRealLocal, Vector3 oRealLocal, Transform cubeRealT, Vector3 ratio)
    {
        Quaternion cubeRealRotLocal = WorldToLocalRotForWarp(cubeRealT.rotation);
        Vector3 deltaLocal = Quaternion.Inverse(cubeRealRotLocal) * (hRealLocal - oRealLocal);

        float rx = Mathf.Max(1e-6f, ratio.x);
        float rz = Mathf.Max(1e-6f, ratio.z);

        float dx = deltaLocal.x / rx;
        float dz = deltaLocal.z / rz;

        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// 表面距離ベース（おすすめ）：変形後の箱の表面からどれだけ外にいるか（XZ）
    /// </summary>
    float ComputeSurfaceDistanceXZ(Vector3 hRealLocal, Vector3 oRealLocal, Transform cubeRealT, Vector3 ratio)
    {
        Quaternion cubeRealRotLocal = WorldToLocalRotForWarp(cubeRealT.rotation);
        Vector3 p = Quaternion.Inverse(cubeRealRotLocal) * (hRealLocal - oRealLocal);

        Vector3 half = new Vector3(
            baseHalfExtents.x * Mathf.Max(1e-6f, ratio.x),
            baseHalfExtents.y * Mathf.Max(1e-6f, ratio.y),
            baseHalfExtents.z * Mathf.Max(1e-6f, ratio.z)
        );

        float qx = Mathf.Abs(p.x) - half.x;
        float qz = Mathf.Abs(p.z) - half.z;

        float dx = Mathf.Max(qx, 0f);
        float dz = Mathf.Max(qz, 0f);

        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// ★今回の目的：
    /// 物体近傍恒等写像を「変形追従」にする。
    /// - 実物体ローカルで相対ベクトルを取り
    /// - ratio（非等方）を掛け
    /// - 変形後（cubeWarped）の向きで戻して oWarpedLocal に加える
    /// </summary>
    Vector3 ComputeDeformedIdentityObjLocal(
        Vector3 hRealLocal,
        Vector3 oRealLocal,
        Vector3 oWarpedLocal,
        Transform cubeRealT,
        Vector3 ratio)
    {
        Quaternion cubeRealRotLocal = WorldToLocalRotForWarp(cubeRealT.rotation);
        Quaternion cubeWarpedRotLocal = WorldToLocalRotForWarp(cubeWarped.rotation);

        // 実物体ローカル相対
        Vector3 deltaLocal = Quaternion.Inverse(cubeRealRotLocal) * (hRealLocal - oRealLocal);

        // 変形追従（非等方）
        Vector3 deltaLocalDeformed = Vector3.Scale(deltaLocal, ratio);

        // 変形後の向きで戻す
        return oWarpedLocal + (cubeWarpedRotLocal * deltaLocalDeformed);
    }

    // --------------------------------------------------------
    // Cube: O_real -> O_vr (表示用)
    // --------------------------------------------------------
    void UpdateCubeWarped()
    {
        if (cubeWarped == null) return;

        Transform cubeRealT = GetCubeRealTransformForWarp();
        if (cubeRealT == null) return;

        Vector3 oRealLocal = WorldToLocalForWarp(cubeRealT.position);
        Vector3 oWarpedLocal = LinearWarpLocal(oRealLocal);

        cubeWarped.position = LocalForWarpToWorld(oWarpedLocal);
        cubeWarped.rotation = cubeRealT.rotation;
    }

    // ----------------------------------------------------------------------
    // ワープ用の座標変換（Yaw-onlyフレーム）
    // ----------------------------------------------------------------------
    Vector3 WorldToLocalForWarp(Vector3 worldPos)
    {
        if (cameraCenter == null) return worldPos;

        if (!useYawOnlyFrame)
            return cameraCenter.InverseTransformPoint(worldPos);

        Quaternion yawRot = GetYawOnlyRotation();
        return Quaternion.Inverse(yawRot) * (worldPos - cameraCenter.position);
    }

    Vector3 LocalForWarpToWorld(Vector3 localPos)
    {
        if (cameraCenter == null) return localPos;

        if (!useYawOnlyFrame)
            return cameraCenter.TransformPoint(localPos);

        Quaternion yawRot = GetYawOnlyRotation();
        return cameraCenter.position + (yawRot * localPos);
    }

    Quaternion WorldToLocalRotForWarp(Quaternion worldRot)
    {
        if (cameraCenter == null) return worldRot;

        if (!useYawOnlyFrame)
            return Quaternion.Inverse(cameraCenter.rotation) * worldRot;

        Quaternion yawRot = GetYawOnlyRotation();
        return Quaternion.Inverse(yawRot) * worldRot;
    }

    Quaternion GetYawOnlyRotation()
    {
        Vector3 e = cameraCenter.rotation.eulerAngles;
        return Quaternion.Euler(0f, e.y, 0f);
    }

    // ----------------------------------------------------------------------
    // Linear Warp (in "warp-local" space)
    // ----------------------------------------------------------------------
    Vector3 LinearWarpLocal(Vector3 localPos)
    {
        Vector3 pXZ = new Vector3(localPos.x, 0f, localPos.z);
        float d = pXZ.magnitude;

        if (d < 1e-6f) return localPos;

        Vector3 dir = pXZ / d;
        float d2 = d * (1f + linearK);
        Vector3 warpedXZ = dir * d2;

        return new Vector3(warpedXZ.x, localPos.y, warpedXZ.z);
    }

    // ----------------------------------------------------------------------
    // Blend curve: near=1, far=0
    // ----------------------------------------------------------------------
    float ComputeObjectBlend(float d)
    {
        float u = Mathf.InverseLerp(nearRadius, farRadius, d);
        u = Mathf.Clamp01(u);

        float p = 3f;                 // カーブ強さ（好みで）
        return Mathf.Pow(1f - u, p);  // 近いほど1、遠いほど0
    }

    // ----------------------------------------------------------------------
    // Cube real source selection + latch logic
    // ----------------------------------------------------------------------
    Transform GetCubeRealTransformForWarp()
    {
        // 1) 最優先：ワールドに置いた推定結果があればそれを使う
        if (cubeRealWorldSource != null)
        {
            _latchedPosW = cubeRealWorldSource.position;
            _latchedRotW = cubeRealWorldSource.rotation;
            _latchedValid = true;
            return cubeRealWorldSource;
        }

        // 2) 互換：HMD子の cubeReal を刺している場合、途切れ判定してラッチを使う
        if (cubeReal == null) return null;

        if (!_hasPrev)
        {
            _prevCamPos = cameraCenter.position;
            _prevCamYaw = GetYawOnlyRotation().eulerAngles.y;
            _prevCubeLocalPos = cubeReal.localPosition;
            _prevCubeLocalRot = cubeReal.localRotation;
            _hasPrev = true;

            _latchedPosW = cubeReal.position;
            _latchedRotW = cubeReal.rotation;
            _latchedValid = true;
            return cubeReal;
        }

        float camMoved = (cameraCenter.position - _prevCamPos).magnitude;
        float camYaw = GetYawOnlyRotation().eulerAngles.y;
        float yawDelta = Mathf.DeltaAngle(_prevCamYaw, camYaw);
        bool camChanged = (camMoved > 0.0005f) || (Mathf.Abs(yawDelta) > 0.05f);

        float cubeLocalMoved = (cubeReal.localPosition - _prevCubeLocalPos).magnitude;
        float cubeLocalRot = Quaternion.Angle(_prevCubeLocalRot, cubeReal.localRotation);
        bool cubeUpdated = (cubeLocalMoved > 0.0005f) || (cubeLocalRot > 0.05f);

        // カメラが動いたのに cubeLocal が更新されない → 途切れっぽい → ラッチ維持
        if (camChanged && !cubeUpdated && _latchedValid)
        {
            cubeReal.position = _latchedPosW;
            cubeReal.rotation = _latchedRotW;
        }
        else
        {
            _latchedPosW = cubeReal.position;
            _latchedRotW = cubeReal.rotation;
            _latchedValid = true;
        }

        _prevCamPos = cameraCenter.position;
        _prevCamYaw = camYaw;
        _prevCubeLocalPos = cubeReal.localPosition;
        _prevCubeLocalRot = cubeReal.localRotation;

        return cubeReal;
    }

    void EnsureCubeWarpedDetached()
    {
        if (cubeWarped == null || cameraCenter == null) return;

        Transform p = cubeWarped.parent;
        if (p != null && (p == cameraCenter || p.IsChildOf(cameraCenter)))
        {
            cubeWarped.SetParent(null, true);
        }
    }
}
*/








/*
using System;
using UnityEngine;

/// <summary>
/// HMD中心の単純線形ワープ（XZのみ） + 物体近傍のローカル恒等補正（XZのみ）をブレンドする。
///
/// 要件対応:
/// - ピンチ（変形）中は ratio を更新せず、写像側は変形追従しない（=コミット方式）。
/// - nearRadius / farRadius の値は実距離[m]として固定（スクリプト側で変更しない）。
/// - ブレンド距離 d は「(コミット済みratioで)変形後の箱表面からの距離」を実距離[m]で評価。
/// </summary>
public class GoGoInteractionController_NoY2 : MonoBehaviour
{
    [Header("HMD / CameraCenter")]
    [Tooltip("HMDの中心（例：OVRCameraRig/CenterEyeAnchor など）")]
    public Transform cameraCenter;

    [Header("Hands")]
    public Transform leftHandOriginal;
    public Transform leftHandRedirector;
    public Transform rightHandOriginal;
    public Transform rightHandRedirector;

    [Header("Real cube pose source")]
    [Tooltip("推奨：ワールドに置いた推定結果（cubeWorld等）。設定されていればこちらを優先。")]
    public Transform cubeRealWorldSource;

    [Tooltip("互換用：HMD子の cubeRelativeToHmd 等を刺す場合はこちら（途切れ時はラッチで頭追従を防ぐ）。")]
    public Transform cubeReal;

    [Header("Warped cube (visual)")]
    [Tooltip("ワープ後の見かけ上のキューブ（HMDの子にしない）")]
    public Transform cubeWarped;

    [Header("Deform controller")]
    public DeformableCubeController deformController;

    [Header("Warp F (Simple Linear)")]
    [Tooltip("XZの線形ワープ係数。0:恒等, 1:(XZが2倍), 2:(XZが3倍) ...")]
    public float linearK = 1.0f;

    [Header("Object-aware Blend")]
    [Tooltip("表面からこの距離以内は完全に物体ローカル恒等（接触整合優先）")]
    public float nearRadius = 0.2f;

    [Tooltip("表面からこの距離以遠は完全に世界ワープ（伸長優先）")]
    public float farRadius = 0.25f;

    [Header("Blend Distance")]
    [Tooltip("true: 箱の表面からの距離でブレンド（推奨）。false: 中心距離（XZ）でブレンド。")]
    public bool useSurfaceDistanceForBlend = true;

    [Tooltip("ratio=1 のときの箱の半サイズ[m]。例: 一辺0.1mなら (0.05,0.05,0.05)。")]
    public Vector3 baseHalfExtents = new Vector3(0.05f, 0.05f, 0.05f);

    [Header("Frame Option")]
    [Tooltip("Yaw-only（ピッチ/ロール除去）で水平XZを定義する。")]
    public bool useYawOnlyFrame = true;

    [Header("Anti head-follow (latch)")]
    public float cameraMoveEps = 0.0025f;
    public float cameraYawEpsDeg = 0.25f;
    public float cubeLocalMoveEps = 0.0005f;
    public float cubeLocalRotEpsDeg = 0.10f;

    // Latch state for cubeReal (when cubeReal is HMD-relative and tracking freezes)
    bool _latchedValid;
    Vector3 _latchedPosW;
    Quaternion _latchedRotW;

    bool _hasPrev;
    Vector3 _prevCamPos;
    float _prevCamYawDeg;
    Vector3 _prevCubeLocalPos;
    Quaternion _prevCubeLocalRot;

    // Deform commit (mapping uses committed ratio only)
    bool _baseScaleInitialized;
    public Vector3 baseWarpedScale = Vector3.one;
    Vector3 _committedRatio = Vector3.one;
    bool _wasDeforming;

    void OnEnable()
    {
        if (deformController != null)
            deformController.OnDeformEnd += HandleDeformEnd;
    }

    void OnDisable()
    {
        if (deformController != null)
            deformController.OnDeformEnd -= HandleDeformEnd;
    }

    void Start()
    {
        EnsureCubeWarpedDetached();

        if (cubeWarped != null && !_baseScaleInitialized)
        {
            baseWarpedScale = cubeWarped.localScale; // localScale基準
            _baseScaleInitialized = true;
        }

        _committedRatio = Vector3.one;
        _wasDeforming = (deformController != null && deformController.IsDeforming);
    }

    void Update()
    {
        if (cameraCenter == null) return;

        EnsureCubeWarpedDetached();

        // Safety: detect end-of-deform even if event missed
        bool isDeformingNow = (deformController != null && deformController.IsDeforming);
        if (_wasDeforming && !isDeformingNow)
        {
            CommitCurrentRatioFromCubeWarped();
        }
        _wasDeforming = isDeformingNow;

        // Update cube first (so hands can use current cubeWarped pose)
        UpdateCubeWarpedVisual();

        UpdateHandWithObjectAwareWarp(leftHandOriginal, leftHandRedirector);
        UpdateHandWithObjectAwareWarp(rightHandOriginal, rightHandRedirector);
    }

    void HandleDeformEnd(Vector3 finalLocalScale)
    {
        // Commit only when deform ends (pinch released or cancelled)
        CommitRatio(finalLocalScale);
    }

    void CommitCurrentRatioFromCubeWarped()
    {
        if (cubeWarped == null) return;
        CommitRatio(cubeWarped.localScale);
    }

    void CommitRatio(Vector3 currentLocalScale)
    {
        if (!_baseScaleInitialized) return;

        // Avoid division by zero
        float bx = Mathf.Abs(baseWarpedScale.x) < 1e-6f ? 1e-6f : baseWarpedScale.x;
        float by = Mathf.Abs(baseWarpedScale.y) < 1e-6f ? 1e-6f : baseWarpedScale.y;
        float bz = Mathf.Abs(baseWarpedScale.z) < 1e-6f ? 1e-6f : baseWarpedScale.z;

        _committedRatio = new Vector3(
            currentLocalScale.x / bx,
            currentLocalScale.y / by,
            currentLocalScale.z / bz
        );
    }

    void EnsureCubeWarpedDetached()
    {
        if (cubeWarped == null || cameraCenter == null) return;

        Transform p = cubeWarped.parent;
        if (p != null && (p == cameraCenter || p.IsChildOf(cameraCenter)))
        {
            cubeWarped.SetParent(null, true);
        }
    }

    // ----------------------------
    // Cube pose (real)
    // ----------------------------
    bool TryGetCubeRealPose(out Vector3 posW, out Quaternion rotW)
    {
        if (cubeRealWorldSource != null)
        {
            posW = cubeRealWorldSource.position;
            rotW = cubeRealWorldSource.rotation;
            return true;
        }

        if (cubeReal == null)
        {
            posW = default;
            rotW = default;
            return false;
        }

        float camYaw = GetCameraYawDeg();

        if (!_hasPrev)
        {
            _prevCamPos = cameraCenter.position;
            _prevCamYawDeg = camYaw;
            _prevCubeLocalPos = cubeReal.localPosition;
            _prevCubeLocalRot = cubeReal.localRotation;

            _latchedPosW = cubeReal.position;
            _latchedRotW = cubeReal.rotation;
            _latchedValid = true;
            _hasPrev = true;

            posW = cubeReal.position;
            rotW = cubeReal.rotation;
            return true;
        }

        float camMoved = (cameraCenter.position - _prevCamPos).magnitude;
        float yawDelta = Mathf.DeltaAngle(_prevCamYawDeg, camYaw);

        float cubeLocalMoved = (cubeReal.localPosition - _prevCubeLocalPos).magnitude;
        float cubeLocalRot = Quaternion.Angle(_prevCubeLocalRot, cubeReal.localRotation);

        bool camChanged = (camMoved > cameraMoveEps) || (Mathf.Abs(yawDelta) > cameraYawEpsDeg);
        bool cubeUpdated = (cubeLocalMoved > cubeLocalMoveEps) || (cubeLocalRot > cubeLocalRotEpsDeg);

        if (camChanged && !cubeUpdated && _latchedValid)
        {
            // Tracking frozen -> use latched world pose
            posW = _latchedPosW;
            rotW = _latchedRotW;
        }
        else
        {
            // Tracking updated -> refresh latch
            _latchedPosW = cubeReal.position;
            _latchedRotW = cubeReal.rotation;
            _latchedValid = true;

            posW = cubeReal.position;
            rotW = cubeReal.rotation;
        }

        _prevCamPos = cameraCenter.position;
        _prevCamYawDeg = camYaw;
        _prevCubeLocalPos = cubeReal.localPosition;
        _prevCubeLocalRot = cubeReal.localRotation;

        return true;
    }

    float GetCameraYawDeg()
    {
        Vector3 f = cameraCenter.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 1e-8f) return cameraCenter.eulerAngles.y;
        f.Normalize();
        return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
    }

    // ----------------------------
    // Warp frame conversions
    // ----------------------------
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

    Vector3 LinearWarpLocalXZ(Vector3 pLocal)
    {
        float s = 1f + linearK;
        return new Vector3(pLocal.x * s, pLocal.y, pLocal.z * s);
    }

    // ----------------------------
    // Cube visual update
    // ----------------------------
    void UpdateCubeWarpedVisual()
    {
        if (cubeWarped == null) return;

        if (!TryGetCubeRealPose(out Vector3 cubePosW, out Quaternion cubeRotW))
            return;

        Vector3 cubeLocal = WorldToLocalForWarp(cubePosW);
        Vector3 cubeWarpLocal = LinearWarpLocalXZ(cubeLocal);

        cubeWarped.position = LocalToWorldForWarp(cubeWarpLocal);
        cubeWarped.rotation = cubeRotW;
    }

    // ----------------------------
    // Hand mapping
    // ----------------------------
    void UpdateHandWithObjectAwareWarp(Transform original, Transform redirector)
    {
        if (original == null || redirector == null) return;

        // World warp (F)
        Vector3 hLocal = WorldToLocalForWarp(original.position);
        Vector3 hWarpLocal = LinearWarpLocalXZ(hLocal);
        Vector3 hWarpW = LocalToWorldForWarp(hWarpLocal);

        // If no cube, fallback to warp only
        if (cubeWarped == null) { redirector.position = hWarpW; redirector.rotation = original.rotation; return; }
        if (!TryGetCubeRealPose(out Vector3 cubePosW, out Quaternion cubeRotW))
        {
            redirector.position = hWarpW;
            redirector.rotation = original.rotation;
            return;
        }

        // IMPORTANT: mapping uses committed ratio only (no change during pinch)
        Vector3 ratio = _committedRatio;

        // Object-local "identity" (G) (XZ only)
        Vector3 deltaRealW = original.position - cubePosW;
        Vector3 deltaRealLocal = Quaternion.Inverse(cubeRotW) * deltaRealW;

        Vector3 deltaWarpLocal = new Vector3(
            deltaRealLocal.x * ratio.x,
            deltaRealLocal.y * ratio.x,             
            deltaRealLocal.z * ratio.z
        );

        Vector3 hObjW = cubeWarped.position + (cubeWarped.rotation * deltaWarpLocal);

        // Blend weight beta(d)
        float d = ComputeBlendDistanceXZ(deltaRealLocal, ratio);
        float beta = ComputeObjectBlend(d);

        redirector.position = Vector3.Lerp(hWarpW, hObjW, beta);
        redirector.rotation = original.rotation;
    }

    float ComputeBlendDistanceXZ(Vector3 deltaRealLocal, Vector3 ratio)
    {
        // deltaRealLocal is in cubeReal local frame.
        float ax = Mathf.Abs(deltaRealLocal.x);
        float az = Mathf.Abs(deltaRealLocal.z);

        if (!useSurfaceDistanceForBlend)
        {
            return Mathf.Sqrt(ax * ax + az * az); // center distance in XZ
        }

        // surface distance to deformed box in XZ:
        // halfExtents' = baseHalfExtents * ratio  (XZ only)
        float hx = baseHalfExtents.x * ratio.x;
        float hz = baseHalfExtents.z * ratio.z;

        float ex = Mathf.Max(ax - hx, 0f);
        float ez = Mathf.Max(az - hz, 0f);

        return Mathf.Sqrt(ex * ex + ez * ez);
    }

    float ComputeObjectBlend(float d)
    {
        if (d <= nearRadius) return 1f;
        if (d >= farRadius) return 0f;

        float t = (d - nearRadius) / (farRadius - nearRadius); // 0..1
        // smoothstep
        float s = t * t * (3f - 2f * t);
        return 1f - s; // near -> 1, far -> 0
    }
}
*/



using System;
using UnityEngine;

/// <summary>
/// HMD中心の単純線形ワープ(F) と、物体近傍のローカル恒等写像(G) をブレンドするコントローラ。
///
/// 追加要件（2026-01-05）:
/// - Cube がある軸に沿って n 倍に変形されたとき、G（恒等写像側）のその軸成分は
///     * 「側面と同じ座標範囲内（= 実物Cubeの側面が存在する範囲）」では n 倍
///     * その範囲外では恒等（倍率 1）
///   となるようにする（Y だけでなく X/Y/Z 全軸で同様）。
/// - 既存の F とのブレンド（nearRadius/farRadius, surface distance）はこの G に統合する。
/// - ピンチ（変形）中は ratio を更新せず、変形終了時のみコミット（=コミット方式）。
/// - nearRadius / farRadius は実距離[m]として固定（スクリプト側で変更しない）。
/// </summary>
public class GoGoInteractionController_NoY2 : MonoBehaviour
{
    [Header("HMD / CameraCenter")]
    [Tooltip("HMDの中心（例：OVRCameraRig/CenterEyeAnchor など）")]
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

    [Header("Object-local identity (G) / Blend")]
    [Tooltip("実物Cubeの半サイズ（cubeRealローカル座標）。例: Cube(1m)なら (0.5,0.5,0.5)")]
    public Vector3 baseHalfExtents = new Vector3(0.5f, 0.5f, 0.5f);

    [Tooltip("nearRadius/farRadius の距離dを '変形後の箱表面' からの距離で評価する（推奨）")]
    public bool useSurfaceDistanceForBlend = true;

    [Tooltip("ブレンド距離dの計算にY成分も含める（推奨：true）。falseだとXZのみ。")]
    public bool blendDistanceIncludeY = true;

    [Tooltip("d <= nearRadius でG=1, d >= farRadius でG=0（実距離[m]）")]
    public float nearRadius = 0.12f;
    public float farRadius = 0.30f;

    [Header("Axis-gated deform identity (G)")]
    [Tooltip("側面の座標範囲境界で急に切り替えると不連続になるので、必要なら遷移幅[m]を与える（0で完全な段差）。")]
    public float axisGateTransitionWidth = 0.0f;

    [Header("Frame Option")]
    [Tooltip("Yaw-only（HMDのYawだけ）をローカル座標系として扱う（回頭でローカル軸がぶれにくい）")]
    public bool useYawOnlyFrame = true;

    // ----------------------------
    // Internal state (commit ratio)
    // ----------------------------
    Vector3 baseWarpedScale;
    bool _baseScaleInitialized = false;
    Vector3 _committedRatio = Vector3.one;

    // Optional deform controller hook (存在する場合のみ)
    DeformableCubeController _deformCtrl;

    void Awake()
    {
        if (cubeWarped != null)
        {
            baseWarpedScale = cubeWarped.localScale;
            _baseScaleInitialized = true;
        }
        _committedRatio = Vector3.one;

        // 変形開始/終了を拾えれば ratio のコミットに使う（無ければ Update で手動コミット可）
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

        // Cube warped visual position is always updated (F)
        UpdateCubeWarpedVisual();

        // Hands redirected
        if (leftHandOriginal != null && leftHandRedirector != null)
            UpdateHand(leftHandOriginal, leftHandRedirector);

        if (rightHandOriginal != null && rightHandRedirector != null)
            UpdateHand(rightHandOriginal, rightHandRedirector);
    }

    // ----------------------------
    // Ratio commit
    // ----------------------------
    void HandleDeformEnd(Vector3 finalLocalScale)
    {
        CommitRatio(finalLocalScale);
    }

    public void CommitCurrentRatioFromCubeWarped()
    {
        if (cubeWarped == null) return;
        CommitRatio(cubeWarped.localScale);
    }

    void CommitRatio(Vector3 currentLocalScale)
    {
        if (!_baseScaleInitialized) return;

        // Avoid division by zero
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

        // cubeWarped が HMD の子になっていると頭追従の二重変換になりやすいので外す
        if (cubeWarped.parent == cameraCenter)
        {
            cubeWarped.SetParent(null, worldPositionStays: true);
        }
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

        if (!TryGetCubeRealPose(out Vector3 cubePosW, out Quaternion cubeRotW))
            return;

        // F: cube center mapping (HMD local -> warp -> world)
        Vector3 cubeLocal = WorldToLocalForWarp(cubePosW);
        Vector3 cubeLocalWarped = LinearWarpLocal(cubeLocal);
        Vector3 cubePosWarpW = LocalToWorldForWarp(cubeLocalWarped);

        cubeWarped.position = cubePosWarpW;
        cubeWarped.rotation = cubeRotW; // 回転は同じ（必要なら将来ここを拡張）
    }

    // ----------------------------
    // Main hand update
    // ----------------------------
    void UpdateHand(Transform original, Transform redirector)
    {
        // F: hand position
        Vector3 hLocal = WorldToLocalForWarp(original.position);
        Vector3 hLocalWarped = LinearWarpLocal(hLocal);
        Vector3 hWarpW = LocalToWorldForWarp(hLocalWarped);

        if (!TryGetCubeRealPose(out Vector3 cubePosW, out Quaternion cubeRotW))
        {
            redirector.position = hWarpW;
            redirector.rotation = original.rotation;
            return;
        }

        // IMPORTANT: mapping uses committed ratio only (no change during pinch)
        Vector3 ratio = _committedRatio;


        // delta in cubeReal local frame
        Vector3 deltaRealW = original.position - cubePosW;
        Vector3 deltaRealLocal = Quaternion.Inverse(cubeRotW) * deltaRealW;

        // G: axis-gated deform identity in cube local
        Vector3 deltaWarpLocal = ApplyAxisGatedDeform(deltaRealLocal, ratio);

        Vector3 hObjW = cubeWarped.position + (cubeWarped.rotation * deltaWarpLocal);

        // Blend beta(d)
        float d = ComputeBlendDistance(deltaRealLocal, ratio);
        float beta = ComputeObjectBlend(d);

        redirector.position = Vector3.Lerp(hWarpW, hObjW, beta);
        redirector.rotation = original.rotation;
    }

    // ----------------------------
    // Axis-gated deform identity (G)
    // ----------------------------
    Vector3 ApplyAxisGatedDeform(Vector3 deltaRealLocal, Vector3 ratio)
    {
        float sx = AxisGatedScale(deltaRealLocal.x, baseHalfExtents.x, ratio.x, axisGateTransitionWidth);
        float sy = AxisGatedScale(deltaRealLocal.y, baseHalfExtents.y, ratio.y, axisGateTransitionWidth);
        float sz = AxisGatedScale(deltaRealLocal.z, baseHalfExtents.z, ratio.z, axisGateTransitionWidth);

        return new Vector3(
            deltaRealLocal.x * sx,
            deltaRealLocal.y * sy,
            deltaRealLocal.z * sz
        );
    }

    static float AxisGatedScale(float coord, float halfExtent, float scale, float transitionWidth)
    {
        // halfExtent<=0 の場合は恒等（安全側）
        if (halfExtent <= 1e-6f) return 1f;

        float a = Mathf.Abs(coord);

        float gate;
        if (transitionWidth <= 1e-6f)
        {
            gate = (a <= halfExtent) ? 1f : 0f; // 要件通り：範囲内は n倍、外は恒等
        }
        else
        {
            // halfExtent から halfExtent+transitionWidth の間で 1→0 に滑らかに落とす
            float t = Mathf.Clamp01((a - halfExtent) / transitionWidth);
            float smooth = t * t * (3f - 2f * t); // smoothstep
            gate = 1f - smooth;
        }

        return Mathf.Lerp(1f, scale, gate);
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
        {
            ay = 0f;
        }

        if (!useSurfaceDistanceForBlend)
        {
            // center distance (option)
            return Mathf.Sqrt(ax * ax + ay * ay + az * az);
        }

        // surface distance to deformed box:
        // halfExtents' = baseHalfExtents * ratio (3D)
        float hx = baseHalfExtents.x * ratio.x;
        float hy = baseHalfExtents.y * ratio.y;
        float hz = baseHalfExtents.z * ratio.z;

        float ex = Mathf.Max(ax - hx, 0f);
        float ey = blendDistanceIncludeY ? Mathf.Max(ay - hy, 0f) : 0f;
        float ez = Mathf.Max(az - hz, 0f);

        return Mathf.Sqrt(ex * ex + ey * ey + ez * ez);
    }

    float ComputeObjectBlend(float d)
    {
        if (d <= nearRadius) return 1f;
        if (d >= farRadius) return 0f;

        float t = (d - nearRadius) / (farRadius - nearRadius); // 0..1
        float s = t * t * (3f - 2f * t); // smoothstep
        return 1f - s; // near -> 1, far -> 0
    }
}
