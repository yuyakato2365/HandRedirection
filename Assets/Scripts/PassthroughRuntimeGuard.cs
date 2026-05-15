using System.Collections;
using UnityEngine;

public sealed class PassthroughRuntimeGuard : MonoBehaviour
{
    private const string LogPrefix = "[PassthroughRuntimeGuard]";

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

        EnsureHandLocalScaniverseOcclusion();
        EnsureAnchorPlacementSceneFader();
        StartCoroutine(LogState());
    }

    private static void EnsureHandLocalScaniverseOcclusion()
    {
        var occlusion = FindFirstObjectByType<HandLocalScaniverseOcclusion>();
        bool created = false;

        if (occlusion == null)
        {
            var obj = new GameObject("HandLocalScaniverseOcclusion");
            occlusion = obj.AddComponent<HandLocalScaniverseOcclusion>();
            created = true;
        }

        ApplyHandLocalScaniverseRuntimeDefaults(occlusion);
        occlusion.enabled = true;
        occlusion.RebuildOverlay();

        if (created)
            Debug.Log($"{LogPrefix} Added HandLocalScaniverseOcclusion for hand-local Scaniverse passthrough cover.");
        else
            Debug.Log($"{LogPrefix} Re-applied HandLocalScaniverseOcclusion runtime defaults.");
    }

    private static void ApplyHandLocalScaniverseRuntimeDefaults(HandLocalScaniverseOcclusion occlusion)
    {
        if (occlusion == null)
            return;

        occlusion.autoFindScaniverseRoots = true;
        occlusion.onlyApplyOverlayToNamedRootChildren = true;
        occlusion.scaniverseMeshRootNameEquals = new[] { "Root", "root" };
        occlusion.enableNonSelectedChildrenUnderScaniverseRoots = true;
        occlusion.doNotAutoEnableNameEquals = new[] { "Root", "root" };
        occlusion.autoFindHands = true;
        occlusion.hideOverlayDuringAnchorPlacement = false;
        occlusion.anchorPlacer = FindFirstObjectByType<ManualSpatialAnchorPlacer>();
        occlusion.deskBinder = FindFirstObjectByType<SpatialAnchorToDeskOriginBinder>();
        occlusion.preferGoGoOriginalHandSources = true;
        occlusion.autoFindObjectOriginalMaskPoints = true;
        occlusion.maxObjectMaskPoints = 16;
        occlusion.overlayRenderQueue = 3000;
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
