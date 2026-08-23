using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Lightweight runtime XYZ axes for Quest/PCVR diagnostics.
/// X is red, Y is green, and Z is blue.
/// </summary>
public sealed class RuntimeCoordinateAxes : MonoBehaviour
{
    public float axisLength = 0.12f;
    public float lineWidth = 0.006f;
    public bool showLabels = true;

    private bool built;

    public static RuntimeCoordinateAxes Create(
        string objectName,
        Transform parent,
        float length,
        float width,
        bool labels)
    {
        GameObject root = new GameObject(objectName);
        root.transform.SetParent(parent, false);

        RuntimeCoordinateAxes axes = root.AddComponent<RuntimeCoordinateAxes>();
        axes.axisLength = length;
        axes.lineWidth = width;
        axes.showLabels = labels;
        axes.EnsureBuilt();
        return axes;
    }

    private void Start()
    {
        EnsureBuilt();
    }

    public void EnsureBuilt()
    {
        if (built)
            return;

        built = true;
        CreateAxis("X Axis", "X", Vector3.right, new Color(1f, 0.12f, 0.12f, 1f));
        CreateAxis("Y Axis", "Y", Vector3.up, new Color(0.15f, 1f, 0.2f, 1f));
        CreateAxis("Z Axis", "Z", Vector3.forward, new Color(0.15f, 0.4f, 1f, 1f));
    }

    private void CreateAxis(string axisName, string label, Vector3 direction, Color color)
    {
        GameObject lineObject = new GameObject(axisName);
        lineObject.transform.SetParent(transform, false);

        LineRenderer line = lineObject.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.positionCount = 2;
        line.SetPosition(0, Vector3.zero);
        line.SetPosition(1, direction * Mathf.Max(0.01f, axisLength));
        line.startWidth = Mathf.Max(0.0005f, lineWidth);
        line.endWidth = Mathf.Max(0.0005f, lineWidth * 0.55f);
        line.numCapVertices = 4;
        line.startColor = color;
        line.endColor = color;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.material = CreateUnlitMaterial(color);

        if (!showLabels)
            return;

        GameObject labelObject = new GameObject(label + " Label");
        labelObject.transform.SetParent(transform, false);
        labelObject.transform.localPosition = direction * (Mathf.Max(0.01f, axisLength) + lineWidth * 2f);
        labelObject.transform.localScale = Vector3.one;

        TextMesh text = labelObject.AddComponent<TextMesh>();
        text.text = label;
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        text.fontSize = 48;
        text.characterSize = Mathf.Max(0.004f, axisLength * 0.12f);
        text.color = color;
    }

    private static Material CreateUnlitMaterial(Color color)
    {
        Shader shader = Shader.Find("HDRP/Unlit")
            ?? Shader.Find("Universal Render Pipeline/Unlit")
            ?? Shader.Find("Unlit/Color")
            ?? Shader.Find("Sprites/Default");

        Material material = new Material(shader)
        {
            color = color,
            hideFlags = HideFlags.DontSave
        };

        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", color);
        if (material.HasProperty("_UnlitColor"))
            material.SetColor("_UnlitColor", color);
        return material;
    }
}
