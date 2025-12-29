using UnityEngine;
using UnityEngine.VFX;

public class VFXAttractorBinder : MonoBehaviour
{
    public VisualEffect vfx;
    public Transform targetTransform;

    [Header("Scaling")]
    [Tooltip("水平方向(XZ)のスケール倍率")]
    public float scaleXZ = 100f;

    [Tooltip("高さ(Y)のスケール倍率")]
    public float scaleY = 100f;

    [Header("Optional: SDF のローカル空間")]
    public Transform sdfRoot;

    static readonly int CenterID = Shader.PropertyToID("attractorCenter");

    void Update()
    {
        if (!vfx || !targetTransform) return;

        // 1. 位置取得
        Vector3 pos = targetTransform.position;

        // 2. SDF ローカル空間に変換（必要な場合）
        if (sdfRoot != null)
        {
            pos = sdfRoot.InverseTransformPoint(pos);
        }

        // 3. XY の倍率を別々に適用
        Vector3 scaledPos = new Vector3(
            pos.x * scaleXZ,
            pos.y * scaleY,
            pos.z * scaleXZ
        );

        // 4. VFX Graph の Center に渡す
        vfx.SetVector3(CenterID, scaledPos);
    }
}
