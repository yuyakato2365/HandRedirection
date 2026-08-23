using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class HandRedirectionEditorPlayBridge
{
    private const string CommandFileName = "hand_redirection_editor_command.txt";
    private const double PollIntervalSeconds = 0.2;
    private const long CommandLifetimeMilliseconds = 10000;

    private static double nextPollTime;

    static HandRedirectionEditorPlayBridge()
    {
        EditorApplication.update -= Poll;
        EditorApplication.update += Poll;
    }

    private static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextPollTime)
            return;
        nextPollTime = EditorApplication.timeSinceStartup + PollIntervalSeconds;

        string path = Path.Combine(Directory.GetParent(Application.dataPath)?.FullName ?? "", "Temp", CommandFileName);
        if (!File.Exists(path))
            return;

        string raw;
        try
        {
            raw = File.ReadAllText(path).Trim();
            File.Delete(path);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[HandRedirectionEditorPlayBridge] Failed to consume command: {e.Message}");
            return;
        }

        string[] parts = raw.Split('|');
        if (parts.Length != 2 ||
            !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long sentAtMs))
            return;

        long ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sentAtMs;
        if (ageMs < 0 || ageMs > CommandLifetimeMilliseconds)
            return;

        switch (parts[0].ToUpperInvariant())
        {
            case "PLAY":
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    EditorApplication.isPlaying = true;
                break;
            case "STOP":
                if (EditorApplication.isPlaying)
                    EditorApplication.isPlaying = false;
                break;
            case "SHOW_EDITOR_REFERENCE_AXES":
                SetEditorReferenceAxesVisible(true);
                break;
            case "HIDE_EDITOR_REFERENCE_AXES":
                SetEditorReferenceAxesVisible(false);
                break;
        }
    }

    private static void SetEditorReferenceAxesVisible(bool visible)
    {
        SpatialAnchorToDeskOriginBinder binder = UnityEngine.Object.FindFirstObjectByType<SpatialAnchorToDeskOriginBinder>(FindObjectsInactive.Include);
        if (binder == null)
        {
            Debug.LogWarning("[HandRedirectionEditorPlayBridge] SpatialAnchorToDeskOriginBinder was not found.");
            return;
        }

        Undo.RecordObject(binder, visible ? "Show Reference Axes" : "Hide Reference Axes");
        binder.showReferenceAxesInEditMode = visible;
        EditorUtility.SetDirty(binder);
        if (binder.gameObject.scene.IsValid())
            EditorSceneManager.MarkSceneDirty(binder.gameObject.scene);
        SceneView.RepaintAll();
    }
}
