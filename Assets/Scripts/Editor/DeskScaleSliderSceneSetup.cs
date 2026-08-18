using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class DeskScaleSliderSceneSetup
{
    [InitializeOnLoadMethod]
    private static void AutoCreateSliderAfterCompile()
    {
        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            if (GameObject.Find("DeskScaleSliderPanel") != null)
                return;
            if (Object.FindAnyObjectByType<GoGoInteractionController_NoY3>() == null)
                return;

            CreateOrUpdateSceneSlider(saveScene: false);
        };
    }

    [MenuItem("Tools/Desk Scale Slider/Create Or Update Scene Slider")]
    public static void CreateOrUpdateSceneSlider()
    {
        CreateOrUpdateSceneSlider(saveScene: true);
    }

    [MenuItem("Tools/Desk Scale Slider/Rebuild Selected Slider")]
    public static void RebuildSelectedSlider()
    {
        DeskScaleSliderPanel panel = Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponent<DeskScaleSliderPanel>()
            : null;

        if (panel == null)
        {
            EditorUtility.DisplayDialog("Desk Scale Slider", "Select a GameObject with DeskScaleSliderPanel.", "OK");
            return;
        }

        Undo.RecordObject(panel, "Rebuild Desk Scale Slider");
        panel.RebuildImmediateForEditor();
        EditorUtility.SetDirty(panel);
        EditorSceneManager.MarkSceneDirty(panel.gameObject.scene);
    }

    private static void CreateOrUpdateSceneSlider(bool saveScene)
    {
        EnsureUsableSceneIsOpen();

        GameObject panelObject = GameObject.Find("DeskScaleSliderPanel");
        if (panelObject == null)
        {
            panelObject = new GameObject("DeskScaleSliderPanel");
            Undo.RegisterCreatedObjectUndo(panelObject, "Create Desk Scale Slider Panel");
        }

        Transform desk = FindDeskAnchor();
        if (desk != null && panelObject.transform.parent != desk)
            Undo.SetTransformParent(panelObject.transform, desk, "Parent Desk Scale Slider Panel To Desk");

        Undo.RecordObject(panelObject.transform, "Place Desk Scale Slider Panel");
        panelObject.transform.localPosition = new Vector3(0f, 0.16f, 0.34f);
        panelObject.transform.localRotation = Quaternion.Euler(65f, 0f, 0f);
        panelObject.transform.localScale = Vector3.one;

        DeskScaleSliderPanel panel = panelObject.GetComponent<DeskScaleSliderPanel>();
        if (panel == null)
            panel = Undo.AddComponent<DeskScaleSliderPanel>(panelObject);

        Undo.RecordObject(panel, "Configure Desk Scale Slider Panel");
        panel.redirectionController = DeskScaleSliderPanel.FindBestRedirectionController();
        panel.autoFindRedirectionController = true;
        panel.applyToWidthScale = true;
        panel.applyToDepthScale = true;
        panel.minScale = 1f;
        panel.maxScale = 3f;
        panel.requirePinchToDrag = true;
        panel.panelSize = new Vector2(0.34f, 0.105f);
        panel.trackWidth = 0.26f;
        panel.trackHeight = 0.012f;
        panel.knobSize = new Vector2(0.032f, 0.04f);
        panel.touchRadius = 0.045f;

        SchedulePanelRebuild(panel, saveScene);

        EditorUtility.SetDirty(panelObject);
        EditorUtility.SetDirty(panel);
        EditorSceneManager.MarkSceneDirty(panelObject.scene);

        Selection.activeGameObject = panelObject;
        EditorGUIUtility.PingObject(panelObject);
        Debug.Log("Created/updated DeskScaleSliderPanel for GoGo desk scale control.");
    }

    private static void SchedulePanelRebuild(DeskScaleSliderPanel panel, bool saveScene)
    {
        EditorApplication.delayCall += () =>
        {
            if (panel == null)
                return;

            panel.RebuildImmediateForEditor();
            EditorUtility.SetDirty(panel);
            EditorUtility.SetDirty(panel.gameObject);
            EditorSceneManager.MarkSceneDirty(panel.gameObject.scene);

            if (saveScene)
                EditorSceneManager.SaveScene(panel.gameObject.scene);
        };
    }

    private static Transform FindDeskAnchor()
    {
        string[] names = { "DeskOrigin", "Desk", "Table", "CoffeeTable", "CoffeeTableTraditional" };
        for (int i = 0; i < names.Length; i++)
        {
            GameObject go = GameObject.Find(names[i]);
            if (go != null)
                return go.transform;
        }

        GoGoInteractionController_NoY3 goGo = Object.FindAnyObjectByType<GoGoInteractionController_NoY3>();
        if (goGo != null && goGo.deskOrigin != null)
            return goGo.deskOrigin;

        return null;
    }

    private static void EnsureUsableSceneIsOpen()
    {
        if (!string.IsNullOrEmpty(EditorSceneManager.GetActiveScene().path))
            return;

        EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
        for (int i = 0; i < scenes.Length; i++)
        {
            EditorBuildSettingsScene scene = scenes[i];
            if (scene != null && scene.enabled && !string.IsNullOrEmpty(scene.path))
            {
                EditorSceneManager.OpenScene(scene.path);
                return;
            }
        }
    }
}
