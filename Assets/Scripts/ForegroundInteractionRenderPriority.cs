using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Hands, objects and the desk share ordinary depth and original materials in one overlay.
public sealed class ForegroundInteractionRenderPriority : MonoBehaviour
{
    const int Layer = 31;
    public static bool PriorityEnabled { get; private set; } = true;
    readonly HashSet<Renderer> targets = new HashSet<Renderer>();
    readonly Dictionary<GameObject,int> layers = new Dictionary<GameObject,int>();
    Camera source, overlay;
    UniversalAdditionalCameraData data;
    int mask;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetState() => PriorityEnabled = true;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (FindAnyObjectByType<ForegroundInteractionRenderPriority>() == null)
            new GameObject("Foreground Interaction Render Priority").AddComponent<ForegroundInteractionRenderPriority>();
    }
    public static void SetPriorityEnabled(bool value)
    {
        PriorityEnabled = value;
        var instance = FindAnyObjectByType<ForegroundInteractionRenderPriority>();
        if (instance != null) instance.Configure();
    }
    void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += Begin;
        RenderPipelineManager.endCameraRendering += End;
        RenderPipelineManager.endContextRendering += EndContext;
    }
    IEnumerator Start()
    {
        yield return null;
        while (true) { Discover(); Configure(); yield return new WaitForSecondsRealtime(2); }
    }
    void AddRoot(Transform root)
    {
        if (root != null) foreach(var r in root.GetComponentsInChildren<Renderer>(true)) targets.Add(r);
    }
    void Discover()
    {
        foreach(var c in FindObjectsByType<GoGoInteractionController_NoY3>(FindObjectsInactive.Include,FindObjectsSortMode.None))
        {
            AddRoot(c.leftHandRedirector); AddRoot(c.rightHandRedirector);
            if(c.objects != null) foreach(var entry in c.objects) if(entry != null) AddRoot(entry.warpedObject);
        }
        foreach(var h in FindObjectsByType<DeformHandle>(FindObjectsInactive.Include,FindObjectsSortMode.None)) AddRoot(h.transform);
        foreach(var challenge in FindObjectsByType<ScalePlacementChallengeController>(FindObjectsInactive.Include,FindObjectsSortMode.None))
            if(challenge.RingRenderer != null) targets.Add(challenge.RingRenderer);
        foreach(var s in FindObjectsByType<DeskScaleSliderPanel>(FindObjectsInactive.Include,FindObjectsSortMode.None))
        {
            AddRoot(s.transform);
            // Use the configured desk, not DeskOrigin/room ancestors: the desk must
            // write depth in the same camera as hands to occlude them naturally.
            AddRoot(s.primaryDeskReference);
        }
        foreach(var v in FindObjectsByType<Oculus.Interaction.HandVisual>(FindObjectsInactive.Include,FindObjectsSortMode.None))
            foreach(string name in new[]{"_skinnedMeshRenderer","_openXRSkinnedMeshRenderer"})
            {
                var field=typeof(Oculus.Interaction.HandVisual).GetField(name,System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
                var r=field?.GetValue(v) as Renderer;
                if(r != null) targets.Add(r);
            }
        foreach(var d in FindObjectsByType<AvatarHandTrackingDriver>(FindObjectsInactive.Include,FindObjectsSortMode.None))
        {
            AddRoot(d.leftHand?.hand); AddRoot(d.rightHand?.hand);
            foreach(var skin in FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include,FindObjectsSortMode.None))
                foreach(var b in skin.bones)
                    if(b != null && (b==d.leftHand?.hand || b==d.rightHand?.hand)) { targets.Add(skin); break; }
        }
    }
    void Configure()
    {
        Restore();
        if(source == null)
        {
            var rig=FindAnyObjectByType<OVRCameraRig>();
            source=rig != null && rig.centerEyeAnchor != null ? rig.centerEyeAnchor.GetComponent<Camera>() : Camera.main;
            if(source == null) return;
            data=source.GetUniversalAdditionalCameraData();
            if(data.renderType != CameraRenderType.Base || data.cameraStack == null)
            { Debug.LogError("Foreground overlay requires URP camera stacking."); source=null; return; }
            mask=source.cullingMask;
            var go=new GameObject("Interaction Depth Overlay Camera");
            go.transform.SetParent(source.transform,false);
            overlay=go.AddComponent<Camera>(); overlay.CopyFrom(source);
            overlay.cullingMask=1<<Layer;
            var extra=overlay.GetUniversalAdditionalCameraData();
            // Newly created URP camera data defaults to clearDepth=true (read-only API).
            extra.renderType=CameraRenderType.Overlay;
        }
        overlay.enabled=PriorityEnabled;
        if(PriorityEnabled)
        {
            if(!data.cameraStack.Contains(overlay)) data.cameraStack.Add(overlay);
            source.cullingMask=mask & ~(1<<Layer);
        }
        else { data.cameraStack.Remove(overlay); source.cullingMask=mask; }
    }
    void Begin(ScriptableRenderContext context,Camera camera)
    {
        if(!PriorityEnabled || camera != source || overlay == null) return;
        overlay.nearClipPlane=source.nearClipPlane; overlay.farClipPlane=source.farClipPlane;
        overlay.fieldOfView=source.fieldOfView; overlay.projectionMatrix=source.projectionMatrix;
        // Render-only reassignment: restore before any subsequent physics/update.
        foreach(var r in targets)
        {
            if(r==null) continue;
            var go=r.gameObject;
            if(!layers.ContainsKey(go)) layers.Add(go,go.layer);
            go.layer=Layer;
        }
    }
    void End(ScriptableRenderContext context,Camera camera) { if(camera==overlay) Restore(); }
    void EndContext(ScriptableRenderContext context,List<Camera> cameras) => Restore();
    void Restore() { foreach(var pair in layers) if(pair.Key!=null) pair.Key.layer=pair.Value; layers.Clear(); }
    void OnDisable()
    {
        Restore();
        RenderPipelineManager.beginCameraRendering-=Begin; RenderPipelineManager.endCameraRendering-=End;
        RenderPipelineManager.endContextRendering-=EndContext;
        if(data!=null && data.cameraStack!=null) data.cameraStack.Remove(overlay);
        if(source!=null) source.cullingMask=mask;
        if(overlay!=null) Destroy(overlay.gameObject);
        source=null; overlay=null; data=null;
    }
}
