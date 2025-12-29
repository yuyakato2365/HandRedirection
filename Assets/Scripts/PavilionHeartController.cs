using UnityEngine;

public class PavilionHeartController : MonoBehaviour
{
    [Header("Heart Rate Receiver")]
    public HeartRateReceiver heart;
    // heart.heartRate → BPM
    // heart.heartRange → 0(落ち着き)〜1(緊張)

    [Header("Scene References")]
    public Transform floor;        // 床（板間）
    public Transform roof;         // 屋根
    public Transform pillarsRoot;  // 柱群の親
    public Light[] lanternLights;  // ライト群

    [Header("Floor Breath Settings")]
    public float floorBreathAmplitude = 0.05f; // 床上下の最大揺れ
    float floorBaseY;
    float phase; // 心拍の位相

    [Header("Roof & Pillars Settings")]
    public float roofScaleRange = 0.05f;     // 屋根の縮小/拡大
    public float pillarBendAmount = 0.03f;   // 柱の内側曲がり量

    [Header("Lantern Settings")]
    public float lanternBaseIntensity = 2.0f;
    public float lanternPulseIntensity = 1.0f;

    void Start()
    {
        if (floor != null)
            floorBaseY = floor.localPosition.y;
    }

    void Update()
    {
        if (heart == null) return;

        //------------------------------------------------------
        // 1. 心拍データ取得
        //------------------------------------------------------
        float bpm = heart.heartRate;       // BPM
        float tension = Mathf.Clamp01(heart.heartRange);
        // 0 = リラックス, 1 = 緊張

        //------------------------------------------------------
        // 2. 床を "心拍の鼓動" で上下させる
        //------------------------------------------------------
        if (floor != null)
        {
            // 位相進行：BPMに応じて速くなる
            float omega = bpm / 60f * Mathf.PI * 2f;
            phase += omega * Time.deltaTime;

            // 振幅は緊張で増加
            float amp = floorBreathAmplitude * Mathf.Lerp(0.4f, 1f, tension);

            float dy = Mathf.Sin(phase) * amp;

            var p = floor.localPosition;
            p.y = floorBaseY + dy;
            floor.localPosition = p;
        }

        //------------------------------------------------------
        // 3. 屋根の拡大/縮小（緊張で縮む・落ち着きで広がる）
        //------------------------------------------------------
        if (roof != null)
        {
            // tension = 0 → 広がる、tension = 1 → すぼむ
            float scaleFactor = 1.0f - roofScaleRange * (tension - 0.5f);
            roof.localScale = new Vector3(scaleFactor, 1.0f, scaleFactor);
        }

        //------------------------------------------------------
        // 4. 柱が内側に "しなる"（緊張時）
        //------------------------------------------------------
        if (pillarsRoot != null)
        {
            for (int i = 0; i < pillarsRoot.childCount; i++)
            {
                Transform pillar = pillarsRoot.GetChild(i);
                Vector3 original = pillar.localPosition;

                // 原点方向へ寄せる（建物中心へ縮む）
                Vector3 dirToCenter = -original.normalized;
                float offset = pillarBendAmount * tension;

                pillar.localPosition = original + dirToCenter * offset;
            }
        }

        //------------------------------------------------------
        // 5. ライトの明滅（心拍と緊張に連動）
        //------------------------------------------------------
        if (lanternLights != null)
        {
            float pulse = (Mathf.Sin(phase) + 1f) * 0.5f; // 0〜1

            foreach (var lt in lanternLights)
            {
                if (lt == null) continue;

                float pulseAmp = Mathf.Lerp(0.2f, 1.0f, tension);
                lt.intensity = lanternBaseIntensity
                            + lanternPulseIntensity * pulse * pulseAmp;
            }
        }
    }
}




/*
using UnityEngine;

public class PavilionHeartController : MonoBehaviour
{
    [Header("Heart Rate Receiver")]
    public HeartRateReceiver heart;

    [Header("Scene References")]
    public Transform floor;        // 床（板間）
    public Transform roof;         // 屋根
    public Transform pillarsRoot;  // 柱の親(まだ未設定でもOK)

    public Light[] lanternLights;  // ライト群

    [Header("Floor Settings")]
    public float floorBreathAmplitude = 0.05f; // 上下揺れ[m]
    public float floorStretchAmount = 0.05f; // XZ方向の伸縮量(割合)

    float floorBaseY;
    Vector3 floorBaseScale;
    float phase; // 心拍位相

    [Header("Roof Settings")]
    public float roofScaleRange = 0.05f;   // 平面方向の縮小/拡大
    public float roofTiltAngleDeg = 3.0f;    // 屋根の傾き最大角度

    Quaternion roofBaseRotation;
    Vector3 roofBaseScale;

    [Header("Pillars Settings")]
    public float pillarBendAmount = 0.03f;   // 柱の内側曲がり量

    [Header("Whole Pavilion Twist")]
    public float twistAngleDeg = 2.0f;       // 建物全体のねじれ角度

    Quaternion pavilionBaseRotation;

    [Header("Lantern Settings")]
    public float lanternBaseIntensity = 2.0f;
    public float lanternPulseIntensity = 1.0f;

    void Start()
    {
        if (floor != null)
        {
            floorBaseY = floor.localPosition.y;
            floorBaseScale = floor.localScale;
        }

        if (roof != null)
        {
            roofBaseRotation = roof.localRotation;
            roofBaseScale = roof.localScale;
        }

        pavilionBaseRotation = transform.localRotation;
    }

    void Update()
    {
        if (heart == null) return;

        //--------------------------------------------------
        // 1. 心拍と「緊張度」取得
        //--------------------------------------------------
        float bpm = heart.heartRate > 0 ? heart.heartRate : 60f; // 0なら60bpmで回す
        float tension = Mathf.Clamp01(heart.heartRange);         // 0:リラックス 1:緊張

        // 心拍に同期した位相
        float omega = bpm / 60f * Mathf.PI * 2f;
        phase += omega * Time.deltaTime;

        // 0〜1 のパルス（鼓動）
        float pulse01 = (Mathf.Sin(phase) + 1f) * 0.5f;

        //--------------------------------------------------
        // 2. 床：上下 + 平面伸縮で「鼓動＋変形」
        //--------------------------------------------------
        if (floor != null)
        {
            // 上下揺れ（緊張時ほど振幅UP）
            float amp = floorBreathAmplitude * Mathf.Lerp(0.4f, 1f, tension);
            float dy = Mathf.Sin(phase) * amp;

            var pos = floor.localPosition;
            pos.y = floorBaseY + dy;
            floor.localPosition = pos;

            // XZ方向の伸縮（鼓動に合わせてフワッと広がる）
            float stretch = 1.0f + floorStretchAmount * (pulse01 - 0.5f) * 2.0f;
            floor.localScale = new Vector3(
                floorBaseScale.x * stretch,
                floorBaseScale.y,
                floorBaseScale.z * stretch
            );
        }

        //--------------------------------------------------
        // 3. 屋根：縮む/開く + 傾き（グニャっと変形してる風）
        //--------------------------------------------------
        if (roof != null)
        {
            // 平面スケール：緊張で少しすぼむ、リラックスで開く
            float scaleFactor = 1.0f - roofScaleRange * (tension - 0.5f);
            roof.localScale = new Vector3(
                roofBaseScale.x * scaleFactor,
                roofBaseScale.y,
                roofBaseScale.z * scaleFactor
            );

            // 傾き：心拍に同期＋緊張時ほど大きく
            float tiltAmp = roofTiltAngleDeg * Mathf.Lerp(0.5f, 1.5f, tension);
            float tiltX = Mathf.Sin(phase * 0.5f) * tiltAmp;  // 前後方向
            float tiltZ = Mathf.Cos(phase * 0.4f) * tiltAmp;  // 左右方向

            Quaternion tiltRot =
                Quaternion.Euler(tiltX, 0f, tiltZ);

            roof.localRotation = roofBaseRotation * tiltRot;
        }

        //--------------------------------------------------
        // 4. 柱：緊張時に少し内側へ「しなる」
        //--------------------------------------------------
        if (pillarsRoot != null)
        {
            for (int i = 0; i < pillarsRoot.childCount; i++)
            {
                Transform pillar = pillarsRoot.GetChild(i);

                Vector3 basePos = pillar.localPosition;
                Vector3 dirToCenter = -basePos.normalized; // 原点方向
                float offset = pillarBendAmount * tension;

                pillar.localPosition = basePos + dirToCenter * offset;
            }
        }

        //--------------------------------------------------
        // 5. 建物全体：わずかな「ねじれ」で生き物感
        //--------------------------------------------------
        {
            // pulse01: 0〜1 → -1〜1 の揺れに
            float twist = (pulse01 - 0.5f) * 2.0f * twistAngleDeg;

            // Y軸まわりに軽くねじる
            Quaternion twistRot = Quaternion.Euler(0f, twist, 0f);
            transform.localRotation = pavilionBaseRotation * twistRot;
        }

        //--------------------------------------------------
        // 6. ライト：鼓動＋緊張度で明滅
        //--------------------------------------------------
        if (lanternLights != null && lanternLights.Length > 0)
        {
            foreach (var lt in lanternLights)
            {
                if (lt == null) continue;

                float pulseAmp = Mathf.Lerp(0.2f, 1f, tension);
                lt.intensity = lanternBaseIntensity
                             + lanternPulseIntensity * pulse01 * pulseAmp;
            }
        }
    }
}
*/