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

        StartCoroutine(LogState());
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
