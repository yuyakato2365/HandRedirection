using System.Collections.Generic;
using UnityEngine;

public class VrWorldTargetsFollower_Linear : MonoBehaviour
{
    [Header("References")]
    [Tooltip("CenterEyeAnchor 等（HMDのTransform）")]
    public Transform hmd;

    [Header("Targets (No Parenting Needed)")]
    [Tooltip("補正移動させたいVR物体（配下にできないので、ここに直接登録）")]
    public List<Transform> targets = new List<Transform>();

    [Header("Mapping Scale")]
    [Tooltip("1.0 = 何もしない, 2.0 = 2倍相当の補正 (あなたの定義に合わせる)")]
    public float scale = 1.5f;

    [Header("Optional: Align on Start")]
    [Tooltip("起動時に realProxy と virtualRef を一致させる（全ターゲットを一括平行移動）")]
    public bool alignOnStart = true;

    [Tooltip("ArUco等で推定した実物体Proxy（今見えている位置）")]
    public Transform realProxy;

    [Tooltip("VR側で実物体に対応させたい基準点（targets内のどれか/または別TransformでもOK）")]
    public Transform virtualRef;

    // --- baseline ---
    private Vector3 hmdBasePos;

    // 各ターゲットの「基準位置」を保持
    private Dictionary<Transform, Vector3> basePos = new Dictionary<Transform, Vector3>();

    // --- grab ---
    private bool isGrabbing = false;
    private Transform grabHand;
    private Vector3 grabHandBasePos;

    private void Start()
    {
        if (hmd == null)
        {
            Debug.LogError("[VrWorldTargetsFollower_Linear] hmd が未設定です。");
            enabled = false;
            return;
        }

        // targets の妥当性チェック
        targets.RemoveAll(t => t == null);

        if (targets.Count == 0)
        {
            Debug.LogError("[VrWorldTargetsFollower_Linear] targets が空です。動かしたいVR物体を登録してください。");
            enabled = false;
            return;
        }

        // 起動時の整列（任意）※Yは動かさない
        if (alignOnStart && realProxy != null && virtualRef != null)
        {
            Vector3 delta = realProxy.position - virtualRef.position;
            delta.y = 0f; // ★Y固定
            ApplyDeltaToAllTargets(delta);
        }

        // baseline 記録
        hmdBasePos = hmd.position;
        CacheBasePositions();
    }

    private void LateUpdate()
    {
        float s = Mathf.Max(1f, scale);

        // 重心固定補正： (s-1)(H0 - H)
        Vector3 comp = (s - 1f) * (hmdBasePos - hmd.position);

        // 掴み中の追加オフセット
        Vector3 grabOffset = Vector3.zero;
        if (isGrabbing && grabHand != null)
        {
            grabOffset = grabHand.position - grabHandBasePos;
        }

        Vector3 totalOffset = comp + grabOffset;
        totalOffset.y = 0f; // ★Y固定（HMD上下移動・掴み上下移動も無視）

        // 全ターゲットを “基準位置 + オフセット” に更新（Yは基準位置のまま）
        for (int i = 0; i < targets.Count; i++)
        {
            Transform t = targets[i];
            if (t == null) continue;

            if (!basePos.TryGetValue(t, out Vector3 p0))
            {
                // 途中で targets が差し替わった等の保険
                p0 = t.position;
                basePos[t] = p0;
            }

            t.position = p0 + totalOffset;
        }
    }

    // -------------------------
    // 外部（掴み検知側）から呼ぶ
    // -------------------------

    public void BeginGrab(Transform hand)
    {
        if (hand == null) return;

        isGrabbing = true;
        grabHand = hand;
        grabHandBasePos = hand.position;
    }

    public void EndGrab()
    {
        // いまの見た目位置を新しい基準に焼き込む
        hmdBasePos = hmd.position;
        CacheBasePositions();

        isGrabbing = false;
        grabHand = null;
    }

    /// <summary>
    /// 途中で scale を変えた/位置が安定した瞬間に、ジャンプを減らしたいときに呼ぶ
    /// </summary>
    public void RebaseNow()
    {
        hmdBasePos = hmd.position;
        CacheBasePositions();

        if (isGrabbing && grabHand != null)
        {
            grabHandBasePos = grabHand.position;
        }
    }

    // -------------------------
    // 内部ユーティリティ
    // -------------------------

    private void CacheBasePositions()
    {
        basePos.Clear();
        for (int i = 0; i < targets.Count; i++)
        {
            Transform t = targets[i];
            if (t == null) continue;
            basePos[t] = t.position;
        }
    }

    private void ApplyDeltaToAllTargets(Vector3 delta)
    {
        // delta.y は呼び出し側で 0 にしている想定
        for (int i = 0; i < targets.Count; i++)
        {
            Transform t = targets[i];
            if (t == null) continue;
            t.position += delta;
        }
    }
}
