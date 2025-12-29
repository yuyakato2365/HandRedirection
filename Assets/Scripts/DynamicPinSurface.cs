/*using UnityEngine;

public class DynamicPinSurface : MonoBehaviour
{
    [Header("Grid Settings")]
    public Transform pinPrefab;            // 立体（ピン）のPrefab（Cubeなど）
    [Range(1, 200)]
    public int gridResolution = 10;        // 一辺の本数（10 → 100とかに変更）
    [Min(0.01f)]
    public float spacing = 0.25f;          // ★ ピン同士の間隔（ここを変えればOK）

    [Header("Go-Go Hand Tip")]
    public Transform goGoHandTip;          // Go-Goで伸びた手先（HandRedirector_R など）

    [Header("Height Settings")]
    public float baseHeight = 0.1f;        // 最低高さ（スケールY）
    public float maxExtraHeight = 0.5f;    // 追加高さ（ピークの高さ）
    public float effectRadius = 1.5f;      // 手の影響が届く半径（ワールド座標の距離）

    [Tooltip("ガウス分布のσをeffectRadiusに対する割合で指定")]
    public float sigmaRatio = 0.4f;        // 小さいほどピークが鋭くなる

    [Header("Smoothing")]
    public float heightLerpSpeed = 12f;    // 高さ変化のなめらかさ

    Transform[,] _pins;
    int _oldRes = -1;
    float _oldSpacing = -1f;

    void OnValidate()
    {
        if (spacing < 0.01f) spacing = 0.01f;
        if (effectRadius < 0.01f) effectRadius = 0.01f;
        if (sigmaRatio <= 0f) sigmaRatio = 0.01f;
    }

    void Start()
    {
        Rebuild();
    }

    void Rebuild()
    {
        // 既存のピンを削除
        if (_pins != null)
        {
            foreach (var p in _pins)
            {
                if (p) Destroy(p.gameObject);
            }
        }

        _pins = new Transform[gridResolution, gridResolution];
        _oldRes = gridResolution;
        _oldSpacing = spacing;

        // グリッド全体のサイズ（ローカル座標）
        float totalSize = (gridResolution - 1) * spacing;
        float half = totalSize * 0.5f;

        for (int x = 0; x < gridResolution; x++)
        {
            for (int z = 0; z < gridResolution; z++)
            {
                // PinSurface を原点としたローカル配置
                Vector3 localPos = new Vector3(
                    x * spacing - half,
                    baseHeight * 0.5f,
                    z * spacing - half
                );

                // 親だけ指定して生成 → localPosition で並べる
                Transform pin = Instantiate(pinPrefab, transform);
                pin.localPosition = localPos;
                pin.localRotation = Quaternion.identity;

                // 初期高さ
                Vector3 s = pin.localScale;
                s.y = baseHeight;
                pin.localScale = s;

                _pins[x, z] = pin;
            }
        }
    }

    void Update()
    {
        // 解像度または間隔が変わったら自動で並べ直す
        if (_oldRes != gridResolution || !Mathf.Approximately(_oldSpacing, spacing))
        {
            Rebuild();
        }

        if (goGoHandTip == null || _pins == null) return;

        Vector3 handPos = goGoHandTip.position;

        float radiusSq = effectRadius * effectRadius;
        float sigma = effectRadius * sigmaRatio;       // ガウスのσ
        float twoSigmaSq = 2f * sigma * sigma;

        for (int x = 0; x < gridResolution; x++)
        {
            for (int z = 0; z < gridResolution; z++)
            {
                Transform pin = _pins[x, z];
                if (pin == null) continue;

                // 距離はワールド座標で計算（XZ平面）
                Vector3 pw = pin.position;
                float dx = pw.x - handPos.x;
                float dz = pw.z - handPos.z;
                float distSq = dx * dx + dz * dz;

                // ガウス分布：中心1、離れると急激に0へ
                float t = 0f;
                if (distSq < radiusSq)
                {
                    t = Mathf.Exp(-distSq / twoSigmaSq);
                }

                float targetH = baseHeight + maxExtraHeight * t;

                // 高さをスムーズに補間
                Vector3 s = pin.localScale;
                float newH = Mathf.Lerp(s.y, targetH, Time.deltaTime * heightLerpSpeed);
                s.y = newH;
                pin.localScale = s;

                // ローカルYだけ上げ下げ（PinSurfaceの移動に追従）
                Vector3 lp = pin.localPosition;
                lp.y = newH * 0.5f;
                pin.localPosition = lp;
            }
        }
    }
}
*/


/*
using UnityEngine;

/// <summary>
/// グリッド状のピン床 + 左右Go-Go手に応じたガウス変形 +
/// HeartRateManager の HeartRateReceiver を使った明滅（全ピンのマテリアルを個別制御）
///
/// 盛り上がり判定は、「Go-Go写像後の手」の
/// cameraCenter に対する相対座標を使って行う。
/// → DynamicPinSurface をどこに置いても / どう回しても、
///   グリッド中心が cameraCenter 原点に対応したままになる。
/// </summary>
public class DynamicPinSurface_HeartPulse_UseExternalHeart : MonoBehaviour
{
    [Header("Grid Settings")]
    public Transform pinPrefab;             // 並べたい立体（Cubeなど）のPrefab
    [Range(1, 200)]
    public int gridResolution = 10;         // 一辺の本数
    [Min(0.01f)]
    public float spacing = 0.25f;           // 立体同士の間隔（論理グリッドの間隔も兼ねる）

    [Header("Go-Go Hand Tips (Redirected)")]
    [Tooltip("写像後の左手（例: HandRedirector_L）")]
    public Transform leftHandTip;           // Go-Go後の手
    [Tooltip("写像後の右手（例: HandRedirector_R）")]
    public Transform rightHandTip;          // Go-Go後の手

    [Header("Go-Go Origin")]
    [Tooltip("GoGoInteractionController_NoY の cameraCenter（CenterEyeAnchor）")]
    public Transform cameraCenter;          // 手の原点

    [Header("Height (Gaussian Surface)")]
    public float baseHeight = 0.1f;         // 最低高さ（スケールY）
    public float maxExtraHeight = 0.5f;     // 追加高さ（どこまで持ち上がるか）
    public float effectRadius = 1.5f;       // 手の影響半径（相対座標上の距離）
    [Tooltip("ガウス分布のσをeffectRadiusに対する割合で指定（小さいほどピークが鋭い）")]
    public float sigmaRatio = 0.4f;

    [Header("Height Smoothing")]
    public float heightLerpSpeed = 12f;     // 高さ変化のなめらかさ

    [Header("Heart Source")]
    [Tooltip("HeartRateManager 上の HeartRateReceiver をここにドラッグ")]
    public HeartRateReceiver heartRateSource;

    [Header("Heart Pulse - Light Settings")]
    public Light pointLight;                // 脈動させたい点光源（任意）
    public float minIntensity = 0.2f;       // 最低光量
    public float maxIntensity = 2.0f;       // 最高光量
    public float lightAmplitude = 1.0f;     // ライトの脈動幅（0〜1 をどれだけ使うか）

    [Header("Heart Pulse - Material Emission")]
    public Color emissionBaseColor = Color.red; // 発光色
    public float emissionMin = 0.1f;        // 最低Emission
    public float emissionMax = 3.0f;        // 最大Emission(HDR推奨)

    Transform[,] _pins;
    Renderer[,] _pinRenderers;
    Material[,] _pinMaterials;

    // ★ 相対座標系上の「論理グリッド位置」
    // グリッド中心が常に (0,0,0) になるようにする
    Vector3[,] _logicalPinPositions;

    int _oldRes = -1;
    float _oldSpacing = -1f;

    float _phase;                           // 脈動フェーズ

    void OnValidate()
    {
        if (spacing < 0.01f) spacing = 0.01f;
        if (effectRadius < 0.01f) effectRadius = 0.01f;
        if (sigmaRatio <= 0f) sigmaRatio = 0.01f;
        if (baseHeight < 0f) baseHeight = 0f;
    }

    void Start()
    {
        Rebuild();
    }

    void Rebuild()
    {
        // 既存ピン削除
        if (_pins != null)
        {
            foreach (var p in _pins)
            {
                if (p != null) Destroy(p.gameObject);
            }
        }

        _pins = new Transform[gridResolution, gridResolution];
        _pinRenderers = new Renderer[gridResolution, gridResolution];
        _pinMaterials = new Material[gridResolution, gridResolution];
        _logicalPinPositions = new Vector3[gridResolution, gridResolution];

        _oldRes = gridResolution;
        _oldSpacing = spacing;

        // 見た目上の配置：PinSurface のローカル座標（中心が0,0,0）
        float totalSize = (gridResolution - 1) * spacing;
        float half = totalSize * 0.5f;

        // 論理グリッド用：中央が常に (0,0,0) になるように index をシフト
        float halfIndex = (gridResolution - 1) * 0.5f; // 例: 10 -> 4.5

        for (int x = 0; x < gridResolution; x++)
        {
            for (int z = 0; z < gridResolution; z++)
            {
                // ─ 見た目用：PinSurface ローカル座標に並べる ─
                Vector3 localPos = new Vector3(
                    x * spacing - half,
                    baseHeight * 0.5f,
                    z * spacing - half
                );

                Transform pin = Instantiate(pinPrefab, transform);
                pin.localPosition = localPos;
                pin.localRotation = Quaternion.identity;

                // 初期高さ
                Vector3 s = pin.localScale;
                s.y = baseHeight;
                pin.localScale = s;

                _pins[x, z] = pin;

                // Renderer & Material
                Renderer r = pin.GetComponentInChildren<Renderer>();
                _pinRenderers[x, z] = r;

                if (r != null)
                {
                    Material mat = r.material; // 各ピン専用のインスタンス
                    mat.EnableKeyword("_EMISSION");
                    _pinMaterials[x, z] = mat;
                }

                // ─ 論理用：写像空間上のグリッド座標（中央が 0,0,0） ─
                float lx = (x - halfIndex) * spacing;
                float lz = (z - halfIndex) * spacing;
                _logicalPinPositions[x, z] = new Vector3(lx, 0f, lz);
            }
        }
    }

    void Update()
    {
        // 解像度 or 間隔が変わったら並べ直し
        if (_oldRes != gridResolution || !Mathf.Approximately(_oldSpacing, spacing))
        {
            Rebuild();
        }

        if (_pins == null) return;

        UpdateHeightsByHands();
        UpdatePulseByHeartRate();
    }

    /// <summary>
    /// 左右のGo-Go手先に応じたガウス分布でピンの高さを更新。
    ///
    /// ・Go-Go側で計算済みの「写像後の手」の位置 (left/rightHandTip)
    /// ・cameraCenter を原点とした相対ベクトル handRel = tip - cameraCenter
    /// ・論理グリッド座標 _logicalPinPositions（中央が常に (0,0,0)）
    ///
    /// を比較して、どのピンをどれだけ盛り上げるか決める。
    /// DynamicPinSurface 自体の位置・回転には依存しない。
    /// </summary>
    void UpdateHeightsByHands()
    {
        if (cameraCenter == null) return;
        if (leftHandTip == null && rightHandTip == null) return;

        float radiusSq = effectRadius * effectRadius;
        float sigma = effectRadius * sigmaRatio;
        float twoSigmaSq = 2f * sigma * sigma;

        // 写像後の手の「原点に対する相対座標」を計算（XZ平面）
        Vector3? leftRel = null;
        Vector3? rightRel = null;

        if (leftHandTip != null)
        {
            Vector3 rel = leftHandTip.position - cameraCenter.position;
            leftRel = new Vector3(rel.x, 0f, rel.z);
        }
        if (rightHandTip != null)
        {
            Vector3 rel = rightHandTip.position - cameraCenter.position;
            rightRel = new Vector3(rel.x, 0f, rel.z);
        }

        for (int x = 0; x < gridResolution; x++)
        {
            for (int z = 0; z < gridResolution; z++)
            {
                Transform pin = _pins[x, z];
                if (pin == null) continue;

                // ★ このピンの「写像空間上での論理位置」（中央が 0,0,0）
                Vector3 pinPosLogical = _logicalPinPositions[x, z];

                float t = 0f;

                // 左手の影響
                if (leftRel.HasValue)
                {
                    Vector3 diffL = pinPosLogical - leftRel.Value;
                    float distSqL = diffL.sqrMagnitude;
                    if (distSqL < radiusSq)
                    {
                        float tL = Mathf.Exp(-distSqL / twoSigmaSq);
                        if (tL > t) t = tL;
                    }
                }

                // 右手の影響
                if (rightRel.HasValue)
                {
                    Vector3 diffR = pinPosLogical - rightRel.Value;
                    float distSqR = diffR.sqrMagnitude;
                    if (distSqR < radiusSq)
                    {
                        float tR = Mathf.Exp(-distSqR / twoSigmaSq);
                        if (tR > t) t = tR;
                    }
                }

                float targetH = baseHeight + maxExtraHeight * t;

                // 高さをスムーズに補間
                Vector3 s = pin.localScale;
                float newH = Mathf.Lerp(s.y, targetH, Time.deltaTime * heightLerpSpeed);
                s.y = newH;
                pin.localScale = s;

                // ローカルYのみ更新（見た目の位置はそのまま）
                Vector3 lpPos = pin.localPosition;
                lpPos.y = newH * 0.5f;
                pin.localPosition = lpPos;
            }
        }
    }

    /// <summary>
    /// HeartRateManagerのHeartRateReceiverに基づいてライトと全ピンのマテリアルを明滅
    /// </summary>
    void UpdatePulseByHeartRate()
    {
        if (heartRateSource == null) return;

        // BPM取得（0防止）
        float bpm = Mathf.Max(heartRateSource.heartRate, 1f);
        float pulseFreq = bpm / 60f; // Hz = BPM/60

        // 位相進行
        float delta = Time.deltaTime * pulseFreq * 2f * Mathf.PI;
        _phase += delta;

        // 0〜1 の脈動波形（sin を 0〜1 にマップ）
        float pulse = Mathf.Sin(_phase) * 0.5f + 0.5f;
        float pulseAmp = pulse * lightAmplitude; // 振幅

        // ライト明滅
        if (pointLight != null)
        {
            float intensity = Mathf.Lerp(minIntensity, maxIntensity, pulseAmp);
            pointLight.intensity = intensity;
        }

        // 全ピンのマテリアル明滅（Emission制御）
        for (int x = 0; x < gridResolution; x++)
        {
            for (int z = 0; z < gridResolution; z++)
            {
                Material mat = _pinMaterials[x, z];
                if (mat == null) continue;

                float e = Mathf.Lerp(emissionMin, emissionMax, pulseAmp);
                Color finalColor = emissionBaseColor * e;
                mat.SetColor("_EmissionColor", finalColor);
            }
        }
    }
}
*/








/*
using UnityEngine;

/// <summary>
/// グリッド状のピン床 + 左右Go-Go手に応じたガウス変形 +
/// HeartRateManager の HeartRateReceiver を使った明滅（全ピンのマテリアルを個別制御）
///
/// 盛り上がり位置:
///   - Go-Go写像後の手 (HandRedirector_L/R)
///   - cameraCenter に対する相対XZ座標 で決定
///
/// 盛り上がり量(振幅):
///   - 同じく cameraCenter に対する相対Y座標 (高さ) でスケール
///   - 手を高く上げると山が大きくなる / 低くすると小さくなる
/// </summary>
public class DynamicPinSurface_HeartPulse_UseExternalHeart : MonoBehaviour
{
    [Header("Grid Settings")]
    public Transform pinPrefab;             // 並べたい立体（ピン）のPrefab
    [Range(1, 200)]
    public int gridResolution = 10;         // 一辺の本数
    [Min(0.01f)]
    public float spacing = 0.25f;           // グリッドの間隔

    [Header("Go-Go Hand Tips (Redirected)")]
    [Tooltip("写像後の左手（例: HandRedirector_L）")]
    public Transform leftHandTip;           // Go-Go後の左手
    [Tooltip("写像後の右手（例: HandRedirector_R）")]
    public Transform rightHandTip;          // Go-Go後の右手

    [Header("Go-Go Origin")]
    [Tooltip("GoGoInteractionController_NoY の cameraCenter（CenterEyeAnchor）")]
    public Transform cameraCenter;          // 手の原点（高さYもここ基準）

    [Header("Height (Gaussian Surface)")]
    public float baseHeight = 0.1f;         // 最低高さ（スケールY）
    public float maxExtraHeight = 0.5f;     // 追加高さ（どこまで持ち上がるか）
    public float effectRadius = 1.5f;       // 手の影響半径（相対XZの距離）
    [Tooltip("ガウス分布のσをeffectRadiusに対する割合で指定（小さいほどピークが鋭い）")]
    public float sigmaRatio = 0.4f;

    [Header("Height Smoothing")]
    public float heightLerpSpeed = 12f;     // 高さ変化のなめらかさ

    [Header("Hand Height Influence (Y)")]
    [Tooltip("相対Yの最小値（この高さ以下で最小スケール）")]
    public float heightMinY = -0.2f;        // cameraCenter から見て 20cm 下
    [Tooltip("相対Yの最大値（この高さ以上で最大スケール）")]
    public float heightMaxY = 0.5f;         // cameraCenter から見て 50cm 上
    [Tooltip("手が低いときの山のスケール")]
    public float minHeightScale = 0.3f;     // 低いときの maxExtraHeight 係数
    [Tooltip("手が高いときの山のスケール")]
    public float maxHeightScale = 1.2f;     // 高いときの maxExtraHeight 係数

    [Header("Heart Source")]
    [Tooltip("HeartRateManager 上の HeartRateReceiver をここにドラッグ")]
    public HeartRateReceiver heartRateSource;

    [Header("Heart Pulse - Light Settings")]
    public Light pointLight;                // 脈動させたい点光源（任意）
    public float minIntensity = 0.2f;       // 最低光量
    public float maxIntensity = 2.0f;       // 最高光量
    public float lightAmplitude = 1.0f;     // ライトの脈動幅（0〜1 をどれだけ使うか）

    [Header("Heart Pulse - Material Emission")]
    public Color emissionBaseColor = Color.red; // 発光色
    public float emissionMin = 0.1f;        // 最低Emission
    public float emissionMax = 3.0f;        // 最大Emission(HDR推奨)

    Transform[,] _pins;
    Renderer[,] _pinRenderers;
    Material[,] _pinMaterials;

    // 「写像空間上の論理グリッド位置」
    // グリッド中心が常に (0,0,0) になるようにする
    Vector3[,] _logicalPinPositions;

    int _oldRes = -1;
    float _oldSpacing = -1f;

    float _phase;                           // 脈動フェーズ

    void OnValidate()
    {
        if (spacing < 0.01f) spacing = 0.01f;
        if (effectRadius < 0.01f) effectRadius = 0.01f;
        if (sigmaRatio <= 0f) sigmaRatio = 0.01f;
        if (baseHeight < 0f) baseHeight = 0f;
        if (heightMaxY <= heightMinY)
        {
            heightMaxY = heightMinY + 0.01f;
        }
    }

    void Start()
    {
        Rebuild();
    }

    void Rebuild()
    {
        // 既存ピン削除
        if (_pins != null)
        {
            foreach (var p in _pins)
            {
                if (p != null) Destroy(p.gameObject);
            }
        }

        _pins = new Transform[gridResolution, gridResolution];
        _pinRenderers = new Renderer[gridResolution, gridResolution];
        _pinMaterials = new Material[gridResolution, gridResolution];
        _logicalPinPositions = new Vector3[gridResolution, gridResolution];

        _oldRes = gridResolution;
        _oldSpacing = spacing;

        // 見た目上の配置：PinSurface のローカル座標（中心が0,0,0）
        float totalSize = (gridResolution - 1) * spacing;
        float half = totalSize * 0.5f;

        // 論理グリッド用：中央が常に (0,0,0) になるように index をシフト
        float halfIndex = (gridResolution - 1) * 0.5f; // 例: N=10 -> 4.5

        for (int x = 0; x < gridResolution; x++)
        {
            for (int z = 0; z < gridResolution; z++)
            {
                // ─ 見た目用: PinSurface ローカル座標に並べる ─
                Vector3 localPos = new Vector3(
                    x * spacing - half,
                    baseHeight * 0.5f,
                    z * spacing - half
                );

                Transform pin = Instantiate(pinPrefab, transform);
                pin.localPosition = localPos;
                pin.localRotation = Quaternion.identity;

                // 初期高さ
                Vector3 s = pin.localScale;
                s.y = baseHeight;
                pin.localScale = s;

                _pins[x, z] = pin;

                // Renderer & Material
                Renderer r = pin.GetComponentInChildren<Renderer>();
                _pinRenderers[x, z] = r;

                if (r != null)
                {
                    Material mat = r.material; // 各ピン専用のインスタンス
                    mat.EnableKeyword("_EMISSION");
                    _pinMaterials[x, z] = mat;
                }

                // ─ 論理用: 写像空間上のグリッド座標（中央が 0,0,0） ─
                float lx = (x - halfIndex) * spacing;
                float lz = (z - halfIndex) * spacing;
                _logicalPinPositions[x, z] = new Vector3(lx, 0f, lz);
            }
        }
    }

    void Update()
    {
        // 解像度 or 間隔が変わったら並べ直し
        if (_oldRes != gridResolution || !Mathf.Approximately(_oldSpacing, spacing))
        {
            Rebuild();
        }

        if (_pins == null) return;

        UpdateHeightsByHands();
        UpdatePulseByHeartRate();
    }

    /// <summary>
    /// 左右のGo-Go手先に応じたガウス分布でピンの高さを更新。
    ///
    /// ・写像後の手 (HandRedirector_L/R)
    /// ・cameraCenter に対する相対XZ → どのピンが盛り上がるか
    /// ・cameraCenter に対する相対Y   → 山のスケール（振幅）を決める
    ///
    /// DynamicPinSurface 自体の位置・回転には依存しない。
    /// </summary>
    void UpdateHeightsByHands()
    {
        if (cameraCenter == null) return;
        if (leftHandTip == null && rightHandTip == null) return;

        float radiusSq = effectRadius * effectRadius;
        float sigma = effectRadius * sigmaRatio;
        float twoSigmaSq = 2f * sigma * sigma;

        // --- 写像後の手の「原点に対する相対座標」を計算 ---

        Vector3? leftRelXZ = null;
        Vector3? rightRelXZ = null;
        float? leftRelY = null;
        float? rightRelY = null;

        if (leftHandTip != null)
        {
            Vector3 rel = leftHandTip.position - cameraCenter.position;
            leftRelXZ = new Vector3(rel.x, 0f, rel.z);
            leftRelY = rel.y; // ← 高さ情報
        }
        if (rightHandTip != null)
        {
            Vector3 rel = rightHandTip.position - cameraCenter.position;
            rightRelXZ = new Vector3(rel.x, 0f, rel.z);
            rightRelY = rel.y;
        }

        // --- 手の高さYに応じた「山のスケール」計算（フレーム一括） ---

        float heightScale = 1f; // 係数（このフレームでの maxExtraHeight の倍率）

        if (leftRelY.HasValue || rightRelY.HasValue)
        {
            // 左右の相対Yを統合（存在するものの平均）
            float sumY = 0f;
            int countY = 0;
            if (leftRelY.HasValue)
            {
                sumY += leftRelY.Value;
                countY++;
            }
            if (rightRelY.HasValue)
            {
                sumY += rightRelY.Value;
                countY++;
            }
            float avgY = sumY / countY; // cameraCenter から見た平均高さ

            // avgY を [heightMinY, heightMaxY] にマップして 0〜1 に正規化
            float norm = Mathf.InverseLerp(heightMinY, heightMaxY, avgY);
            // 0〜1 を [minHeightScale, maxHeightScale] にマップ
            heightScale = Mathf.Lerp(minHeightScale, maxHeightScale, norm);
        }

        float scaledMaxExtraHeight = maxExtraHeight * heightScale;

        // --- 各ピンに対してガウス分布を適用 ---

        for (int x = 0; x < gridResolution; x++)
        {
            for (int z = 0; z < gridResolution; z++)
            {
                Transform pin = _pins[x, z];
                if (pin == null) continue;

                // このピンの「写像空間上での論理位置」（中央が 0,0,0）
                Vector3 pinPosLogical = _logicalPinPositions[x, z];

                float t = 0f;

                // 左手の影響（XZ平面）
                if (leftRelXZ.HasValue)
                {
                    Vector3 diffL = pinPosLogical - leftRelXZ.Value;
                    float distSqL = diffL.sqrMagnitude;
                    if (distSqL < radiusSq)
                    {
                        float tL = Mathf.Exp(-distSqL / twoSigmaSq);
                        if (tL > t) t = tL;
                    }
                }

                // 右手の影響（XZ平面）
                if (rightRelXZ.HasValue)
                {
                    Vector3 diffR = pinPosLogical - rightRelXZ.Value;
                    float distSqR = diffR.sqrMagnitude;
                    if (distSqR < radiusSq)
                    {
                        float tR = Mathf.Exp(-distSqR / twoSigmaSq);
                        if (tR > t) t = tR;
                    }
                }

                // 手の高さYを反映した maxExtraHeight を使って高さ決定
                float targetH = baseHeight + scaledMaxExtraHeight * t;

                // 高さをスムーズに補間
                Vector3 s = pin.localScale;
                float newH = Mathf.Lerp(s.y, targetH, Time.deltaTime * heightLerpSpeed);
                s.y = newH;
                pin.localScale = s;

                // ローカルYのみ更新（見た目の位置はそのまま）
                Vector3 lpPos = pin.localPosition;
                lpPos.y = newH * 0.5f;
                pin.localPosition = lpPos;
            }
        }
    }

    /// <summary>
    /// HeartRateManagerのHeartRateReceiverに基づいてライトと全ピンのマテリアルを明滅
    /// </summary>
    void UpdatePulseByHeartRate()
    {
        if (heartRateSource == null) return;

        // BPM取得（0防止）
        float bpm = Mathf.Max(heartRateSource.heartRate, 1f);
        float pulseFreq = bpm / 60f; // Hz = BPM/60

        // 位相進行
        float delta = Time.deltaTime * pulseFreq * 2f * Mathf.PI;
        _phase += delta;

        // 0〜1 の脈動波形（sin を 0〜1 にマップ）
        float pulse = Mathf.Sin(_phase) * 0.5f + 0.5f;
        float pulseAmp = pulse * lightAmplitude; // 振幅

        // ライト明滅
        if (pointLight != null)
        {
            float intensity = Mathf.Lerp(minIntensity, maxIntensity, pulseAmp);
            pointLight.intensity = intensity;
        }

        // 全ピンのマテリアル明滅（Emission制御）
        for (int x = 0; x < gridResolution; x++)
        {
            for (int z = 0; z < gridResolution; z++)
            {
                Material mat = _pinMaterials[x, z];
                if (mat == null) continue;

                float e = Mathf.Lerp(emissionMin, emissionMax, pulseAmp);
                Color finalColor = emissionBaseColor * e;
                mat.SetColor("_EmissionColor", finalColor);
            }
        }
    }
}
*/









using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// グリッド状のピン床 + 左右Go-Go手に応じたガウス変形
/// ＋ 盛り上がったピンだけが光り、盛り上がりに応じて「複数の光の波紋」が広がるバージョン。
///
/// ・心拍による明滅は空関数で無効化
/// ・高さはあくまでガウス変形のみ（波紋では高さを変えない）
/// ・高さに応じて基本Emissionが変化（盛り上がるほど光る）
/// ・手を動かすたびに波源が増え、既存の波は消えずに光として広がり続ける
/// ・波紋は「光の輪」としてのみ表現される
/// </summary>
public class DynamicPinSurface_HeartPulse_UseExternalHeart : MonoBehaviour
{
    [Header("Grid Settings")]
    public Transform pinPrefab;
    [Range(1, 200)]
    public int gridResolution = 10;
    [Min(0.01f)]
    public float spacing = 0.25f;

    [Header("Go-Go Hand Tips (Redirected)")]
    public Transform leftHandTip;
    public Transform rightHandTip;

    [Header("Go-Go Origin")]
    public Transform cameraCenter;

    [Header("Height (Gaussian Surface)")]
    public float baseHeight = 0.1f;
    public float maxExtraHeight = 0.5f;
    public float effectRadius = 1.5f;
    public float sigmaRatio = 0.4f;

    [Header("Height Smoothing")]
    public float heightLerpSpeed = 12f;

    [Header("Hand Height Influence (Y)")]
    public float heightMinY = -0.2f;
    public float heightMaxY = 0.5f;
    public float minHeightScale = 0.3f;
    public float maxHeightScale = 1.2f;

    [Header("Heart Source")]
    public HeartRateReceiver heartRateSource; // 参照は残すが使わない

    [Header("Emission Settings (NEW)")]
    public Color emissionBaseColor = Color.red;
    public float emissionMin = 0.0f;
    public float emissionMax = 3.0f;

    [Header("Ripple Settings (NEW)")]
    public float rippleSpeed = 4.0f;       // 波が広がる速さ
    public float rippleAmplitude = 0.5f;   // 光の強さへの寄与（波紋の明るさ）
    public float rippleWidth = 0.5f;       // 波面の厚み（リングの太さ）

    Transform[,] _pins;
    Renderer[,] _pinRenderers;
    Material[,] _pinMaterials;
    Vector3[,] _logicalPinPositions;

    int _oldRes = -1;
    float _oldSpacing = -1f;

    // ──────────────────────────
    // 波紋用：複数の波を保持
    // ──────────────────────────
    struct RippleWave
    {
        public Vector3 origin; // 論理解像度上の中心
        public float phase;    // 半径（時間とともに増える）
    }

    List<RippleWave> _ripples = new List<RippleWave>();

    Vector3 _lastPeakPos;
    bool _hasLastPeak = false;

    void OnValidate()
    {
        if (spacing < 0.01f) spacing = 0.01f;
        if (effectRadius < 0.01f) effectRadius = 0.01f;
        if (sigmaRatio <= 0f) sigmaRatio = 0.01f;
        if (baseHeight < 0f) baseHeight = 0f;
        if (heightMaxY <= heightMinY)
            heightMaxY = heightMinY + 0.01f;
    }

    void Start()
    {
        Rebuild();
    }

    void Rebuild()
    {
        if (_pins != null)
        {
            foreach (var p in _pins)
            {
                if (p != null) Destroy(p.gameObject);
            }
        }

        _pins = new Transform[gridResolution, gridResolution];
        _pinRenderers = new Renderer[gridResolution, gridResolution];
        _pinMaterials = new Material[gridResolution, gridResolution];
        _logicalPinPositions = new Vector3[gridResolution, gridResolution];

        _oldRes = gridResolution;
        _oldSpacing = spacing;

        float totalSize = (gridResolution - 1) * spacing;
        float half = totalSize * 0.5f;
        float halfIndex = (gridResolution - 1) * 0.5f;

        for (int x = 0; x < gridResolution; x++)
        {
            for (int z = 0; z < gridResolution; z++)
            {
                Vector3 localPos = new Vector3(
                    x * spacing - half,
                    baseHeight * 0.5f,
                    z * spacing - half
                );

                Transform pin = Instantiate(pinPrefab, transform);
                pin.localPosition = localPos;

                Vector3 s = pin.localScale;
                s.y = baseHeight;
                pin.localScale = s;

                _pins[x, z] = pin;

                Renderer r = pin.GetComponentInChildren<Renderer>();
                _pinRenderers[x, z] = r;

                if (r != null)
                {
                    Material mat = r.material;
                    mat.EnableKeyword("_EMISSION");
                    _pinMaterials[x, z] = mat;
                }

                float lx = (x - halfIndex) * spacing;
                float lz = (z - halfIndex) * spacing;
                _logicalPinPositions[x, z] = new Vector3(lx, 0f, lz);
            }
        }

        _ripples.Clear();
        _hasLastPeak = false;
    }

    void Update()
    {
        if (_oldRes != gridResolution || !Mathf.Approximately(_oldSpacing, spacing))
        {
            Rebuild();
        }

        if (_pins == null) return;

        UpdateHeightsByHands();
        UpdateRipples();
    }

    // ──────────────────────────
    // 高さ計算 + 光 + 波紋反映
    // ──────────────────────────
    void UpdateHeightsByHands()
    {
        if (cameraCenter == null) return;
        if (leftHandTip == null && rightHandTip == null) return;

        float radiusSq = effectRadius * effectRadius;
        float sigma = effectRadius * sigmaRatio;
        float twoSigmaSq = 2f * sigma * sigma;

        Vector3? leftRelXZ = null;
        Vector3? rightRelXZ = null;
        float? leftRelY = null;
        float? rightRelY = null;

        if (leftHandTip != null)
        {
            Vector3 rel = leftHandTip.position - cameraCenter.position;
            leftRelXZ = new Vector3(rel.x, 0f, rel.z);
            leftRelY = rel.y;
        }
        if (rightHandTip != null)
        {
            Vector3 rel = rightHandTip.position - cameraCenter.position;
            rightRelXZ = new Vector3(rel.x, 0f, rel.z);
            rightRelY = rel.y;
        }

        float heightScale = 1f;
        if (leftRelY.HasValue || rightRelY.HasValue)
        {
            float sumY = 0f;
            int countY = 0;
            if (leftRelY.HasValue) { sumY += leftRelY.Value; countY++; }
            if (rightRelY.HasValue) { sumY += rightRelY.Value; countY++; }
            float avgY = sumY / countY;
            float norm = Mathf.InverseLerp(heightMinY, heightMaxY, avgY);
            heightScale = Mathf.Lerp(minHeightScale, maxHeightScale, norm);
        }

        float scaledMaxExtraHeight = maxExtraHeight * heightScale;

        float maxT = 0f;
        Vector3 peakPos = Vector3.zero;

        for (int x = 0; x < gridResolution; x++)
        {
            for (int z = 0; z < gridResolution; z++)
            {
                Transform pin = _pins[x, z];
                Material mat = _pinMaterials[x, z];
                if (pin == null || mat == null) continue;

                Vector3 pinPosLogical = _logicalPinPositions[x, z];

                // ─ ガウスによる基本盛り上がり ─
                float t = 0f;

                if (leftRelXZ.HasValue)
                {
                    Vector3 diffL = pinPosLogical - leftRelXZ.Value;
                    float d2L = diffL.sqrMagnitude;
                    if (d2L < radiusSq)
                    {
                        float tL = Mathf.Exp(-d2L / twoSigmaSq);
                        if (tL > t) t = tL;
                    }
                }
                if (rightRelXZ.HasValue)
                {
                    Vector3 diffR = pinPosLogical - rightRelXZ.Value;
                    float d2R = diffR.sqrMagnitude;
                    if (d2R < radiusSq)
                    {
                        float tR = Mathf.Exp(-d2R / twoSigmaSq);
                        if (tR > t) t = tR;
                    }
                }

                if (t > maxT)
                {
                    maxT = t;
                    peakPos = pinPosLogical;
                }

                // 手による盛り上がりのみ（波紋では高さを変えない）
                float targetH = baseHeight + scaledMaxExtraHeight * t;

                // ─ 複数波紋からの「光」の寄与のみ ─
                float rippleLight = 0f;

                if (_ripples.Count > 0)
                {
                    float maxPhase = effectRadius * 2f;

                    for (int i = 0; i < _ripples.Count; i++)
                    {
                        RippleWave rw = _ripples[i];
                        float dist = Vector3.Distance(pinPosLogical, rw.origin);
                        float diff = Mathf.Abs(dist - rw.phase);

                        if (diff < rippleWidth)
                        {
                            // リングの太さに沿った重み（中心で1、端で0）
                            float w = 1f - (diff / rippleWidth); // 0〜1

                            // 時間経過による減衰（中心近く＝1、遠く＝0）
                            float attenuation = Mathf.InverseLerp(maxPhase, 0f, rw.phase);

                            // 光の強さとして加算（高さには足さない）
                            rippleLight += w * attenuation * rippleAmplitude;
                        }
                    }
                }

                // ─ 高さ補間 ─
                Vector3 s = pin.localScale;
                float newH = Mathf.Lerp(s.y, targetH, Time.deltaTime * heightLerpSpeed);
                s.y = newH;
                pin.localScale = s;

                Vector3 lpPos = pin.localPosition;
                lpPos.y = newH * 0.5f;
                pin.localPosition = lpPos;

                // ─ 高さ + 波紋に応じて光る（高さはガウスのみ、波紋は光だけ） ─
                float h01 = Mathf.InverseLerp(baseHeight, baseHeight + scaledMaxExtraHeight, newH);
                float glowFactor = Mathf.Clamp01(h01 + rippleLight); // 波紋が通ると一瞬明るくなる
                float e = Mathf.Lerp(emissionMin, emissionMax, glowFactor);
                mat.SetColor("_EmissionColor", emissionBaseColor * e);
            }
        }

        // ─ ピークが移動したときのみ「新しい波」を追加 ─
        if (maxT > 0.1f)
        {
            if (!_hasLastPeak || Vector3.Distance(peakPos, _lastPeakPos) > spacing * 0.25f)
            {
                _ripples.Add(new RippleWave
                {
                    origin = peakPos,
                    phase = 0f
                });
                _hasLastPeak = true;
            }

            _lastPeakPos = peakPos;
        }
        else
        {
            _hasLastPeak = false;
        }
    }

    // ──────────────────────────
    // 波紋の時間進行：全部の phase を前に進める
    // ──────────────────────────
    void UpdateRipples()
    {
        if (_ripples.Count == 0) return;

        float maxPhase = effectRadius * 2f;

        for (int i = _ripples.Count - 1; i >= 0; i--)
        {
            RippleWave rw = _ripples[i];
            rw.phase += Time.deltaTime * rippleSpeed;

            if (rw.phase > maxPhase)
            {
                _ripples.RemoveAt(i); // 遠くまで行った波は削除
            }
            else
            {
                _ripples[i] = rw;
            }
        }
    }

    // 心拍明滅は空（構造だけ残す）
    void UpdatePulseByHeartRate()
    {
        return;
    }
}
