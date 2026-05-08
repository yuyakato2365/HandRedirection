using System.Collections.Generic;
using UnityEngine;

public class ExhibitionExperienceResetter : MonoBehaviour
{
    [Header("Scale Reset Targets")]
    public Transform[] explicitScaleTargets;
    public bool autoCollectDeformableTargets = true;
    public bool refreshGhostFeedbackAfterReset = true;
    public bool logReset = true;

    private readonly List<ScaleState> startupScales = new List<ScaleState>();
    private bool hasCapturedStartupState;

    public int CapturedTargetCount => startupScales.Count;

    private void Awake()
    {
        CaptureStartupState();
    }

    public void CaptureStartupState()
    {
        startupScales.Clear();
        HashSet<Transform> seen = new HashSet<Transform>();

        if (explicitScaleTargets != null)
        {
            foreach (Transform target in explicitScaleTargets)
                AddScaleTarget(target, seen);
        }

        if (autoCollectDeformableTargets)
        {
            DeformableCubeController[] deformables = FindObjectsByType<DeformableCubeController>(FindObjectsSortMode.None);
            foreach (DeformableCubeController deformable in deformables)
            {
                if (deformable == null)
                    continue;

                AddScaleTarget(deformable.cubeWarped != null ? deformable.cubeWarped : deformable.transform, seen);
            }
        }

        hasCapturedStartupState = true;

        if (logReset)
            Debug.Log($"[ExhibitionExperienceResetter] Captured {startupScales.Count} startup scale target(s).");
    }

    public int ResetForNextParticipant()
    {
        if (!hasCapturedStartupState)
            CaptureStartupState();

        int restoredCount = 0;
        for (int i = 0; i < startupScales.Count; i++)
        {
            ScaleState state = startupScales[i];
            if (state.Target == null)
                continue;

            state.Target.localScale = state.LocalScale;
            restoredCount++;
        }

        if (refreshGhostFeedbackAfterReset)
        {
            GhostPlacementFeedback[] feedbacks = FindObjectsByType<GhostPlacementFeedback>(FindObjectsSortMode.None);
            foreach (GhostPlacementFeedback feedback in feedbacks)
            {
                if (feedback != null)
                    feedback.ForceRecheckNow();
            }
        }

        if (logReset)
            Debug.Log($"[ExhibitionExperienceResetter] Reset {restoredCount} object scale(s) for next participant.");

        return restoredCount;
    }

    private void AddScaleTarget(Transform target, HashSet<Transform> seen)
    {
        if (target == null || seen.Contains(target))
            return;

        seen.Add(target);
        startupScales.Add(new ScaleState(target, target.localScale));
    }

    private readonly struct ScaleState
    {
        public readonly Transform Target;
        public readonly Vector3 LocalScale;

        public ScaleState(Transform target, Vector3 localScale)
        {
            Target = target;
            LocalScale = localScale;
        }
    }
}
