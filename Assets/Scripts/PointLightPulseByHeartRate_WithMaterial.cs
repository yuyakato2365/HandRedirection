using UnityEngine;

[RequireComponent(typeof(HeartRateReceiver))]
public class PointLightPulseByHeartRate : MonoBehaviour
{
    [Header("Light Settings")]
    public Light pointLight;
    public float minIntensity = 0.2f;   // 最低光量
    public float maxIntensity = 2.0f;   // ピーク光量
    public float amplitude = 1.0f;      // 脈動幅の調整

    [Header("Material Emission Settings")]
    public Renderer targetRenderer;         // 明滅させたいオブジェクト
    public Color emissionBaseColor = Color.red;  // 基準の発光色
    public float emissionMin = 0.1f;        // 最低Emission
    public float emissionMax = 3.0f;        // 最大Emission (HDR推奨)

    private HeartRateReceiver heart;
    private float phase;

    private Material mat;

    void Start()
    {
        heart = GetComponent<HeartRateReceiver>();

        // マテリアルの準備
        if (targetRenderer != null)
        {
            mat = targetRenderer.material;     // インスタンス化されたマテリアルを取得
            mat.EnableKeyword("_EMISSION");    // Emission有効化
        }
    }

    void Update()
    {
        float bpm = Mathf.Max(heart.heartRate, 1);
        float pulseFreq = bpm / 60f;  // Hz = BPM/60
        float delta = Time.deltaTime * pulseFreq * 2f * Mathf.PI;

        // 位相進行
        phase += delta;

        // 0〜1 の脈動波形（sin波を0〜1に正規化）
        float pulse = (Mathf.Sin(phase) * 0.5f + 0.5f) * amplitude;

        // ★ ライト明滅
        if (pointLight != null)
        {
            float intensity = Mathf.Lerp(minIntensity, maxIntensity, pulse);
            pointLight.intensity = intensity;
        }

        // ★ マテリアル明滅（Emission制御）
        if (mat != null)
        {
            float e = Mathf.Lerp(emissionMin, emissionMax, pulse);
            Color finalColor = emissionBaseColor * e;
            mat.SetColor("_EmissionColor", finalColor);
        }
    }
}
