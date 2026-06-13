using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using UnityEngine;

public class ExhibitionExperienceResetter : MonoBehaviour
{
    [Header("Scale Reset Targets")]
    public Transform[] explicitScaleTargets;
    public bool autoCollectDeformableTargets = true;
    public bool resetRedirectionStateAfterScaleReset = true;
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

        int refreshedRedirectionCount = resetRedirectionStateAfterScaleReset
            ? RefreshRedirectionStateAfterScaleReset()
            : 0;

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
        {
            Debug.Log(
                $"[ExhibitionExperienceResetter] Reset {restoredCount} object scale(s) and refreshed {refreshedRedirectionCount} redirection component(s) for next participant."
            );
        }

        return restoredCount;
    }

    private void AddScaleTarget(Transform target, HashSet<Transform> seen)
    {
        if (target == null || seen.Contains(target))
            return;

        seen.Add(target);
        startupScales.Add(new ScaleState(target, target.localScale));
    }

    private int RefreshRedirectionStateAfterScaleReset()
    {
        int refreshedCount = 0;
        MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);

        foreach (MonoBehaviour behaviour in behaviours)
        {
            if (behaviour == null || behaviour == this)
                continue;

            if (TryInvokeExplicitReset(behaviour))
            {
                refreshedCount++;
                continue;
            }

            if (TryRefreshRedirectionFields(behaviour))
                refreshedCount++;
        }

        return refreshedCount;
    }

    private static bool TryInvokeExplicitReset(MonoBehaviour behaviour)
    {
        MethodInfo method = behaviour.GetType().GetMethod(
            "ResetObjectRedirectionState",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            System.Type.EmptyTypes,
            null
        );

        if (method == null)
            return false;

        method.Invoke(behaviour, null);
        return true;
    }

    private static bool TryRefreshRedirectionFields(MonoBehaviour behaviour)
    {
        bool refreshed = false;
        System.Type type = behaviour.GetType();

        Transform cubeWarped = GetFieldValue<Transform>(type, behaviour, "cubeWarped");
        if (cubeWarped != null)
        {
            SetFieldValue(type, behaviour, "baseWarpedScale", cubeWarped.localScale);
            SetFieldValue(type, behaviour, "_committedRatio", Vector3.one);
            SetFieldValue(type, behaviour, "_baseScaleInitialized", true);
            refreshed = true;
        }

        object entries = GetFieldValue<object>(type, behaviour, "objects");
        if (entries is IEnumerable enumerable)
        {
            foreach (object entry in enumerable)
            {
                if (entry == null)
                    continue;

                System.Type entryType = entry.GetType();
                Transform warpedObject = GetFieldValue<Transform>(entryType, entry, "warpedObject");
                if (warpedObject == null)
                    continue;

                SetFieldValue(entryType, entry, "baseWarpedScale", warpedObject.localScale);
                SetFieldValue(entryType, entry, "baseScaleInitialized", true);
                SetFieldValue(entryType, entry, "committedRatio", Vector3.one);
                refreshed = true;
            }
        }

        if (refreshed)
        {
            SetFieldValue(type, behaviour, "_lastSelectedIndexLeft", -1);
            SetFieldValue(type, behaviour, "_lastSelectedIndexRight", -1);
            TryInvokeNoArgMethod(type, behaviour, "ResetRedirectorsToOriginalHands");
        }

        return refreshed;
    }

    private static T GetFieldValue<T>(System.Type type, object target, string fieldName)
    {
        FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null)
            return default;

        object value = field.GetValue(target);
        return value is T typed ? typed : default;
    }

    private static void SetFieldValue<T>(System.Type type, object target, string fieldName, T value)
    {
        FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null || field.IsInitOnly)
            return;

        if (typeof(T) == typeof(Vector3) && field.FieldType != typeof(Vector3))
            return;
        if (typeof(T) == typeof(bool) && field.FieldType != typeof(bool))
            return;
        if (typeof(T) == typeof(int) && field.FieldType != typeof(int))
            return;

        field.SetValue(target, value);
    }

    private static void TryInvokeNoArgMethod(System.Type type, object target, string methodName)
    {
        MethodInfo method = type.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            System.Type.EmptyTypes,
            null
        );

        method?.Invoke(target, null);
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
