using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class HandRedirectionEnvironmentSetupWindow : EditorWindow
{
    private Transform room3Dgs;
    private Transform desk;
    private bool disablePreviousRooms = true;
    private Vector2 scroll;
    private string validationMessage = "Press Auto Detect or assign a room and desk.";
    private MessageType validationMessageType = MessageType.Info;

    [MenuItem("Tools/Hand Redirection/Environment Setup")]
    public static void Open()
    {
        var window = GetWindow<HandRedirectionEnvironmentSetupWindow>();
        window.titleContent = new GUIContent("Environment Setup");
        window.minSize = new Vector2(460f, 360f);
        window.AutoDetect();
        window.Show();
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.LabelField("Hand Redirection Environment Setup", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Assign the active room 3DGS and desk here. Apply writes all required Inspector references, so object names can be changed safely.",
            MessageType.Info);

        EditorGUILayout.Space();
        room3Dgs = (Transform)EditorGUILayout.ObjectField("Room 3DGS", room3Dgs, typeof(Transform), true);
        desk = (Transform)EditorGUILayout.ObjectField("Desk", desk, typeof(Transform), true);
        disablePreviousRooms = EditorGUILayout.ToggleLeft("Disable previously configured room roots", disablePreviousRooms);

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Auto Detect", GUILayout.Height(30f)))
                AutoDetect();

            using (new EditorGUI.DisabledScope(room3Dgs == null || desk == null))
            {
                if (GUILayout.Button("Apply + Validate", GUILayout.Height(30f)))
                    ApplyAndValidate();
            }
        }

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(validationMessage, validationMessageType);
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Apply updates", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("• Passthrough room target and hand-local passthrough", EditorStyles.wordWrappedLabel);
        EditorGUILayout.LabelField("• Desk scale reference and room deformation target", EditorStyles.wordWrappedLabel);
        EditorGUILayout.LabelField("• Fallback display names (references remain authoritative)", EditorStyles.wordWrappedLabel);
        EditorGUILayout.LabelField("• Scene dirty state, so the new setup can be saved", EditorStyles.wordWrappedLabel);
        EditorGUILayout.EndScrollView();
    }

    private void AutoDetect()
    {
        PassthroughRuntimeGuard guard = FindSceneObject<PassthroughRuntimeGuard>();
        DeskScaleSliderPanel slider = FindSceneObject<DeskScaleSliderPanel>();

        room3Dgs = FirstNonNull(guard != null ? guard.scaniverseTargets : null)
            ?? FirstNonNull(slider != null ? slider.scaniverseDeformationRoots : null)
            ?? FindLikelyRoomRoot();
        desk = slider != null ? slider.primaryDeskReference : null;

        Validate(false);
        Repaint();
    }

    private void ApplyAndValidate()
    {
        PassthroughRuntimeGuard guard = FindSceneObject<PassthroughRuntimeGuard>();
        DeskScaleSliderPanel slider = FindSceneObject<DeskScaleSliderPanel>();
        if (guard == null || slider == null)
        {
            validationMessage = "Required components were not found: PassthroughRuntimeGuard and DeskScaleSliderPanel must exist in the active scene.";
            validationMessageType = MessageType.Error;
            return;
        }

        Undo.RecordObjects(new UnityEngine.Object[] { guard, slider }, "Apply Hand Redirection Environment");

        Transform[] previousRooms = guard.scaniverseTargets ?? Array.Empty<Transform>();
        if (disablePreviousRooms)
        {
            foreach (Transform previousRoom in previousRooms)
            {
                if (previousRoom != null && previousRoom != room3Dgs)
                    Undo.RecordObject(previousRoom.gameObject, "Disable Previous Room");
            }
        }

        foreach (Transform previousRoom in previousRooms)
        {
            if (disablePreviousRooms && previousRoom != null && previousRoom != room3Dgs)
                previousRoom.gameObject.SetActive(false);
        }

        Undo.RecordObject(room3Dgs.gameObject, "Enable Room 3DGS");
        room3Dgs.gameObject.SetActive(true);

        guard.scaniverseTargets = new[] { room3Dgs };
        guard.activateConfiguredTargetsOnStart = true;
        guard.useNameFallbackWhenNoTargets = false;
        guard.fallbackOcclusionRootNameContains = new[] { room3Dgs.name };

        slider.primaryDeskReference = desk;
        slider.scaniverseDeformationRoots = new[] { room3Dgs };
        slider.autoFindScaniverseMinitable = false;
        slider.scaniverseRootName = room3Dgs.name;
        slider.minitableName = desk.name;
        slider.scaledObjects = Array.Empty<DeskScaleSliderPanel.ScaledObject>();
        slider.recaptureSceneObjectBaselineOnStart = true;
        slider.deformScaniverseRoomWithDeskScale = true;
        slider.deformScaniverseWidthBand = true;
        slider.deformScaniverseDepthBand = true;

        EditorUtility.SetDirty(guard);
        EditorUtility.SetDirty(slider);
        EditorUtility.SetDirty(room3Dgs.gameObject);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

        Validate(true);
        Selection.activeTransform = room3Dgs;
        EditorGUIUtility.PingObject(room3Dgs);
    }

    private void Validate(bool applied)
    {
        var issues = new System.Collections.Generic.List<string>();
        PassthroughRuntimeGuard guard = FindSceneObject<PassthroughRuntimeGuard>();
        DeskScaleSliderPanel slider = FindSceneObject<DeskScaleSliderPanel>();

        if (room3Dgs == null)
            issues.Add("Room 3DGS is not assigned.");
        if (desk == null)
            issues.Add("Desk is not assigned.");
        if (guard == null)
            issues.Add("PassthroughRuntimeGuard is missing.");
        if (slider == null)
            issues.Add("DeskScaleSliderPanel is missing.");

        if (room3Dgs != null)
        {
            bool hasRoomVisual = room3Dgs.GetComponentsInChildren<Renderer>(true).Length > 0
                || room3Dgs.GetComponentsInChildren<MeshFilter>(true).Length > 0
                || room3Dgs.GetComponentsInChildren<MonoBehaviour>(true)
                    .Any(component => component != null && component.GetType().Name.IndexOf("GaussianSplat", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!hasRoomVisual)
                issues.Add("Room 3DGS has no Renderer, MeshFilter, or Gaussian Splat component in its hierarchy.");
        }

        if (desk != null && desk.GetComponentsInChildren<Renderer>(true).Length == 0)
            issues.Add("Desk has no Renderer in its hierarchy, so width/depth bounds cannot be measured.");

        if (issues.Count > 0)
        {
            validationMessage = string.Join("\n", issues.Select(issue => "• " + issue));
            validationMessageType = MessageType.Error;
            return;
        }

        bool referencesApplied = guard.scaniverseTargets != null
            && guard.scaniverseTargets.Length == 1
            && guard.scaniverseTargets[0] == room3Dgs
            && slider.primaryDeskReference == desk
            && slider.scaniverseDeformationRoots != null
            && slider.scaniverseDeformationRoots.Length == 1
            && slider.scaniverseDeformationRoots[0] == room3Dgs
            && slider.deformScaniverseRoomWithDeskScale
            && slider.deformScaniverseWidthBand
            && slider.deformScaniverseDepthBand;

        validationMessage = referencesApplied
            ? $"Ready. Room='{room3Dgs.name}', Desk='{desk.name}'. References are assigned and the room's desk-width/depth bands will deform with the desk. Renaming either object is safe. Save the scene."
            : applied
                ? "Apply did not produce the expected references. Check the Console."
                : $"Detected Room='{room3Dgs.name}', Desk='{desk.name}'. Press Apply + Validate to remove name dependence.";
        validationMessageType = referencesApplied ? MessageType.Info : MessageType.Warning;
    }

    private static T FindSceneObject<T>() where T : UnityEngine.Object
    {
        return Resources.FindObjectsOfTypeAll<T>()
            .FirstOrDefault(candidate =>
            {
                if (candidate is Component component)
                    return component.gameObject.scene == SceneManager.GetActiveScene();
                return true;
            });
    }

    private static Transform FirstNonNull(Transform[] transforms)
    {
        return transforms != null ? transforms.FirstOrDefault(transform => transform != null) : null;
    }

    private static Transform FindLikelyRoomRoot()
    {
        Scene scene = SceneManager.GetActiveScene();
        return scene.GetRootGameObjects()
            .Select(root => root.transform)
            .FirstOrDefault(root =>
                root.name.IndexOf("3DGS", StringComparison.OrdinalIgnoreCase) >= 0
                || root.name.IndexOf("Scaniverse", StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
