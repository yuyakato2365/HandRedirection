using UnityEngine;

public class HandSphereSelector : MonoBehaviour
{
    [Header("Hand refs")]
    [SerializeField] private OVRHand leftHand;
    [SerializeField] private OVRHand rightHand;

    [Tooltip("各手のピンチ点（人差し指先に近いTransform）を入れる。無ければ Hand transform を使う。")]
    [SerializeField] private Transform leftPinchPoint;
    [SerializeField] private Transform rightPinchPoint;

    [Header("Sphere visual")]
    [SerializeField] private Transform sphereVisual;

    [Header("Radius limits")]
    [SerializeField] private float radiusMin = 0.05f;
    [SerializeField] private float radiusMax = 1.00f;

    [Header("Confirm / Scan gestures")]
    [Tooltip("片手ピンチ長押しで確定する秒数")]
    [SerializeField] private float holdToConfirmSec = 0.6f;

    [Tooltip("確定後、片手ピンチ長押しでスキャン開始する秒数")]
    [SerializeField] private float holdToScanSec = 0.6f;

    [Header("Smoothing")]
    [SerializeField] private float centerLerp = 15f;
    [SerializeField] private float radiusLerp = 15f;

    // Outputs
    public Vector3 CenterWorld { get; private set; }
    public float Radius { get; private set; } = 0.18f;
    public bool IsConfirmed { get; private set; }
    public bool ScanRequestedThisFrame { get; private set; } // MrScanControllerが拾う

    // internal
    private float holdTimer = 0f;

    void Reset()
    {
        Radius = 0.18f;
    }

    void Update()
    {
        ScanRequestedThisFrame = false;

        if (leftHand == null || rightHand == null || sphereVisual == null) return;

        bool lPinch = leftHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
        bool rPinch = rightHand.GetFingerIsPinching(OVRHand.HandFinger.Index);

        Vector3 lp = GetPinchPos(leftHand, leftPinchPoint);
        Vector3 rp = GetPinchPos(rightHand, rightPinchPoint);

        // 1) Resize（両手ピンチ中）: 半径 = 両ピンチ点距離 / 2
        if (!IsConfirmed && lPinch && rPinch)
        {
            Vector3 targetCenter = (lp + rp) * 0.5f;
            float targetRadius = Vector3.Distance(lp, rp) * 0.5f;
            targetRadius = Mathf.Clamp(targetRadius, radiusMin, radiusMax);

            CenterWorld = Vector3.Lerp(CenterWorld, targetCenter, 1f - Mathf.Exp(-centerLerp * Time.deltaTime));
            Radius = Mathf.Lerp(Radius, targetRadius, 1f - Mathf.Exp(-radiusLerp * Time.deltaTime));

            holdTimer = 0f; // 両手調整中は確定判定しない
        }
        // 2) Aim（片手ピンチ or 無操作）: 中心だけ更新（視線中心より手の方が自然）
        else if (!IsConfirmed)
        {
            // 片手ピンチしてるならその手を中心に。どちらもピンチしてないなら右手位置を仮中心に。
            Vector3 targetCenter =
                rPinch ? rp :
                lPinch ? lp :
                rp;

            CenterWorld = Vector3.Lerp(CenterWorld, targetCenter, 1f - Mathf.Exp(-centerLerp * Time.deltaTime));

            // 半径は両手ピンチ以外は維持（誤爆防止）
            holdTimer = (lPinch ^ rPinch) ? holdTimer + Time.deltaTime : 0f;

            // 3) Confirm（確定）：片手ピンチ長押し
            if ((lPinch ^ rPinch) && holdTimer >= holdToConfirmSec)
            {
                IsConfirmed = true;
                holdTimer = 0f;
            }
        }
        else
        {
            // 4) Confirm後：球は固定。スキャンは片手ピンチ長押しで発火
            bool anyPinch = lPinch || rPinch;
            holdTimer = anyPinch ? holdTimer + Time.deltaTime : 0f;

            if (anyPinch && holdTimer >= holdToScanSec)
            {
                ScanRequestedThisFrame = true;
                holdTimer = 0f;
            }
        }

        // Visual update
        sphereVisual.position = CenterWorld;
        sphereVisual.localScale = Vector3.one * (Radius * 2f);
    }

    public void ResetConfirm()
    {
        IsConfirmed = false;
        holdTimer = 0f;
    }

    private static Vector3 GetPinchPos(OVRHand hand, Transform pinchPointOverride)
    {
        // overrideがあれば最優先
        if (pinchPointOverride != null) return pinchPointOverride.position;

        // OVRHandのtransformは手首寄りなので、最低限のfallbackとして使う
        return hand.transform.position;
    }
}
