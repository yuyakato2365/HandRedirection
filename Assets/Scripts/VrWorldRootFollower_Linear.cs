/*
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
*/

using System.Collections.Generic;
using UnityEngine;

public class VrWorldTargetsFollower_Linear : MonoBehaviour
{
    [System.Serializable]
    public class FollowTarget
    {
        [Tooltip("仮想空間内で追従させたいオブジェクト")]
        public Transform targetVR;

        [Tooltip("回転も追従させるか")]
        public bool followRotation = false;

        [HideInInspector] public Vector3 baseWorldPosition;
        [HideInInspector] public Quaternion baseWorldRotation = Quaternion.identity;
        [HideInInspector] public bool hasBasePose = false;
    }

    [Header("HMD / Camera Center")]
    [Tooltip("GoGoInteractionController_NoY2_PiecewiseClampDeform の cameraCenter と同じ Transform を入れる")]
    [SerializeField] private Transform cameraCenter;

    [Header("Targets")]
    [Tooltip("頭移動に対して GoGo と同じ線形写像で動かしたい仮想オブジェクト")]
    [SerializeField] private List<FollowTarget> targets = new List<FollowTarget>();

    [Header("Linear Warp (match GoGo F)")]
    [Tooltip("GoGo の linearK と同じ値を入れる")]
    [SerializeField] private float linearK = 0.0f;

    [Tooltip("GoGo の linearWarpAffectX と揃える")]
    [SerializeField] private bool linearWarpAffectX = true;

    [Tooltip("GoGo の linearWarpAffectY と揃える")]
    [SerializeField] private bool linearWarpAffectY = false;

    [Tooltip("GoGo の linearWarpAffectZ と揃える")]
    [SerializeField] private bool linearWarpAffectZ = true;

    [Header("Frame Option")]
    [Tooltip("GoGo の useYawOnlyFrame と揃える")]
    [SerializeField] private bool useYawOnlyFrame = true;

    [Header("Initialization")]
    [Tooltip("Start 時に各 target の現在位置を基準位置として記録する")]
    [SerializeField] private bool captureBasePoseOnStart = true;

    [Header("Optional Grab Offset")]
    [Tooltip("必要なら、掴み中の手移動オフセットを全 target に追加する")]
    [SerializeField] private bool enableGrabOffset = false;

    [Tooltip("grab offset に Y を含めるか")]
    [SerializeField] private bool grabAffectY = false;

    private bool isGrabbing = false;
    private Transform grabHand;
    private Vector3 grabHandBasePos;

    private void Start()
    {
        ValidateTargets();

        if (cameraCenter == null)
        {
            Debug.LogError("[VrWorldRootFollower_Linear] cameraCenter が未設定です。");
            enabled = false;
            return;
        }

        if (targets.Count == 0)
        {
            Debug.LogError("[VrWorldRootFollower_Linear] targets が空です。");
            enabled = false;
            return;
        }

        if (captureBasePoseOnStart)
        {
            CaptureBasePoseNow();
        }
    }

    private void LateUpdate()
    {
        if (cameraCenter == null) return;

        Vector3 grabOffset = Vector3.zero;
        if (enableGrabOffset && isGrabbing && grabHand != null)
        {
            grabOffset = grabHand.position - grabHandBasePos;
            if (!grabAffectY)
            {
                grabOffset.y = 0f;
            }
        }

        for (int i = 0; i < targets.Count; i++)
        {
            FollowTarget t = targets[i];
            if (t == null || t.targetVR == null || !t.hasBasePose) continue;

            // 基準位置を GoGo と同じ線形写像 F に通す
            Vector3 local = WorldToLocalForWarp(t.baseWorldPosition);
            Vector3 localWarped = LinearWarpLocal(local);
            Vector3 warpedWorld = LocalToWorldForWarp(localWarped);

            t.targetVR.position = warpedWorld + grabOffset;

            if (t.followRotation)
            {
                t.targetVR.rotation = t.baseWorldRotation;
            }
        }
    }

    // =========================
    // Public API
    // =========================

    [ContextMenu("Capture Base Pose Now")]
    public void CaptureBasePoseNow()
    {
        ValidateTargets();

        for (int i = 0; i < targets.Count; i++)
        {
            FollowTarget t = targets[i];
            if (t == null || t.targetVR == null) continue;

            t.baseWorldPosition = t.targetVR.position;
            t.baseWorldRotation = t.targetVR.rotation;
            t.hasBasePose = true;
        }
    }

    [ContextMenu("Reapply Current Pose As Base")]
    public void RebaseNow()
    {
        CaptureBasePoseNow();
    }

    public void BeginGrab(Transform hand)
    {
        if (hand == null) return;

        isGrabbing = true;
        grabHand = hand;
        grabHandBasePos = hand.position;
    }

    public void EndGrab()
    {
        isGrabbing = false;
        grabHand = null;
    }

    public void RebaseGrabNow()
    {
        if (isGrabbing && grabHand != null)
        {
            grabHandBasePos = grabHand.position;
        }
    }

    public void SetLinearK(float value)
    {
        linearK = value;
    }

    public float GetLinearK()
    {
        return linearK;
    }

    // =========================
    // Internal
    // =========================

    private void ValidateTargets()
    {
        targets.RemoveAll(t => t == null || t.targetVR == null);
    }

    private float GetCameraYawDeg()
    {
        Vector3 fwd = cameraCenter.forward;
        fwd.y = 0f;

        if (fwd.sqrMagnitude < 1e-8f)
        {
            return 0f;
        }

        fwd.Normalize();
        return Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
    }

    private Vector3 WorldToLocalForWarp(Vector3 world)
    {
        Vector3 rel = world - cameraCenter.position;

        if (!useYawOnlyFrame)
        {
            return Quaternion.Inverse(cameraCenter.rotation) * rel;
        }

        float yaw = GetCameraYawDeg();
        Quaternion invYaw = Quaternion.Inverse(Quaternion.Euler(0f, yaw, 0f));
        return invYaw * rel;
    }

    private Vector3 LocalToWorldForWarp(Vector3 local)
    {
        if (!useYawOnlyFrame)
        {
            return cameraCenter.position + (cameraCenter.rotation * local);
        }

        float yaw = GetCameraYawDeg();
        Quaternion yawQ = Quaternion.Euler(0f, yaw, 0f);
        return cameraCenter.position + (yawQ * local);
    }

    private Vector3 LinearWarpLocal(Vector3 pLocal)
    {
        float s = 1f + linearK;

        return new Vector3(
            linearWarpAffectX ? pLocal.x * s : pLocal.x,
            linearWarpAffectY ? pLocal.y * s : pLocal.y,
            linearWarpAffectZ ? pLocal.z * s : pLocal.z
        );
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (targets == null) return;

        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i] == null) continue;

            if (targets[i].targetVR == null)
            {
                targets[i].hasBasePose = false;
            }
        }
    }
#endif
}