/*
 * using System;
using UnityEngine;

public class DeformableCubeController : MonoBehaviour
{
    [Header("Cube")]
    public Transform cubeWarped;        // scale will be applied here (pivot at center)
    public bool isStatic = true;        // TODO: set from your cubeCenterWorld static detector

    [Header("Hands")]
    public PinchProvider leftPinch;
    public PinchProvider rightPinch;

    [Header("Handle Detection")]
    public LayerMask handleLayer;
    public float detectRadius = 0.025f;     // 2.5cm
    public float breakDistance = 0.06f;     // if pinch point goes too far from latched handle, release

    [Header("Scale Limits")]
    public Vector3 minScale = new Vector3(0.2f, 0.2f, 0.2f);
    public Vector3 maxScale = new Vector3(5f, 5f, 5f);

    public bool IsDeforming => _mode != Mode.None;

    /// <summary>
    /// 変形が終了した瞬間に呼ばれる（ピンチ解除 / 条件外れで停止した瞬間）
    /// </summary>
    public event Action<Vector3> OnDeformEnd;

    enum Mode { None, AxisX, AxisY, AxisZ, Uniform }
    Mode _mode = Mode.None;

    DeformHandle _latchedL, _latchedR;
    Vector3 _scale0;
    float _d0;

    void Reset()
    {
        cubeWarped = transform;
    }

    void Update()
    {
        if (cubeWarped == null || leftPinch == null || rightPinch == null) return;

        // If not static, don't start deformation (but allow continuing if already deforming? here: stop)
        if (!isStatic && _mode != Mode.None)
        {
            ResetDeform();
            return;
        }

        // Update latching
        UpdateLatchForHand(isLeft: true, leftPinch, ref _latchedL);
        UpdateLatchForHand(isLeft: false, rightPinch, ref _latchedR);

        // Determine/maintain mode
        if (_mode == Mode.None)
        {
            TryBeginDeform();
        }
        else
        {
            // If either pinch released, stop
            if (!leftPinch.IsPinching || !rightPinch.IsPinching)
            {
                ResetDeform();
                return;
            }

            // If pinches drift too far from their latched handles, stop
            if (!IsNearLatched(leftPinch.PinchPosWorld, _latchedL) ||
                !IsNearLatched(rightPinch.PinchPosWorld, _latchedR))
            {
                ResetDeform();
                return;
            }

            ApplyDeform();
        }
    }

    void UpdateLatchForHand(bool isLeft, PinchProvider pinch, ref DeformHandle latched)
    {
        if (!pinch.IsPinching)
        {
            latched = null;
            return;
        }

        // Start latching only if static and not currently deforming
        if (!isStatic && _mode == Mode.None)
        {
            latched = null;
            return;
        }

        // If already latched and still near, keep it
        if (latched != null && IsNearLatched(pinch.PinchPosWorld, latched))
            return;

        // Otherwise find nearest handle around pinch point
        latched = FindNearestHandle(pinch.PinchPosWorld);
    }

    bool IsNearLatched(Vector3 pinchPos, DeformHandle h)
    {
        if (h == null) return false;
        float d = Vector3.Distance(pinchPos, h.transform.position);
        return d <= breakDistance;
    }

    DeformHandle FindNearestHandle(Vector3 pos)
    {
        Collider[] hits = Physics.OverlapSphere(pos, detectRadius, handleLayer, QueryTriggerInteraction.Collide);
        if (hits == null || hits.Length == 0) return null;

        float best = float.PositiveInfinity;
        DeformHandle bestH = null;
        foreach (var c in hits)
        {
            var h = c.GetComponentInParent<DeformHandle>();
            if (h == null) continue;
            float d = Vector3.Distance(pos, h.transform.position);
            if (d < best)
            {
                best = d;
                bestH = h;
            }
        }
        return bestH;
    }

    void TryBeginDeform()
    {
        if (!isStatic) return;
        if (_latchedL == null || _latchedR == null) return;

        // Both hands must be pinching
        if (!leftPinch.IsPinching || !rightPinch.IsPinching) return;

        // Decide mode from handle pair
        if (IsOppositeFacePair(_latchedL, _latchedR, DeformHandle.Axis.X)) { Begin(Mode.AxisX); return; }
        if (IsOppositeFacePair(_latchedL, _latchedR, DeformHandle.Axis.Y)) { Begin(Mode.AxisY); return; }
        if (IsOppositeFacePair(_latchedL, _latchedR, DeformHandle.Axis.Z)) { Begin(Mode.AxisZ); return; }

        if (IsOppositeCornerPair(_latchedL, _latchedR)) { Begin(Mode.Uniform); return; }
    }

    void Begin(Mode m)
    {
        _mode = m;
        _scale0 = cubeWarped.localScale;

        // store initial distance along axis / euclidean
        _d0 = MeasureDistanceForMode(m);
        if (_d0 < 1e-4f)
        {
            ResetDeform();
            return;
        }
    }

    float MeasureDistanceForMode(Mode m)
    {
        Vector3 pL = leftPinch.PinchPosWorld;
        Vector3 pR = rightPinch.PinchPosWorld;

        if (m == Mode.Uniform)
        {
            return Vector3.Distance(pL, pR);
        }

        Vector3 axis = GetWorldAxis(m);
        float d = Mathf.Abs(Vector3.Dot(pR - pL, axis));
        return d;
    }

    Vector3 GetWorldAxis(Mode m)
    {
        // axis of the cube (world direction)
        switch (m)
        {
            case Mode.AxisX: return cubeWarped.right.normalized;
            case Mode.AxisY: return cubeWarped.up.normalized;
            case Mode.AxisZ: return cubeWarped.forward.normalized;
            default: return Vector3.right;
        }
    }

    void ApplyDeform()
    {
        float d = MeasureDistanceForMode(_mode);
        float ratio = d / _d0;

        Vector3 s = _scale0;

        switch (_mode)
        {
            case Mode.AxisX: s = new Vector3(_scale0.x * ratio, _scale0.y, _scale0.z); break;
            case Mode.AxisY: s = new Vector3(_scale0.x, _scale0.y * ratio, _scale0.z); break;
            case Mode.AxisZ: s = new Vector3(_scale0.x, _scale0.y, _scale0.z * ratio); break;
            case Mode.Uniform:
                float u = ratio;
                s = new Vector3(_scale0.x * u, _scale0.y * u, _scale0.z * u);
                break;
        }

        // Clamp
        s = new Vector3(
            Mathf.Clamp(s.x, minScale.x, maxScale.x),
            Mathf.Clamp(s.y, minScale.y, maxScale.y),
            Mathf.Clamp(s.z, minScale.z, maxScale.z)
        );

        cubeWarped.localScale = s;
    }

    void ResetDeform()
    {
        bool wasDeforming = (_mode != Mode.None);

        _mode = Mode.None;
        _scale0 = Vector3.one;
        _d0 = 0f;

        _latchedL = null;
        _latchedR = null;

        if (wasDeforming)
        {
            // 終了通知：最終スケールを渡す
            OnDeformEnd?.Invoke(cubeWarped != null ? cubeWarped.localScale : Vector3.one);
        }
    }

    bool IsOppositeFacePair(DeformHandle a, DeformHandle b, DeformHandle.Axis axis)
    {
        if (a == null || b == null) return false;
        if (a.kind != DeformHandle.Kind.Face || b.kind != DeformHandle.Kind.Face) return false;
        if (a.faceAxis != axis || b.faceAxis != axis) return false;
        return a.faceSign == -b.faceSign;
    }

    bool IsOppositeCornerPair(DeformHandle a, DeformHandle b)
    {
        if (a == null || b == null) return false;
        if (a.kind != DeformHandle.Kind.Corner || b.kind != DeformHandle.Kind.Corner) return false;

        // opposite if all signs flipped
        Vector3Int sa = a.cornerSign;
        Vector3Int sb = b.cornerSign;
        return (sa.x == -sb.x) && (sa.y == -sb.y) && (sa.z == -sb.z);
    }
}


*/


/*
using System;
using System.Collections.Generic;
using UnityEngine;

public class DeformableCubeController_Pre: MonoBehaviour
{
    [Header("Cube")]
    public Transform cubeWarped;        // scale will be applied here (pivot at center)
    public bool isStatic = true;        // 静置判定（開始条件にだけ使う）

    [Header("Hands")]
    public PinchProvider leftPinch;
    public PinchProvider rightPinch;

    [Header("Handle Detection")]
    public LayerMask handleLayer;
    public float detectRadius = 0.025f;     // ピンチ中のラッチ探索用（OverlapSphere）
    public float breakDistance = 0.06f;     // ピンチ点がラッチから離れたら解除

    [Header("Hover (Grabbable)")]
    [Tooltip("手が近づいたら Grabbable にする半径。まずは detectRadius より少し大きめ推奨")]
    public float hoverRadius = 0.05f;

    [Header("Scale Limits")]
    public Vector3 minScale = new Vector3(0.2f, 0.2f, 0.2f);
    public Vector3 maxScale = new Vector3(5f, 5f, 5f);

    [Header("Handle Cache (optional)")]
    [Tooltip("空なら自動でこのオブジェクト配下から DeformHandle を集める")]
    public DeformHandle[] allHandles;

    public bool IsDeforming => _mode != Mode.None;

    /// <summary>変形が終了した瞬間に呼ばれる（ピンチ解除 / 条件外れで停止した瞬間）</summary>
    public event Action<Vector3> OnDeformEnd;

    enum Mode { None, AxisX, AxisY, AxisZ, Uniform }
    Mode _mode = Mode.None;

    DeformHandle _latchedL, _latchedR;
    Vector3 _scale0;
    float _d0;

    readonly HashSet<DeformHandle> _hovered = new HashSet<DeformHandle>();

    void Reset()
    {
        cubeWarped = transform;
    }

    void Awake()
    {
        CacheHandlesIfNeeded();
    }

    void OnValidate()
    {
        CacheHandlesIfNeeded();
        if (hoverRadius <= 0f) hoverRadius = detectRadius;
    }

    void CacheHandlesIfNeeded()
    {
        if (allHandles != null && allHandles.Length > 0) return;
        allHandles = GetComponentsInChildren<DeformHandle>(includeInactive: true);
    }

    void Update()
    {
        if (cubeWarped == null || leftPinch == null || rightPinch == null) return;

        // --- 1) Hover 更新（ピンチしてなくても「近い」を拾う） ---
        UpdateHoveredHandles();

        // --- 2) 変形ロジック本体 ---
        if (!isStatic && _mode != Mode.None)
        {
            ResetDeform();
            UpdateHandleVisuals();
            return;
        }

        // ピンチ中のみラッチ更新（＝掴んでいる状態に入る）
        UpdateLatchForHand(leftPinch, ref _latchedL);
        UpdateLatchForHand(rightPinch, ref _latchedR);

        if (_mode == Mode.None)
        {
            TryBeginDeform();
        }
        else
        {
            if (!leftPinch.IsPinching || !rightPinch.IsPinching)
            {
                ResetDeform();
                UpdateHandleVisuals();
                return;
            }

            if (!IsNearLatched(leftPinch.PinchPosWorld, _latchedL) ||
                !IsNearLatched(rightPinch.PinchPosWorld, _latchedR))
            {
                ResetDeform();
                UpdateHandleVisuals();
                return;
            }

            ApplyDeform();
        }

        // --- 3) 見た目（マテリアル）反映 ---
        UpdateHandleVisuals();
    }

    // --------------------------
    // Hover（Grabbable）検出
    // --------------------------
    void UpdateHoveredHandles()
    {
        _hovered.Clear();

        // PinchPosWorld は IsPinching=false でも “手先の代表点” として使える想定
        AddHoveredAround(leftPinch != null ? leftPinch.PinchPosWorld : Vector3.zero);
        AddHoveredAround(rightPinch != null ? rightPinch.PinchPosWorld : Vector3.zero);
    }

    void AddHoveredAround(Vector3 pos)
    {
        var hits = Physics.OverlapSphere(pos, hoverRadius, handleLayer, QueryTriggerInteraction.Collide);
        if (hits == null) return;

        foreach (var c in hits)
        {
            var h = c.GetComponentInParent<DeformHandle>();
            if (h != null) _hovered.Add(h);
        }
    }

    // --------------------------
    // Latch（Grabbed）検出
    // --------------------------
    void UpdateLatchForHand(PinchProvider pinch, ref DeformHandle latched)
    {
        if (!pinch.IsPinching)
        {
            latched = null;
            return;
        }

        if (!isStatic && _mode == Mode.None)
        {
            latched = null;
            return;
        }

        if (latched != null && IsNearLatched(pinch.PinchPosWorld, latched))
            return;

        latched = FindNearestHandle(pinch.PinchPosWorld);
    }

    bool IsNearLatched(Vector3 pinchPos, DeformHandle h)
    {
        if (h == null) return false;
        return Vector3.Distance(pinchPos, h.transform.position) <= breakDistance;
    }

    DeformHandle FindNearestHandle(Vector3 pos)
    {
        Collider[] hits = Physics.OverlapSphere(pos, detectRadius, handleLayer, QueryTriggerInteraction.Collide);
        if (hits == null || hits.Length == 0) return null;

        float best = float.PositiveInfinity;
        DeformHandle bestH = null;
        foreach (var c in hits)
        {
            var h = c.GetComponentInParent<DeformHandle>();
            if (h == null) continue;
            float d = Vector3.Distance(pos, h.transform.position);
            if (d < best)
            {
                best = d;
                bestH = h;
            }
        }
        return bestH;
    }

    // --------------------------
    // Deform
    // --------------------------
    void TryBeginDeform()
    {
        if (!isStatic) return;
        if (_latchedL == null || _latchedR == null) return;
        if (!leftPinch.IsPinching || !rightPinch.IsPinching) return;

        if (IsOppositeFacePair(_latchedL, _latchedR, DeformHandle.Axis.X)) { Begin(Mode.AxisX); return; }
        if (IsOppositeFacePair(_latchedL, _latchedR, DeformHandle.Axis.Y)) { Begin(Mode.AxisY); return; }
        if (IsOppositeFacePair(_latchedL, _latchedR, DeformHandle.Axis.Z)) { Begin(Mode.AxisZ); return; }

        if (IsOppositeCornerPair(_latchedL, _latchedR)) { Begin(Mode.Uniform); return; }
    }

    void Begin(Mode m)
    {
        _mode = m;
        _scale0 = cubeWarped.localScale;

        _d0 = MeasureDistanceForMode(m);
        if (_d0 < 1e-4f)
        {
            ResetDeform();
            return;
        }
    }

    float MeasureDistanceForMode(Mode m)
    {
        Vector3 pL = leftPinch.PinchPosWorld;
        Vector3 pR = rightPinch.PinchPosWorld;

        if (m == Mode.Uniform)
            return Vector3.Distance(pL, pR);

        Vector3 axis = GetWorldAxis(m);
        return Mathf.Abs(Vector3.Dot(pR - pL, axis));
    }

    Vector3 GetWorldAxis(Mode m)
    {
        switch (m)
        {
            case Mode.AxisX: return cubeWarped.right.normalized;
            case Mode.AxisY: return cubeWarped.up.normalized;
            case Mode.AxisZ: return cubeWarped.forward.normalized;
            default: return Vector3.right;
        }
    }

    void ApplyDeform()
    {
        float d = MeasureDistanceForMode(_mode);
        float ratio = d / _d0;

        Vector3 s = _scale0;

        switch (_mode)
        {
            case Mode.AxisX: s = new Vector3(_scale0.x * ratio, _scale0.y, _scale0.z); break;
            case Mode.AxisY: s = new Vector3(_scale0.x, _scale0.y * ratio, _scale0.z); break;
            case Mode.AxisZ: s = new Vector3(_scale0.x, _scale0.y, _scale0.z * ratio); break;
            case Mode.Uniform: s = _scale0 * ratio; break;
        }

        s = new Vector3(
            Mathf.Clamp(s.x, minScale.x, maxScale.x),
            Mathf.Clamp(s.y, minScale.y, maxScale.y),
            Mathf.Clamp(s.z, minScale.z, maxScale.z)
        );

        cubeWarped.localScale = s;
    }

    void ResetDeform()
    {
        bool wasDeforming = (_mode != Mode.None);

        _mode = Mode.None;
        _scale0 = Vector3.one;
        _d0 = 0f;

        _latchedL = null;
        _latchedR = null;

        if (wasDeforming)
            OnDeformEnd?.Invoke(cubeWarped != null ? cubeWarped.localScale : Vector3.one);
    }

    bool IsOppositeFacePair(DeformHandle a, DeformHandle b, DeformHandle.Axis axis)
    {
        if (a == null || b == null) return false;
        if (a.kind != DeformHandle.Kind.Face || b.kind != DeformHandle.Kind.Face) return false;
        if (a.faceAxis != axis || b.faceAxis != axis) return false;
        return a.faceSign == -b.faceSign;
    }

    bool IsOppositeCornerPair(DeformHandle a, DeformHandle b)
    {
        if (a == null || b == null) return false;
        if (a.kind != DeformHandle.Kind.Corner || b.kind != DeformHandle.Kind.Corner) return false;

        Vector3Int sa = a.cornerSign;
        Vector3Int sb = b.cornerSign;
        return (sa.x == -sb.x) && (sa.y == -sb.y) && (sa.z == -sb.z);
    }

    // --------------------------
    // Visuals（3状態）
    // --------------------------
    void UpdateHandleVisuals()
    {
        if (allHandles == null) return;

        // 1) まず全て Standby
        for (int i = 0; i < allHandles.Length; i++)
        {
            var h = allHandles[i];
            if (h == null) continue;
            h.SetVisualState(DeformHandle.VisualState.Standby);
        }

        // 2) Hoverしているものを Grabbable
        foreach (var h in _hovered)
        {
            if (h == null) continue;
            h.SetVisualState(DeformHandle.VisualState.Grabbable);
        }

        // 3) 掴んでいる（ピンチでラッチ中／変形中）は Grabbed（最優先）
        if (_latchedL != null && leftPinch.IsPinching)
            _latchedL.SetVisualState(DeformHandle.VisualState.Grabbed);

        if (_latchedR != null && rightPinch.IsPinching)
            _latchedR.SetVisualState(DeformHandle.VisualState.Grabbed);
    }
}
*/

using System;
using System.Collections.Generic;
using UnityEngine;

public class DeformableCubeController : MonoBehaviour
{
    [Header("Cube")]
    public Transform cubeWarped;
    public bool isStatic = true;

    [Header("Hands")]
    public PinchProvider leftPinch;
    public PinchProvider rightPinch;

    [Header("Handle Detection")]
    public LayerMask handleLayer;
    public float detectRadius = 0.025f;
    public float breakDistance = 0.06f;

    [Tooltip("true のとき、一度ラッチしたハンドルはその手のピンチを離すまで維持する")]
    public bool keepLatchedUntilRelease = true;

    [Header("Hover (Grabbable)")]
    public float hoverRadius = 0.05f;

    [Header("Scale Limits")]
    public Vector3 minScale = new Vector3(0.2f, 0.2f, 0.2f);
    public Vector3 maxScale = new Vector3(5f, 5f, 5f);

    [Header("Handle Cache (optional)")]
    [Tooltip("空なら自動でこのオブジェクト配下から DeformHandle を集める")]
    public DeformHandle[] allHandles;

    public bool IsDeforming => _mode != Mode.None;

    public event Action<Vector3> OnDeformEnd;

    enum Mode { None, AxisX, AxisY, AxisZ, Uniform }
    Mode _mode = Mode.None;

    DeformHandle _latchedL, _latchedR;
    Vector3 _scale0;
    float _d0;
    bool _pendingBaseline = false;

    readonly HashSet<DeformHandle> _hovered = new HashSet<DeformHandle>();
    readonly HashSet<DeformHandle> _ownedHandleSet = new HashSet<DeformHandle>();

    void Reset()
    {
        cubeWarped = transform;
    }

    void Awake()
    {
        CacheHandlesIfNeeded();
        RebuildOwnedHandleSet();
    }

    void OnValidate()
    {
        CacheHandlesIfNeeded();
        RebuildOwnedHandleSet();

        if (hoverRadius <= 0f) hoverRadius = detectRadius;
        if (breakDistance < detectRadius) breakDistance = detectRadius;
    }

    void CacheHandlesIfNeeded()
    {
        if (allHandles == null || allHandles.Length == 0)
        {
            allHandles = GetComponentsInChildren<DeformHandle>(includeInactive: true);
        }
    }

    void RebuildOwnedHandleSet()
    {
        _ownedHandleSet.Clear();

        if (allHandles == null) return;

        for (int i = 0; i < allHandles.Length; i++)
        {
            var h = allHandles[i];
            if (h != null) _ownedHandleSet.Add(h);
        }
    }

    bool IsOwnedHandle(DeformHandle h)
    {
        return h != null && _ownedHandleSet.Contains(h);
    }

    void Update()
    {
        if (cubeWarped == null || leftPinch == null || rightPinch == null) return;

        UpdateHoveredHandles();

        if (!isStatic && _mode != Mode.None)
        {
            ResetDeform();
            UpdateHandleVisuals();
            return;
        }

        UpdateLatchForHand(leftPinch, ref _latchedL);
        UpdateLatchForHand(rightPinch, ref _latchedR);

        if (_mode == Mode.None)
        {
            TryBeginDeform();
        }
        else
        {
            if (!leftPinch.IsPinching || !rightPinch.IsPinching)
            {
                ResetDeform();
                UpdateHandleVisuals();
                return;
            }

            if (!keepLatchedUntilRelease)
            {
                if (!IsNearLatched(leftPinch.PinchPosWorld, _latchedL) ||
                    !IsNearLatched(rightPinch.PinchPosWorld, _latchedR))
                {
                    ResetDeform();
                    UpdateHandleVisuals();
                    return;
                }
            }

            ApplyDeform();
        }

        UpdateHandleVisuals();
    }

    // --------------------------
    // Hover
    // --------------------------
    void UpdateHoveredHandles()
    {
        _hovered.Clear();

        if (leftPinch != null) AddHoveredAround(leftPinch.PinchPosWorld);
        if (rightPinch != null) AddHoveredAround(rightPinch.PinchPosWorld);
    }

    void AddHoveredAround(Vector3 pos)
    {
        var hits = Physics.OverlapSphere(pos, hoverRadius, handleLayer, QueryTriggerInteraction.Collide);
        if (hits == null) return;

        foreach (var c in hits)
        {
            var h = c.GetComponentInParent<DeformHandle>();
            if (IsOwnedHandle(h))
            {
                _hovered.Add(h);
            }
        }
    }

    // --------------------------
    // Latch
    // --------------------------
    void UpdateLatchForHand(PinchProvider pinch, ref DeformHandle latched)
    {
        if (!pinch.IsPinching)
        {
            latched = null;
            return;
        }

        if (!isStatic && _mode == Mode.None)
        {
            latched = null;
            return;
        }

        if (keepLatchedUntilRelease && latched != null && IsOwnedHandle(latched))
        {
            return;
        }

        if (!keepLatchedUntilRelease && latched != null && IsOwnedHandle(latched) && IsNearLatched(pinch.PinchPosWorld, latched))
        {
            return;
        }

        latched = FindNearestOwnedHandle(pinch.PinchPosWorld);
    }

    bool IsNearLatched(Vector3 pinchPos, DeformHandle h)
    {
        if (!IsOwnedHandle(h)) return false;
        return Vector3.Distance(pinchPos, h.transform.position) <= breakDistance;
    }

    DeformHandle FindNearestOwnedHandle(Vector3 pos)
    {
        Collider[] hits = Physics.OverlapSphere(pos, detectRadius, handleLayer, QueryTriggerInteraction.Collide);
        if (hits == null || hits.Length == 0) return null;

        float best = float.PositiveInfinity;
        DeformHandle bestH = null;

        foreach (var c in hits)
        {
            var h = c.GetComponentInParent<DeformHandle>();
            if (!IsOwnedHandle(h)) continue;

            float d = Vector3.Distance(pos, h.transform.position);
            if (d < best)
            {
                best = d;
                bestH = h;
            }
        }

        return bestH;
    }

    // --------------------------
    // Deform
    // --------------------------
    void TryBeginDeform()
    {
        if (!isStatic) return;
        if (_latchedL == null || _latchedR == null) return;
        if (!leftPinch.IsPinching || !rightPinch.IsPinching) return;

        if (IsOppositeFacePair(_latchedL, _latchedR, DeformHandle.Axis.X)) { Begin(Mode.AxisX); return; }
        if (IsOppositeFacePair(_latchedL, _latchedR, DeformHandle.Axis.Y)) { Begin(Mode.AxisY); return; }
        if (IsOppositeFacePair(_latchedL, _latchedR, DeformHandle.Axis.Z)) { Begin(Mode.AxisZ); return; }

        if (IsOppositeCornerPair(_latchedL, _latchedR) || IsFaceDiagonalCornerPair(_latchedL, _latchedR))
        {
            Begin(Mode.Uniform);
            return;
        }
    }

    void Begin(Mode m)
    {
        _mode = m;
        _scale0 = cubeWarped.localScale;

        _d0 = MeasureDistanceForMode(m);
        if (_d0 < 1e-4f)
        {
            ResetDeform();
            return;
        }

        _pendingBaseline = true;
    }

    float MeasureDistanceForMode(Mode m)
    {
        Vector3 pL = leftPinch.PinchPosWorld;
        Vector3 pR = rightPinch.PinchPosWorld;

        if (m == Mode.Uniform)
            return Vector3.Distance(pL, pR);

        Vector3 axis = GetWorldAxis(m);
        return Mathf.Abs(Vector3.Dot(pR - pL, axis));
    }

    Vector3 GetWorldAxis(Mode m)
    {
        switch (m)
        {
            case Mode.AxisX: return cubeWarped.right.normalized;
            case Mode.AxisY: return cubeWarped.up.normalized;
            case Mode.AxisZ: return cubeWarped.forward.normalized;
            default: return Vector3.right;
        }
    }

    void ApplyDeform()
    {
        float d = MeasureDistanceForMode(_mode);

        if (_pendingBaseline)
        {
            _d0 = Mathf.Max(d, 1e-4f);
            _pendingBaseline = false;
            cubeWarped.localScale = _scale0;
            return;
        }

        float ratio = d / _d0;
        Vector3 s = _scale0;

        switch (_mode)
        {
            case Mode.AxisX:
                s = new Vector3(_scale0.x * ratio, _scale0.y, _scale0.z);
                break;
            case Mode.AxisY:
                s = new Vector3(_scale0.x, _scale0.y * ratio, _scale0.z);
                break;
            case Mode.AxisZ:
                s = new Vector3(_scale0.x, _scale0.y, _scale0.z * ratio);
                break;
            case Mode.Uniform:
                s = _scale0 * ratio;
                break;
        }

        s = new Vector3(
            Mathf.Clamp(s.x, minScale.x, maxScale.x),
            Mathf.Clamp(s.y, minScale.y, maxScale.y),
            Mathf.Clamp(s.z, minScale.z, maxScale.z)
        );

        cubeWarped.localScale = s;
    }

    void ResetDeform()
    {
        bool wasDeforming = (_mode != Mode.None);

        _mode = Mode.None;
        _scale0 = Vector3.one;
        _d0 = 0f;
        _pendingBaseline = false;

        _latchedL = null;
        _latchedR = null;

        if (wasDeforming)
            OnDeformEnd?.Invoke(cubeWarped != null ? cubeWarped.localScale : Vector3.one);
    }

    bool IsOppositeFacePair(DeformHandle a, DeformHandle b, DeformHandle.Axis axis)
    {
        if (a == null || b == null) return false;
        if (a.kind != DeformHandle.Kind.Face || b.kind != DeformHandle.Kind.Face) return false;
        if (a.faceAxis != axis || b.faceAxis != axis) return false;
        return a.faceSign == -b.faceSign;
    }

    bool IsOppositeCornerPair(DeformHandle a, DeformHandle b)
    {
        if (a == null || b == null) return false;
        if (a.kind != DeformHandle.Kind.Corner || b.kind != DeformHandle.Kind.Corner) return false;

        Vector3Int sa = a.cornerSign;
        Vector3Int sb = b.cornerSign;
        return (sa.x == -sb.x) && (sa.y == -sb.y) && (sa.z == -sb.z);
    }

    bool IsFaceDiagonalCornerPair(DeformHandle a, DeformHandle b)
    {
        if (a == null || b == null) return false;
        if (a.kind != DeformHandle.Kind.Corner || b.kind != DeformHandle.Kind.Corner) return false;

        Vector3Int sa = a.cornerSign;
        Vector3Int sb = b.cornerSign;

        bool sameX = sa.x == sb.x;
        bool sameY = sa.y == sb.y;
        bool sameZ = sa.z == sb.z;

        int sameCount = (sameX ? 1 : 0) + (sameY ? 1 : 0) + (sameZ ? 1 : 0);
        if (sameCount != 1) return false;

        if (!sameX && sa.x != -sb.x) return false;
        if (!sameY && sa.y != -sb.y) return false;
        if (!sameZ && sa.z != -sb.z) return false;

        return true;
    }

    // --------------------------
    // Visuals
    // --------------------------
    void UpdateHandleVisuals()
    {
        if (allHandles == null) return;

        for (int i = 0; i < allHandles.Length; i++)
        {
            var h = allHandles[i];
            if (h == null) continue;
            h.SetVisualState(DeformHandle.VisualState.Standby);
        }

        foreach (var h in _hovered)
        {
            if (h == null) continue;
            h.SetVisualState(DeformHandle.VisualState.Grabbable);
        }

        if (_latchedL != null && leftPinch.IsPinching)
            _latchedL.SetVisualState(DeformHandle.VisualState.Grabbed);

        if (_latchedR != null && rightPinch.IsPinching)
            _latchedR.SetVisualState(DeformHandle.VisualState.Grabbed);
    }
}