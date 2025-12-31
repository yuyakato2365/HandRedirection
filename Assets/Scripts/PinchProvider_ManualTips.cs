using UnityEngine;

public class PinchProvider : MonoBehaviour
{
    [Header("Required")]
    public OVRHand ovrHand;

    [Header("Tip Transforms (assign from hand visual model)")]
    public Transform thumbTip;
    public Transform indexTip;

    [Header("Pinch Settings")]
    [Range(0f, 1f)] public float pinchStrengthThreshold = 0.7f;

    public bool IsPinching { get; private set; }
    public float PinchStrength { get; private set; }
    public Vector3 PinchPosWorld { get; private set; }

    void Reset()
    {
        // 付けたオブジェクトにOVRHandがある想定（君の構成はそう）
        ovrHand = GetComponent<OVRHand>();
    }

    void Update()
    {
        if (ovrHand == null)
        {
            IsPinching = false;
            PinchStrength = 0f;
            return;
        }

        PinchStrength = ovrHand.GetFingerPinchStrength(OVRHand.HandFinger.Index);
        IsPinching = PinchStrength >= pinchStrengthThreshold;

        if (thumbTip != null && indexTip != null)
        {
            PinchPosWorld = 0.5f * (thumbTip.position + indexTip.position);
        }
        else
        {
            // 指先が未設定なら安全に手元近辺にフォールバック
            PinchPosWorld = ovrHand.transform.position;
        }
    }
}
