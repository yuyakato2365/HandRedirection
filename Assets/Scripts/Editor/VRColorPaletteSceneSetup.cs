using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class VRColorPaletteSceneSetup
{
    [InitializeOnLoadMethod]
    private static void AutoCreatePaletteAfterCompile()
    {
        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            if (GameObject.Find("VRColorPalettePanel") != null)
                return;
            if (!SceneLooksRelevant())
                return;

            CreateOrUpdateScenePalette(saveScene: false);
        };
    }

    [MenuItem("Tools/VR Color Palette/Create Or Update Scene Palette")]
    public static void CreateOrUpdateScenePalette()
    {
        CreateOrUpdateScenePalette(saveScene: true);
    }

    private static void CreateOrUpdateScenePalette(bool saveScene)
    {
        EnsureUsableSceneIsOpen();

        GameObject panelObject = GameObject.Find("VRColorPalettePanel");
        if (panelObject == null)
        {
            panelObject = new GameObject("VRColorPalettePanel");
            Undo.RegisterCreatedObjectUndo(panelObject, "Create VR Color Palette Panel");
        }

        Transform desk = FindDeskAnchor();
        if (desk != null && panelObject.transform.parent != desk)
            Undo.SetTransformParent(panelObject.transform, desk, "Parent VR Color Palette Panel To Desk");

        Undo.RecordObject(panelObject.transform, "Place VR Color Palette Panel");
        panelObject.transform.localPosition = new Vector3(0.35f, 0.10f, 0.25f);
        panelObject.transform.localRotation = Quaternion.Euler(65f, 0f, 0f);
        panelObject.transform.localScale = Vector3.one;

        VRColorPalettePanel panel = panelObject.GetComponent<VRColorPalettePanel>();
        if (panel == null)
            panel = Undo.AddComponent<VRColorPalettePanel>(panelObject);

        Undo.RecordObject(panel, "Configure VR Color Palette Panel");
        panel.autoDiscoverTargetNames = new[] { "Usagi", "Usagi (1)", "teapot", "Teapot" };
        panel.autoAddColorTargetToDiscoveredObjects = true;
        panel.enableTargetButtons = true;
        panel.enableGradientPicker = true;
        panel.requirePinchToSelect = false;
        PopulateDefaultTargetButtonsIfEmpty(panel);
        panel.RefreshSceneTargetsFromEditor();
        SchedulePanelRebuild(panel, saveScene);

        ResultPlacer placer = Object.FindAnyObjectByType<ResultPlacer>();
        if (placer != null)
        {
            Undo.RecordObject(placer, "Assign VR Color Palette To Result Placer");
            placer.colorPalettePanel = panel;
            EditorUtility.SetDirty(placer);
        }

        EditorUtility.SetDirty(panelObject);
        EditorUtility.SetDirty(panel);
        EditorSceneManager.MarkSceneDirty(panelObject.scene);

        Selection.activeGameObject = panelObject;
        EditorGUIUtility.PingObject(panelObject);
        Debug.Log("Created/updated VRColorPalettePanel and linked selectable scene targets.");
    }

    [MenuItem("Tools/VR Color Palette/Rebuild Selected Palette")]
    public static void RebuildSelectedPalette()
    {
        VRColorPalettePanel panel = Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponent<VRColorPalettePanel>()
            : null;

        if (panel == null)
        {
            EditorUtility.DisplayDialog("VR Color Palette", "Select a GameObject with VRColorPalettePanel.", "OK");
            return;
        }

        Undo.RecordObject(panel, "Rebuild VR Color Palette");
        panel.RefreshSceneTargetsFromEditor();
        panel.RebuildImmediateForEditor();
        EditorUtility.SetDirty(panel);
        EditorSceneManager.MarkSceneDirty(panel.gameObject.scene);
    }

    private static void SchedulePanelRebuild(VRColorPalettePanel panel, bool saveScene)
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

        return null;
    }

    private static void PopulateDefaultTargetButtonsIfEmpty(VRColorPalettePanel panel)
    {
        if (panel == null || (panel.targetButtonEntries != null && panel.targetButtonEntries.Length > 0))
            return;

        string[] names = panel.autoDiscoverTargetNames;
        if (names == null || names.Length == 0)
            return;

        var entries = new System.Collections.Generic.List<VRColorPalettePanel.TargetButtonEntry>();
        for (int i = 0; i < names.Length; i++)
        {
            string objectName = names[i];
            if (string.IsNullOrWhiteSpace(objectName))
                continue;

            GameObject go = GameObject.Find(objectName);
            if (go == null)
                continue;

            if (panel.autoAddColorTargetToDiscoveredObjects && go.GetComponent<ColorPaletteTarget>() == null)
                go.AddComponent<ColorPaletteTarget>();

            entries.Add(new VRColorPalettePanel.TargetButtonEntry
            {
                label = objectName,
                targetObject = go
            });
        }

        panel.targetButtonEntries = entries.ToArray();
    }

    private static bool SceneLooksRelevant()
    {
        if (Object.FindAnyObjectByType<ResultPlacer>() != null)
            return true;

        string[] names = { "Usagi", "Usagi (1)", "teapot", "Teapot", "DeskOrigin", "Desk" };
        for (int i = 0; i < names.Length; i++)
        {
            if (GameObject.Find(names[i]) != null)
                return true;
        }

        return false;
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
