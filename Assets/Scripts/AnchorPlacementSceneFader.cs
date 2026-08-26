using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class AnchorPlacementSceneFader : MonoBehaviour
{
    public ManualSpatialAnchorPlacer anchorPlacer;
    public SpatialAnchorToDeskOriginBinder deskBinder;
    [Range(0.05f, 1f)] public float placementAlpha = 0.28f;
    public bool autoFindFadeTargets = true;
    public Transform[] fadeRoots;
    public bool includeInactiveRenderers = false;
    public string[] excludeNameContains =
    {
        "SpatialAnchorPreview",
        "SpatialAnchorMarker",
        "PersistentDeskAnchorMarker",
        "LoadedPersistentDeskAnchorMarker",
        "PCVRSessionAnchorMarker",
        "Anchor",
        "Controller",
        "Camera",
        "Passthrough",
        "Status",
        "Text",
        "GeneratedHandLocalScaniverseOverlay",
        "RedirectionOriginMarker"
    };

    private readonly List<MaterialState> materialStates = new List<MaterialState>();
    private readonly List<Renderer> targetRenderers = new List<Renderer>();
    private bool fadeApplied;

    private struct MaterialState
    {
        public Material material;
        public bool hasColor;
        public Color color;
        public bool hasBaseColor;
        public Color baseColor;
        public bool hasMode;
        public float mode;
        public bool hasSurface;
        public float surface;
        public bool hasSrcBlend;
        public float srcBlend;
        public bool hasDstBlend;
        public float dstBlend;
        public bool hasZWrite;
        public float zWrite;
        public int renderQueue;
    }

    private void OnEnable()
    {
        if (anchorPlacer == null)
            anchorPlacer = FindAnyObjectByType<ManualSpatialAnchorPlacer>();
        if (deskBinder == null)
            deskBinder = FindAnyObjectByType<SpatialAnchorToDeskOriginBinder>();
        RefreshTargetRenderers();
    }

    private void OnDisable()
    {
        RestoreFade();
    }

    private void OnDestroy()
    {
        RestoreFade();
    }

    private void LateUpdate()
    {
        if (anchorPlacer == null)
            anchorPlacer = FindAnyObjectByType<ManualSpatialAnchorPlacer>();
        if (deskBinder == null)
            deskBinder = FindAnyObjectByType<SpatialAnchorToDeskOriginBinder>();

        bool shouldFade = anchorPlacer != null && anchorPlacer.IsPlacementMode;
        if (shouldFade)
            ApplyFade();
        else
            RestoreFade();
    }

    private void ApplyFade()
    {
        if (fadeApplied)
            return;

        if (targetRenderers.Count == 0)
            RefreshTargetRenderers();

        float alpha = Mathf.Clamp01(placementAlpha);
        for (int i = 0; i < targetRenderers.Count; i++)
        {
            Renderer renderer = targetRenderers[i];
            if (renderer == null || ShouldExclude(renderer))
                continue;

            Material[] materials = renderer.sharedMaterials;
            for (int m = 0; m < materials.Length; m++)
            {
                Material material = materials[m];
                if (material == null || ContainsMaterialState(material))
                    continue;

                materialStates.Add(CaptureMaterialState(material));
                ForceMaterialTransparent(material, alpha);
            }
        }

        fadeApplied = materialStates.Count > 0;
    }

    public void RefreshTargetRenderers()
    {
        targetRenderers.Clear();

        HashSet<Transform> roots = new HashSet<Transform>();
        if (fadeRoots != null)
        {
            for (int i = 0; i < fadeRoots.Length; i++)
            {
                if (fadeRoots[i] != null)
                    roots.Add(fadeRoots[i]);
            }
        }

        if (autoFindFadeTargets)
            AddAutoFadeRoots(roots);

        foreach (Transform root in roots)
            AddRenderersUnder(root);
    }

    private static void AddAutoFadeRoots(HashSet<Transform> roots)
    {
        GoGoInteractionController_NoY3[] controllers = FindObjectsByType<GoGoInteractionController_NoY3>(FindObjectsSortMode.None);
        if (controllers != null)
        {
            for (int i = 0; i < controllers.Length; i++)
            {
                GoGoInteractionController_NoY3 controller = controllers[i];
                if (controller == null)
                    continue;

                AddRoot(roots, controller.cubeWarped);
                if (controller.objects == null)
                    continue;

                for (int entryIndex = 0; entryIndex < controller.objects.Count; entryIndex++)
                {
                    GoGoInteractionController_NoY3.WarpObjectEntry entry = controller.objects[entryIndex];
                    if (entry != null && entry.enabled)
                        AddRoot(roots, entry.warpedObject);
                }
            }
        }

        DeskVisualFollower[] deskVisuals = FindObjectsByType<DeskVisualFollower>(FindObjectsSortMode.None);
        if (deskVisuals != null)
        {
            for (int i = 0; i < deskVisuals.Length; i++)
            {
                if (deskVisuals[i] != null)
                    AddRoot(roots, deskVisuals[i].transform);
            }
        }

        DeskScaleSliderPanel[] deskSliders = FindObjectsByType<DeskScaleSliderPanel>(FindObjectsSortMode.None);
        if (deskSliders != null)
        {
            for (int i = 0; i < deskSliders.Length; i++)
            {
                DeskScaleSliderPanel slider = deskSliders[i];
                if (slider == null)
                    continue;

                AddRoot(roots, slider.primaryDeskReference);
                if (slider.scaniverseDeformationRoots == null)
                    continue;

                for (int rootIndex = 0; rootIndex < slider.scaniverseDeformationRoots.Length; rootIndex++)
                    AddRoot(roots, slider.scaniverseDeformationRoots[rootIndex]);
            }
        }
    }

    private static void AddRoot(HashSet<Transform> roots, Transform root)
    {
        if (root != null)
            roots.Add(root);
    }

    private void AddRenderersUnder(Transform root)
    {
        if (root == null)
            return;

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactiveRenderers);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer != null && !targetRenderers.Contains(renderer))
                targetRenderers.Add(renderer);
        }
    }

    private void RestoreFade()
    {
        if (!fadeApplied && materialStates.Count == 0)
            return;

        for (int i = 0; i < materialStates.Count; i++)
            RestoreMaterialState(materialStates[i]);

        materialStates.Clear();
        fadeApplied = false;
    }

    public void ClearFadeNow()
    {
        RestoreFade();
    }

    private bool ShouldExclude(Renderer renderer)
    {
        if (renderer == null)
            return true;
        if (renderer.GetComponentInParent<HandLocalScaniverseOcclusion>() != null)
            return true;
        if (renderer.GetComponentInParent<Canvas>() != null)
            return true;

        Transform current = renderer.transform;
        while (current != null)
        {
            if (NameMatchesAny(current.name, excludeNameContains))
                return true;
            current = current.parent;
        }

        return false;
    }

    private bool ContainsMaterialState(Material material)
    {
        for (int i = 0; i < materialStates.Count; i++)
        {
            if (materialStates[i].material == material)
                return true;
        }

        return false;
    }

    private static MaterialState CaptureMaterialState(Material material)
    {
        return new MaterialState
        {
            material = material,
            hasColor = material.HasProperty("_Color"),
            color = material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white,
            hasBaseColor = material.HasProperty("_BaseColor"),
            baseColor = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : Color.white,
            hasMode = material.HasProperty("_Mode"),
            mode = material.HasProperty("_Mode") ? material.GetFloat("_Mode") : 0f,
            hasSurface = material.HasProperty("_Surface"),
            surface = material.HasProperty("_Surface") ? material.GetFloat("_Surface") : 0f,
            hasSrcBlend = material.HasProperty("_SrcBlend"),
            srcBlend = material.HasProperty("_SrcBlend") ? material.GetFloat("_SrcBlend") : 0f,
            hasDstBlend = material.HasProperty("_DstBlend"),
            dstBlend = material.HasProperty("_DstBlend") ? material.GetFloat("_DstBlend") : 0f,
            hasZWrite = material.HasProperty("_ZWrite"),
            zWrite = material.HasProperty("_ZWrite") ? material.GetFloat("_ZWrite") : 1f,
            renderQueue = material.renderQueue
        };
    }

    private static void ForceMaterialTransparent(Material material, float alpha)
    {
        if (material.HasProperty("_Color"))
        {
            Color color = material.GetColor("_Color");
            color.a = alpha;
            material.SetColor("_Color", color);
        }

        if (material.HasProperty("_BaseColor"))
        {
            Color color = material.GetColor("_BaseColor");
            color.a = alpha;
            material.SetColor("_BaseColor", color);
        }

        if (material.HasProperty("_Mode"))
            material.SetFloat("_Mode", 3f);
        if (material.HasProperty("_Surface"))
            material.SetFloat("_Surface", 1f);
        if (material.HasProperty("_SrcBlend"))
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        if (material.HasProperty("_DstBlend"))
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        if (material.HasProperty("_ZWrite"))
            material.SetFloat("_ZWrite", 0f);

        material.EnableKeyword("_ALPHABLEND_ON");
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.renderQueue = (int)RenderQueue.Transparent;
    }

    private static void RestoreMaterialState(MaterialState state)
    {
        Material material = state.material;
        if (material == null)
            return;

        if (state.hasColor)
            material.SetColor("_Color", state.color);
        if (state.hasBaseColor)
            material.SetColor("_BaseColor", state.baseColor);
        if (state.hasMode)
            material.SetFloat("_Mode", state.mode);
        if (state.hasSurface)
            material.SetFloat("_Surface", state.surface);
        if (state.hasSrcBlend)
            material.SetFloat("_SrcBlend", state.srcBlend);
        if (state.hasDstBlend)
            material.SetFloat("_DstBlend", state.dstBlend);
        if (state.hasZWrite)
            material.SetFloat("_ZWrite", state.zWrite);

        if ((!state.hasMode || state.mode < 2.5f) && (!state.hasSurface || state.surface < 0.5f))
        {
            material.DisableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }

        material.renderQueue = state.renderQueue;
    }

    private static bool NameMatchesAny(string objectName, string[] tokens)
    {
        if (string.IsNullOrEmpty(objectName) || tokens == null || tokens.Length == 0)
            return false;

        string lower = objectName.ToLowerInvariant();
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (!string.IsNullOrWhiteSpace(token) && lower.Contains(token.ToLowerInvariant()))
                return true;
        }

        return false;
    }
}
