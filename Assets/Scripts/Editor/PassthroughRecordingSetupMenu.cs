using UnityEditor;
using UnityEngine;
using System;
using System.Reflection;

public static class PassthroughRecordingSetupMenu
{
    [MenuItem("Tools/Recording/Create Or Update Passthrough Recording Bridge")]
    public static void CreateOrUpdateBridge()
    {
        GameObject root = GameObject.Find("PassthroughRecordingBridge");
        if (root == null)
        {
            root = new GameObject("PassthroughRecordingBridge");
            Undo.RegisterCreatedObjectUndo(root, "Create Passthrough Recording Bridge");
        }

        PassthroughTcpStreamRecorderBridge bridge = root.GetComponent<PassthroughTcpStreamRecorderBridge>();
        if (bridge == null)
            bridge = Undo.AddComponent<PassthroughTcpStreamRecorderBridge>(root);

        Undo.RecordObject(bridge, "Configure Passthrough Recording Bridge");
        bridge.listenPort = 9201;
        bridge.statusPort = 9202;
        bridge.framesPerSecond = 15;
        bridge.jpegQuality = 75;
        bridge.maxLongSidePixels = 1280;
        bridge.autoFindPassthroughCameraAccess = true;
        bridge.autoCreatePassthroughCameraAccess = true;
        bridge.preferRightCamera = true;
        bridge.requestedResolution = new Vector2Int(1280, 960);

        if (bridge.passthroughCameraAccess == null)
            bridge.passthroughCameraAccess = FindPassthroughCameraAccess();

        if (bridge.passthroughCameraAccess == null)
            bridge.passthroughCameraAccess = CreatePassthroughCameraAccess();

        ConfigureProjectPassthroughCameraAccess();
        ConfigureOvrManagerPassthroughCameraPermission();

        EditorUtility.SetDirty(root);
        Selection.activeGameObject = root;
        EditorGUIUtility.PingObject(root);

        if (bridge.passthroughCameraAccess == null)
        {
            Debug.LogWarning("[PassthroughRecordingSetupMenu] Bridge created, but PassthroughCameraAccess could not be created. Check that Meta MR Utility Kit is installed.");
        }
        else
        {
            Debug.Log($"[PassthroughRecordingSetupMenu] Bridge ready. PassthroughCameraAccess={bridge.passthroughCameraAccess.name}");
        }
    }

    private static MonoBehaviour FindPassthroughCameraAccess()
    {
        MonoBehaviour[] behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (MonoBehaviour behaviour in behaviours)
        {
            if (behaviour == null)
                continue;

            if (behaviour.GetType().Name.IndexOf("PassthroughCameraAccess", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return behaviour;
        }

        return null;
    }

    private static MonoBehaviour CreatePassthroughCameraAccess()
    {
        Type pcaType = FindTypeByName("Meta.XR.PassthroughCameraAccess") ?? FindTypeByName("PassthroughCameraAccess");
        if (pcaType == null || !typeof(MonoBehaviour).IsAssignableFrom(pcaType))
            return null;

        GameObject obj = GameObject.Find("PassthroughCameraAccess_Right");
        if (obj == null)
        {
            obj = new GameObject("PassthroughCameraAccess_Right");
            Undo.RegisterCreatedObjectUndo(obj, "Create Passthrough Camera Access");
        }

        MonoBehaviour pca = obj.GetComponent(pcaType) as MonoBehaviour;
        if (pca == null)
            pca = Undo.AddComponent(obj, pcaType) as MonoBehaviour;

        ConfigurePassthroughCameraAccess(pca);
        EditorUtility.SetDirty(obj);
        return pca;
    }

    private static void ConfigurePassthroughCameraAccess(MonoBehaviour pca)
    {
        if (pca == null)
            return;

        SerializedObject serialized = new SerializedObject(pca);
        SerializedProperty cameraPosition = serialized.FindProperty("CameraPosition");
        if (cameraPosition != null)
            cameraPosition.enumValueIndex = FindEnumIndex(cameraPosition, "Right", 1);

        SerializedProperty requestedResolution = serialized.FindProperty("RequestedResolution");
        if (requestedResolution != null)
            requestedResolution.vector2IntValue = new Vector2Int(1280, 960);

        serialized.ApplyModifiedProperties();
    }

    private static int FindEnumIndex(SerializedProperty property, string enumName, int fallback)
    {
        for (int i = 0; i < property.enumNames.Length; i++)
        {
            if (string.Equals(property.enumNames[i], enumName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return Mathf.Clamp(fallback, 0, property.enumNames.Length - 1);
    }

    private static void ConfigureOvrManagerPassthroughCameraPermission()
    {
        MonoBehaviour manager = FindMonoBehaviourByTypeName("OVRManager");
        if (manager == null)
            return;

        SerializedObject serialized = new SerializedObject(manager);
        SerializedProperty requestPermission = serialized.FindProperty("requestPassthroughCameraAccessPermissionOnStartup");
        if (requestPermission != null)
        {
            requestPermission.boolValue = true;
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(manager);
        }
    }

    private static MonoBehaviour FindMonoBehaviourByTypeName(string typeName)
    {
        MonoBehaviour[] behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (MonoBehaviour behaviour in behaviours)
        {
            if (behaviour == null)
                continue;

            if (behaviour.GetType().Name == typeName)
                return behaviour;
        }

        return null;
    }

    private static void ConfigureProjectPassthroughCameraAccess()
    {
        Type configType = FindTypeByName("OVRProjectConfig");
        if (configType == null)
            return;

        MethodInfo getConfig = configType.GetMethod("GetProjectConfig", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo commitConfig = configType.GetMethod("CommitProjectConfig", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        object config = getConfig != null ? getConfig.Invoke(null, null) : null;
        if (config == null)
            return;

        FieldInfo enabledField = configType.GetField("isPassthroughCameraAccessEnabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        enabledField?.SetValue(config, true);

        PropertyInfo supportProperty = configType.GetProperty("insightPassthroughSupport", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (supportProperty != null && supportProperty.PropertyType.IsEnum)
        {
            object supported = Enum.Parse(supportProperty.PropertyType, "Supported");
            supportProperty.SetValue(config, supported);
        }

        try
        {
            commitConfig?.Invoke(null, new[] { config });
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PassthroughRecordingSetupMenu] Could not commit OVRProjectConfig automatically: {e.Message}");
        }
    }

    private static Type FindTypeByName(string typeName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type exact = assembly.GetType(typeName);
            if (exact != null)
                return exact;

            foreach (Type type in GetLoadableTypes(assembly))
            {
                if (type.Name == typeName)
                    return type;
            }
        }

        return null;
    }

    private static Type[] GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            System.Collections.Generic.List<Type> types = new System.Collections.Generic.List<Type>();
            foreach (Type type in e.Types)
            {
                if (type != null)
                    types.Add(type);
            }

            return types.ToArray();
        }
    }
}
