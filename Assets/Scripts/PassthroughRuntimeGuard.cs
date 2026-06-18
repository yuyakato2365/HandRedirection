using System.Collections;
using UnityEngine;

public sealed class PassthroughRuntimeGuard : MonoBehaviour
{
    private const string LogPrefix = "[PassthroughRuntimeGuard]";
    private const string SceneMatchedScaniverseRootName = "Scaniverse 2026-06-17 213301";
    private const string GeneratedHandLocalOverlayPrefix = "GeneratedHandLocalScaniverseOverlay";
    private static readonly string[] OcclusionScaniverseRootNames =
    {
        SceneMatchedScaniverseRootName,
        "Scaniverse 2026-05-20 114107"
    };

    private IEnumerator Start()
    {
        yield return null;

        var manager = OVRManager.instance ?? FindFirstObjectByType<OVRManager>();
        if (manager == null)
        {
            Debug.LogWarning($"{LogPrefix} OVRManager was not found. Passthrough cannot start.");
            yield break;
        }

        manager.isInsightPassthroughEnabled = true;
        Debug.Log($"{LogPrefix} OVRManager passthrough requested. supported={OVRManager.IsInsightPassthroughSupported()}");

        var layers = FindObjectsByType<OVRPassthroughLayer>(FindObjectsSortMode.None);
        if (layers.Length == 0)
        {
            Debug.LogWarning($"{LogPrefix} No OVRPassthroughLayer was found in the loaded scene.");
        }

        foreach (var layer in layers)
        {
            layer.hidden = false;
            layer.textureOpacity = 1f;
            Debug.Log($"{LogPrefix} Layer active={layer.isActiveAndEnabled}, projection={layer.projectionSurfaceType}, placement={layer.overlayType}, hidden={layer.hidden}, opacity={layer.textureOpacity}");
        }

        ConfigureActiveScaniverseMaterials();
        EnsureHandLocalScaniverseOcclusion();
        EnsurePassthroughScaniverseModeController();
        EnsureAnchorPlacementSceneFader();
        StartCoroutine(LogState());
    }

    private void LateUpdate()
    {
        ConfigureActiveScaniverseMaterials(false);
    }

    private static void ConfigureActiveScaniverseMaterials(bool logResult = true)
    {
        Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
        if (unlitShader == null)
        {
            if (logResult)
                Debug.LogWarning($"{LogPrefix} Universal Render Pipeline/Unlit shader was not found. Scaniverse materials were not normalized.");
            return;
        }

        int rendererCount = 0;
        int materialCount = 0;
        MeshRenderer[] renderers = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
        for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
        {
            MeshRenderer renderer = renderers[rendererIndex];
            if (renderer == null || !renderer.gameObject.activeInHierarchy)
                continue;
            if (IsGeneratedHandLocalOverlay(renderer))
                continue;
            if (!IsUnderSceneMatchedScaniverseRoot(renderer.transform))
                continue;

            Material[] materials = renderer.sharedMaterials;
            bool touchedRenderer = false;
            for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                Material material = materials[materialIndex];
                if (material == null)
                    continue;

                material.shader = unlitShader;
                material.doubleSidedGI = true;
                SetMaterialFloatIfPresent(material, "_Mode", 0f);
                SetMaterialFloatIfPresent(material, "_Surface", 0f);
                SetMaterialFloatIfPresent(material, "_AlphaClip", 0f);
                SetMaterialFloatIfPresent(material, "_Cull", 0f);
                SetMaterialFloatIfPresent(material, "_SrcBlend", 1f);
                SetMaterialFloatIfPresent(material, "_DstBlend", 0f);
                SetMaterialFloatIfPresent(material, "_ZWrite", 1f);
                material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.DisableKeyword("_ALPHATEST_ON");
                material.DisableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.DisableKeyword("_ALPHAMODULATE_ON");
                material.SetOverrideTag("RenderType", "Opaque");
                material.renderQueue = 2000;
                materialCount++;
                touchedRenderer = true;
            }

            if (touchedRenderer)
                rendererCount++;
        }

        if (logResult)
            Debug.Log($"{LogPrefix} Normalized Scaniverse materials to URP/Unlit double-sided. renderers={rendererCount}, materials={materialCount}");
    }

    private static bool IsUnderSceneMatchedScaniverseRoot(Transform transform)
    {
        Transform current = transform;
        while (current != null)
        {
            if (current.name.Contains(SceneMatchedScaniverseRootName))
                return true;

            current = current.parent;
        }

        return false;
    }

    private static bool IsGeneratedHandLocalOverlay(Renderer renderer)
    {
        if (renderer == null)
            return false;

        Transform current = renderer.transform;
        while (current != null)
        {
            if (current.name.StartsWith(GeneratedHandLocalOverlayPrefix, System.StringComparison.Ordinal))
                return true;

            current = current.parent;
        }

        return false;
    }

    private static void SetMaterialFloatIfPresent(Material material, string propertyName, float value)
    {
        if (material != null && material.HasProperty(propertyName))
            material.SetFloat(propertyName, value);
    }

    public static HandLocalScaniverseOcclusion EnsureHandLocalScaniverseOcclusion()
    {
        var occlusion = FindFirstObjectByType<HandLocalScaniverseOcclusion>();
        bool created = false;

        if (occlusion == null)
        {
            var obj = new GameObject("HandLocalScaniverseOcclusion");
            occlusion = obj.AddComponent<HandLocalScaniverseOcclusion>();
            created = true;
        }

        ApplyHandLocalScaniverseRuntimeDefaults(occlusion, created);
        occlusion.enabled = true;
        occlusion.RebuildOverlay();

        if (created)
            Debug.Log($"{LogPrefix} Added HandLocalScaniverseOcclusion for hand-local Scaniverse passthrough cover.");
        else
            Debug.Log($"{LogPrefix} Re-applied HandLocalScaniverseOcclusion runtime defaults.");

        return occlusion;
    }

    private static void EnsurePassthroughScaniverseModeController()
    {
        var controller = FindFirstObjectByType<PassthroughScaniverseModeController>();
        if (controller == null)
        {
            var obj = new GameObject("PassthroughScaniverseModeController");
            controller = obj.AddComponent<PassthroughScaniverseModeController>();
        }

        controller.deskScaleSliderPanel = FindFirstObjectByType<DeskScaleSliderPanel>();
        controller.handLocalScaniverseOcclusion = FindFirstObjectByType<HandLocalScaniverseOcclusion>();
        controller.ApplyCurrentMode();
    }

    private static void ApplyHandLocalScaniverseRuntimeDefaults(HandLocalScaniverseOcclusion occlusion, bool created)
    {
        if (occlusion == null)
            return;

        occlusion.autoFindScaniverseRoots = true;
        occlusion.scaniverseRoots = null;
        occlusion.scaniverseRenderers = null;
        occlusion.autoFindNameContains = OcclusionScaniverseRootNames;
        occlusion.onlyApplyOverlayToNamedRootChildren = true;
        occlusion.scaniverseMeshRootNameEquals = new[] { "Root", "root" };
        occlusion.fullRootOverlayNameContains = new[] { SceneMatchedScaniverseRootName };
        occlusion.respectInactiveScaniverseRoots = true;
        occlusion.enableNonSelectedChildrenUnderScaniverseRoots = true;
        occlusion.doNotAutoEnableNameEquals = new[] { "Root", "root" };
        occlusion.autoFindHands = true;
        occlusion.hideOverlayDuringAnchorPlacement = false;
        occlusion.anchorPlacer = FindFirstObjectByType<ManualSpatialAnchorPlacer>();
        occlusion.deskBinder = FindFirstObjectByType<SpatialAnchorToDeskOriginBinder>();
        occlusion.preferGoGoOriginalHandSources = true;
        occlusion.avatarHandDriver = FindFirstObjectByType<AvatarHandTrackingDriver>();
        if (created)
        {
            occlusion.enableForearmBoxMask = true;
            occlusion.autoFindAvatarHandDriver = true;
            occlusion.useAvatarCapturedForearmDirection = false;
            occlusion.useIndexTipDirectionForForearmMask = true;
            occlusion.leftForearmMaskWristLocalDirection = Vector3.down;
            occlusion.rightForearmMaskWristLocalDirection = Vector3.down;
            occlusion.defaultForearmLengthMeters = 0.25f;
            occlusion.defaultWristToElbowLocalDirection = Vector3.back;
            occlusion.forearmBoxHalfWidthMeters = 0.3f;
            occlusion.forearmBoxHalfHeightMeters = 0.3f;
            occlusion.forearmBoxFeatherMeters = 0.07f;
            occlusion.forearmBoxDepthBiasMeters = 0.02f;
        }
        occlusion.autoFindObjectOriginalMaskPoints = true;
        occlusion.maxObjectMaskPoints = 16;
        occlusion.overlayRenderQueue = 2000;
        occlusion.radiusMeters = 0.18f;
        occlusion.featherMeters = 0.07f;
        occlusion.handDepthBiasMeters = 0.02f;
        occlusion.handMaskIndexTipBlend = 0.65f;
        occlusion.objectMaskLocalOffset = new Vector3(0f, 0f, -0.06f);
        occlusion.objectMaskRadiusMeters = 0.54f;
        occlusion.objectMaskFeatherMeters = 0.21f;
        occlusion.objectMaskDepthBiasMeters = 0.02f;
        occlusion.maskSoftness = 1.35f;
        occlusion.opacity = 1f;
        occlusion.hideOriginalScaniverseRenderers = true;
        occlusion.destroyGeneratedOverlayOnDisable = true;
        occlusion.pruneOldHandOverlayLogs = true;
    }

    private static void EnsureAnchorPlacementSceneFader()
    {
        var fader = FindFirstObjectByType<AnchorPlacementSceneFader>();
        if (fader == null)
        {
            var obj = new GameObject("AnchorPlacementSceneFader");
            fader = obj.AddComponent<AnchorPlacementSceneFader>();
        }

        fader.anchorPlacer = FindFirstObjectByType<ManualSpatialAnchorPlacer>();
        fader.deskBinder = FindFirstObjectByType<SpatialAnchorToDeskOriginBinder>();
        fader.placementAlpha = 0.28f;
        fader.autoFindFadeTargets = true;
        fader.includeInactiveRenderers = false;
        fader.RefreshTargetRenderers();
    }

    private static IEnumerator LogState()
    {
        for (var i = 0; i < 5; i++)
        {
            yield return new WaitForSeconds(1f);
            Debug.Log($"{LogPrefix} state supported={OVRManager.IsInsightPassthroughSupported()}, initialized={OVRManager.IsInsightPassthroughInitialized()}, pending={OVRManager.IsInsightPassthroughInitPending()}, failed={OVRManager.HasInsightPassthroughInitFailed()}");
        }
    }
}
