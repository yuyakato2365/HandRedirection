/*
using UnityEngine;

public class DeformHandle : MonoBehaviour
{
    public enum Kind { Face, Corner }
    public enum Axis { X, Y, Z }

    [Header("Handle Type")]
    public Kind kind = Kind.Face;

    // For Face: which axis does this face correspond to?
    public Axis faceAxis = Axis.X;

    // For Face: +1 means +X/+Y/+Z face, -1 means -X/-Y/-Z face
    public int faceSign = +1;

    // For Corner: corner sign (e.g., (+1,+1,+1) or (-1,-1,-1))
    public Vector3Int cornerSign = new Vector3Int(+1, +1, +1);

    void OnValidate()
    {
        faceSign = faceSign >= 0 ? +1 : -1;
        cornerSign = new Vector3Int(
            cornerSign.x >= 0 ? +1 : -1,
            cornerSign.y >= 0 ? +1 : -1,
            cornerSign.z >= 0 ? +1 : -1
        );
    }
}

*/


using UnityEngine;

public class DeformHandle : MonoBehaviour
{
    public enum Kind { Face, Corner }
    public enum Axis { X, Y, Z }

    public enum VisualState
    {
        Standby,     // 掴める場所（待機）
        Grabbable,   // つかみ範囲に入った
        Grabbed      // 掴んでいる（ラッチ中/変形中）
    }

    [Header("Handle Type")]
    public Kind kind = Kind.Face;

    // For Face: which axis does this face correspond to?
    public Axis faceAxis = Axis.X;

    // For Face: +1 means +X/+Y/+Z face, -1 means -X/-Y/-Z face
    public int faceSign = +1;

    // For Corner: corner sign (e.g., (+1,+1,+1) or (-1,-1,-1))
    public Vector3Int cornerSign = new Vector3Int(+1, +1, +1);

    [Header("Visual (Material Swap)")]
    [Tooltip("空なら自動で Renderer を拾う（自分 or 子）")]
    public Renderer[] targetRenderers;

    public Material standbyMaterial;
    public Material grabbableMaterial;
    public Material grabbedMaterial;

    VisualState _state = VisualState.Standby;

    void Awake()
    {
        AutoAssignRenderersIfNeeded();
        Apply(_state);
    }

    void OnValidate()
    {
        faceSign = faceSign >= 0 ? +1 : -1;
        cornerSign = new Vector3Int(
            cornerSign.x >= 0 ? +1 : -1,
            cornerSign.y >= 0 ? +1 : -1,
            cornerSign.z >= 0 ? +1 : -1
        );
        AutoAssignRenderersIfNeeded();
    }

    void AutoAssignRenderersIfNeeded()
    {
        if (targetRenderers != null && targetRenderers.Length > 0) return;

        var r = GetComponent<Renderer>();
        if (r != null)
        {
            targetRenderers = new[] { r };
            return;
        }

        targetRenderers = GetComponentsInChildren<Renderer>(includeInactive: true);
    }

    public void SetVisualState(VisualState s)
    {
        if (_state == s) return;
        _state = s;
        Apply(s);
    }

    void Apply(VisualState s)
    {
        if (targetRenderers == null || targetRenderers.Length == 0) return;

        Material m = standbyMaterial;

        // 優先度：Grabbed > Grabbable > Standby
        if (s == VisualState.Grabbable && grabbableMaterial != null) m = grabbableMaterial;
        if (s == VisualState.Grabbed && grabbedMaterial != null) m = grabbedMaterial;

        if (m == null) return;

        for (int i = 0; i < targetRenderers.Length; i++)
        {
            var r = targetRenderers[i];
            if (r == null) continue;

            // sharedMaterial：余計なインスタンス生成を避ける
            r.sharedMaterial = m;
        }
    }
}
