using UnityEngine;

/// <summary>
/// 心拍数に合わせてオブジェクトのスケールを脈動させるコンポーネント
/// PointLightPulseByHeartRate をベースに、「光量」ではなく「大きさ」を変化させる版。
/// </summary>
[RequireComponent(typeof(HeartRateReceiver))]
public class ScalePulseByHeartRate : MonoBehaviour
{
    [Header("Target Settings")]
    [Tooltip("スケールを変化させたい対象。未指定なら自分自身を対象にする。")]
    public Transform target;

    [Header("Scale Settings")]
    [Tooltip("最小時のスケール倍率（target の初期スケールに対する倍率）")]
    public float minScaleMultiplier = 0.8f;

    [Tooltip("最大時のスケール倍率（target の初期スケールに対する倍率）")]
    public float maxScaleMultiplier = 1.2f;

    [Tooltip("脈動の強さ（0〜1あたりが目安。1以上にすると揺れが大きくなる）")]
    public float amplitude = 1.0f;

    private HeartRateReceiver heart;
    private float phase;
    private Vector3 baseScale;   // target の初期スケールを保持

    void Start()
    {
        heart = GetComponent<HeartRateReceiver>();

        if (target == null)
        {
            target = this.transform;
        }

        baseScale = target.localScale;
    }

    void Update()
    {
        // BPM 取得（ゼロ除算防止で最低1にしておく）
        float bpm = Mathf.Max(heart.heartRate, 1.0f);

        // 周波数 [Hz] = BPM / 60
        float pulseFreq = bpm / 60f;

        // 1フレーム分の位相進行量：2π * f * dt
        float delta = Time.deltaTime * pulseFreq * 2f * Mathf.PI;
        phase += delta;

        // 0〜1 の脈動波形に正規化した sin
        float pulse = (Mathf.Sin(phase) * 0.5f + 0.5f) * amplitude;

        // 指定した倍率範囲で補間
        float scaleMul = Mathf.Lerp(minScaleMultiplier, maxScaleMultiplier, pulse);

        // 実際のスケールに反映
        target.localScale = baseScale * scaleMul;
    }
}
