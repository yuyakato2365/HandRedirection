using System;
using System.Reflection;
using UnityEngine;

public class PassthroughBootstrap : MonoBehaviour
{
    public bool enableOnStart = true;
    public Color cameraClearColor = new Color(0f, 0f, 0f, 0f);

    void Start()
    {
        if (enableOnStart)
            EnablePassthrough();
    }

    [ContextMenu("Enable Passthrough")]
    public void EnablePassthrough()
    {
        EnableOvrManagerPassthrough();
        EnsurePassthroughLayer();
        ConfigureMainCamera();
    }

    void EnableOvrManagerPassthrough()
    {
        Type managerType = FindType("OVRManager");
        if (managerType == null) return;

        UnityEngine.Object manager = FindFirstObjectByType(managerType);
        if (manager == null) return;

        SetBool(manager, "isInsightPassthroughEnabled", true);
    }

    void EnsurePassthroughLayer()
    {
        Type layerType = FindType("OVRPassthroughLayer");
        if (layerType == null) return;

        Component layer = FindFirstObjectByType(layerType) as Component;
        if (layer == null)
        {
            GameObject host = Camera.main != null ? Camera.main.gameObject : gameObject;
            layer = host.AddComponent(layerType);
        }

        SetEnum(layer, "overlayType", "Underlay");
        SetFloat(layer, "textureOpacity", 1f);
        layer.enabled = true;
    }

    void ConfigureMainCamera()
    {
        if (Camera.main == null) return;

        Camera.main.clearFlags = CameraClearFlags.SolidColor;
        Camera.main.backgroundColor = cameraClearColor;
    }

    static Type FindType(string typeName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType(typeName);
            if (type != null) return type;
        }

        return null;
    }

    static UnityEngine.Object FindFirstObjectByType(Type type)
    {
        UnityEngine.Object[] objects = Resources.FindObjectsOfTypeAll(type);
        foreach (UnityEngine.Object obj in objects)
        {
            if (obj is Component component && component.gameObject.scene.IsValid())
                return obj;
            if (obj is GameObject gameObject && gameObject.scene.IsValid())
                return obj;
        }

        return null;
    }

    static void SetBool(object target, string memberName, bool value)
    {
        Type type = target.GetType();
        FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && field.FieldType == typeof(bool))
        {
            field.SetValue(target, value);
            return;
        }

        PropertyInfo property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.PropertyType == typeof(bool) && property.CanWrite)
            property.SetValue(target, value);
    }

    static void SetFloat(object target, string memberName, float value)
    {
        Type type = target.GetType();
        FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && field.FieldType == typeof(float))
        {
            field.SetValue(target, value);
            return;
        }

        PropertyInfo property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.PropertyType == typeof(float) && property.CanWrite)
            property.SetValue(target, value);
    }

    static void SetEnum(object target, string memberName, string enumName)
    {
        Type type = target.GetType();
        FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && field.FieldType.IsEnum)
        {
            field.SetValue(target, Enum.Parse(field.FieldType, enumName));
            return;
        }

        PropertyInfo property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.PropertyType.IsEnum && property.CanWrite)
            property.SetValue(target, Enum.Parse(property.PropertyType, enumName));
    }
}
