using UnityEngine;

public class ColorPaletteTarget : MonoBehaviour
{
    [Header("Renderers")]
    [Tooltip("If empty, renderers are collected from this object and children.")]
    public Renderer[] targetRenderers;

    [Header("Material Property")]
    [Tooltip("Base color property used by URP/Lit and many standard shaders.")]
    public string colorProperty = "_BaseColor";
    [Tooltip("Fallback for built-in Standard shader.")]
    public string fallbackColorProperty = "_Color";

    [Header("Initial Color")]
    public bool applyOnStart = false;
    public Color initialColor = Color.white;

    private MaterialPropertyBlock block;

    public Color CurrentColor { get; private set; } = Color.white;

    void Reset()
    {
        AutoAssignRenderers();
    }

    void Awake()
    {
        AutoAssignRenderers();
        block = new MaterialPropertyBlock();
    }

    void Start()
    {
        if (applyOnStart)
            ApplyColor(initialColor);
    }

    void OnValidate()
    {
        AutoAssignRenderers();
    }

    public void ApplyColor(Color color)
    {
        CurrentColor = color;

        if (block == null)
            block = new MaterialPropertyBlock();

        AutoAssignRenderers();
        if (targetRenderers == null)
            return;

        for (int i = 0; i < targetRenderers.Length; i++)
        {
            Renderer r = targetRenderers[i];
            if (r == null)
                continue;

            r.GetPropertyBlock(block);

            Material shared = r.sharedMaterial;
            bool applied = false;

            if (shared != null && shared.HasProperty(colorProperty))
            {
                block.SetColor(colorProperty, color);
                applied = true;
            }

            if (shared != null && !applied && shared.HasProperty(fallbackColorProperty))
            {
                block.SetColor(fallbackColorProperty, color);
                applied = true;
            }

            if (!applied)
            {
                block.SetColor(colorProperty, color);
                block.SetColor(fallbackColorProperty, color);
            }

            r.SetPropertyBlock(block);
        }
    }

    public void ClearColorOverride()
    {
        if (targetRenderers == null)
            return;

        for (int i = 0; i < targetRenderers.Length; i++)
        {
            Renderer r = targetRenderers[i];
            if (r == null)
                continue;

            r.SetPropertyBlock(null);
        }
    }

    private void AutoAssignRenderers()
    {
        if (targetRenderers != null && targetRenderers.Length > 0)
            return;

        targetRenderers = GetComponentsInChildren<Renderer>(includeInactive: true);
    }
}
