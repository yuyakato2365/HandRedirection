using UnityEngine;

public class GhostPlacementFeedback : MonoBehaviour
{
    public enum MatchState
    {
        Default,
        Near,
        Success
    }

    [Header("References")]
    [SerializeField] private Transform targetObject;

    [Tooltip("targetObject の絶対座標や親拡大率計算に使う親。未指定なら targetObject.parent を使う")]
    [SerializeField] private Transform targetParentOverride;

    [Tooltip("ゴースト側の絶対座標や親拡大率計算に使う親。未指定なら transform.parent を使う")]
    [SerializeField] private Transform ghostParentOverride;

    [Tooltip("空なら自動で Renderer を拾う（自分 or 子）")]
    [SerializeField] private Renderer[] targetRenderers;

    [Header("Thresholds")]
    [SerializeField] private float nearPositionThreshold = 0.15f;
    [SerializeField] private float successPositionThreshold = 0.05f;

    [SerializeField] private float nearScaleThreshold = 0.20f;
    [SerializeField] private float successScaleThreshold = 0.08f;

    [Header("Comparison Options")]
    [Tooltip("true のときは親から絶対座標を計算して比較する")]
    [SerializeField] private bool useAbsoluteWorldPosition = true;

    [Tooltip("true のときは親の『拡大率』を使って実効スケールを計算する")]
    [SerializeField] private bool useParentExpansionRatioScale = true;

    [Header("Base Scale Capture")]
    [Tooltip("Start 時に親の基準スケールを自動記録する")]
    [SerializeField] private bool captureParentBaseScaleOnStart = true;

    [Header("Visual (Material Swap)")]
    [SerializeField] private Material defaultMaterial;
    [SerializeField] private Material nearMaterial;
    [SerializeField] private Material successMaterial;

    [Header("Debug (Read Only)")]
    [SerializeField] private Vector3 currentTargetPosition;
    [SerializeField] private Vector3 currentGhostPosition;
    [SerializeField] private Vector3 currentTargetScale;
    [SerializeField] private Vector3 currentGhostScale;

    [SerializeField] private Vector3 targetParentCurrentScale = Vector3.one;
    [SerializeField] private Vector3 ghostParentCurrentScale = Vector3.one;
    [SerializeField] private Vector3 targetParentBaseScale = Vector3.one;
    [SerializeField] private Vector3 ghostParentBaseScale = Vector3.one;
    [SerializeField] private Vector3 targetParentExpansionRatio = Vector3.one;
    [SerializeField] private Vector3 ghostParentExpansionRatio = Vector3.one;

    public MatchState CurrentState { get; private set; } = MatchState.Default;
    public float CurrentPositionError { get; private set; }
    public float CurrentScaleError { get; private set; }

    private bool hasCapturedTargetParentBaseScale = false;
    private bool hasCapturedGhostParentBaseScale = false;

    private void Awake()
    {
        AutoAssignRenderersIfNeeded();
        Apply(CurrentState);
    }

    private void Start()
    {
        if (captureParentBaseScaleOnStart)
        {
            CaptureParentBaseScaleNow();
        }
    }

    private void OnValidate()
    {
        AutoAssignRenderersIfNeeded();
    }

    private void LateUpdate()
    {
        EvaluateMatch();
    }

    [ContextMenu("Capture Parent Base Scale Now")]
    public void CaptureParentBaseScaleNow()
    {
        Transform targetParent = GetParentToUse(targetObject, targetParentOverride);
        if (targetParent != null)
        {
            targetParentBaseScale = targetParent.lossyScale;
            hasCapturedTargetParentBaseScale = true;
        }
        else
        {
            targetParentBaseScale = Vector3.one;
            hasCapturedTargetParentBaseScale = false;
        }

        Transform ghostParent = GetParentToUse(transform, ghostParentOverride);
        if (ghostParent != null)
        {
            ghostParentBaseScale = ghostParent.lossyScale;
            hasCapturedGhostParentBaseScale = true;
        }
        else
        {
            ghostParentBaseScale = Vector3.one;
            hasCapturedGhostParentBaseScale = false;
        }
    }

    public void ForceRecheckNow()
    {
        EvaluateMatch();
    }

    public bool IsNear()
    {
        return CurrentState == MatchState.Near;
    }

    public bool IsSuccess()
    {
        return CurrentState == MatchState.Success;
    }

    private void EvaluateMatch()
    {
        if (targetObject == null) return;

        Vector3 targetPosition = GetComparisonPosition(targetObject, targetParentOverride);
        Vector3 ghostPosition = GetComparisonPosition(transform, ghostParentOverride);

        Vector3 targetScale = GetComparisonScale(targetObject, targetParentOverride, true);
        Vector3 ghostScale = GetComparisonScale(transform, ghostParentOverride, false);

        currentTargetPosition = targetPosition;
        currentGhostPosition = ghostPosition;
        currentTargetScale = targetScale;
        currentGhostScale = ghostScale;

        CurrentPositionError = Vector3.Distance(targetPosition, ghostPosition);
        CurrentScaleError = Vector3.Distance(targetScale, ghostScale);

        bool isSuccess =
            CurrentPositionError <= successPositionThreshold &&
            CurrentScaleError <= successScaleThreshold;

        bool isNear =
            CurrentPositionError <= nearPositionThreshold &&
            CurrentScaleError <= nearScaleThreshold;

        MatchState newState;
        if (isSuccess)
        {
            newState = MatchState.Success;
        }
        else if (isNear)
        {
            newState = MatchState.Near;
        }
        else
        {
            newState = MatchState.Default;
        }

        if (newState != CurrentState)
        {
            CurrentState = newState;
            Apply(CurrentState);
        }
    }

    private Transform GetParentToUse(Transform target, Transform parentOverride)
    {
        if (target == null) return null;
        return parentOverride != null ? parentOverride : target.parent;
    }

    private Vector3 GetComparisonPosition(Transform target, Transform parentOverride)
    {
        if (target == null) return Vector3.zero;

        if (!useAbsoluteWorldPosition)
        {
            return target.localPosition;
        }

        Transform parentToUse = GetParentToUse(target, parentOverride);
        if (parentToUse != null)
        {
            return parentToUse.TransformPoint(target.localPosition);
        }

        return target.position;
    }

    private Vector3 GetComparisonScale(Transform target, Transform parentOverride, bool isTargetSide)
    {
        if (target == null) return Vector3.one;

        if (!useParentExpansionRatioScale)
        {
            return target.localScale;
        }

        Transform parentToUse = GetParentToUse(target, parentOverride);
        if (parentToUse == null)
        {
            if (isTargetSide)
            {
                targetParentCurrentScale = Vector3.one;
                targetParentExpansionRatio = Vector3.one;
            }
            else
            {
                ghostParentCurrentScale = Vector3.one;
                ghostParentExpansionRatio = Vector3.one;
            }

            return target.localScale;
        }

        Vector3 currentParentScale = parentToUse.lossyScale;
        Vector3 baseParentScale;
        bool hasBase;

        if (isTargetSide)
        {
            targetParentCurrentScale = currentParentScale;
            baseParentScale = targetParentBaseScale;
            hasBase = hasCapturedTargetParentBaseScale;
        }
        else
        {
            ghostParentCurrentScale = currentParentScale;
            baseParentScale = ghostParentBaseScale;
            hasBase = hasCapturedGhostParentBaseScale;
        }

        if (!hasBase)
        {
            baseParentScale = currentParentScale;
        }

        Vector3 expansionRatio = DivideVector3Safely(currentParentScale, baseParentScale);

        if (isTargetSide)
        {
            targetParentExpansionRatio = expansionRatio;
        }
        else
        {
            ghostParentExpansionRatio = expansionRatio;
        }

        return Vector3.Scale(target.localScale, expansionRatio);
    }

    private Vector3 DivideVector3Safely(Vector3 numerator, Vector3 denominator)
    {
        return new Vector3(
            SafeDivide(numerator.x, denominator.x),
            SafeDivide(numerator.y, denominator.y),
            SafeDivide(numerator.z, denominator.z)
        );
    }

    private float SafeDivide(float a, float b)
    {
        if (Mathf.Abs(b) < 1e-6f) return 1f;
        return a / b;
    }

    private void AutoAssignRenderersIfNeeded()
    {
        if (targetRenderers != null && targetRenderers.Length > 0) return;

        Renderer r = GetComponent<Renderer>();
        if (r != null)
        {
            targetRenderers = new[] { r };
            return;
        }

        targetRenderers = GetComponentsInChildren<Renderer>(includeInactive: true);
    }

    private void Apply(MatchState state)
    {
        if (targetRenderers == null || targetRenderers.Length == 0) return;

        Material m = defaultMaterial;

        if (state == MatchState.Near && nearMaterial != null)
        {
            m = nearMaterial;
        }

        if (state == MatchState.Success && successMaterial != null)
        {
            m = successMaterial;
        }

        if (m == null) return;

        for (int i = 0; i < targetRenderers.Length; i++)
        {
            Renderer r = targetRenderers[i];
            if (r == null) continue;
            r.sharedMaterial = m;
        }
    }
}