using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

public static class ForegroundRenderAudit
{
    static bool rendered;
    [MenuItem("Tools/Hand Redirection/Audit Foreground Rendering (Play Mode)")]
    static void Tick()
    {
        if (!EditorApplication.isPlaying) return;
        rendered = false;
        var s = new StringBuilder();
        foreach(var camera in Camera.allCameras)
        {
            var data = camera.GetUniversalAdditionalCameraData();
            s.AppendLine($"Camera {camera.name} type={data.renderType} clearDepth={data.clearDepth} mask={camera.cullingMask}");
            if(data.renderType == CameraRenderType.Base && data.cameraStack != null)
                foreach(var child in data.cameraStack) s.AppendLine($"  Overlay: {child.name}");
        }
        foreach (var c in Object.FindObjectsByType<GoGoInteractionController_NoY3>(FindObjectsSortMode.None))
            s.AppendLine($"Controller {Path(c.transform)} L={Path(c.leftHandRedirector)} R={Path(c.rightHandRedirector)}");
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!(r is SkinnedMeshRenderer) && r.name != "CubeWarped" && r.name != "QuestCase" && r.name != "Gun") continue;
            s.AppendLine($"Renderer {Path(r.transform)} enabled={r.enabled} active={r.gameObject.activeInHierarchy} layer={r.sortingLayerName} order={r.sortingOrder} pos={r.transform.position} bounds={r.bounds}");
            foreach(var m in r.sharedMaterials) if(m) s.AppendLine($"  {m.name} shader={m.shader.name} queue={m.renderQueue}");
        }
        Directory.CreateDirectory("tmp");
        File.WriteAllText("tmp/foreground-render-audit.txt",s.ToString());
        if (!rendered)
        {
            foreach (var skin in Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!Path(skin.transform).Contains("OVRInteractionComprehensive/OVRRightHandVisual/OpenXRRightHand/RightHand")) continue;
                if (!skin.sharedMaterial) continue;
                RenderCheck(skin, true);
                RenderCheck(skin, false);
                rendered = true;
                break;
            }
        }
    }
    static string Path(Transform t) => t == null ? "null" : t.parent == null ? t.name : Path(t.parent)+"/"+t.name;
    static void RenderCheck(SkinnedMeshRenderer skin, bool objectInFront)
    {
        var preview = new PreviewRenderUtility();
        var mesh = new Mesh();
        skin.BakeMesh(mesh);
        var hand = new GameObject("Actual scene hand mesh (preview copy)");
        hand.AddComponent<MeshFilter>().sharedMesh = mesh;
        var hr = hand.AddComponent<MeshRenderer>();
        hr.sharedMaterial = skin.sharedMaterial;
        hr.sortingOrder = skin.sortingOrder;
        preview.AddSingleGO(hand);
        var b = mesh.bounds;
        float size = Mathf.Max(b.size.x, b.size.y, b.size.z);
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        preview.AddSingleGO(cube);
        cube.transform.position = b.center + Vector3.up * size * (objectInFront ? 0.65f : -0.65f);
        cube.transform.localScale = new Vector3(size * 0.5f, size * 0.1f, size * 0.5f);
        var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        material.SetColor("_BaseColor", Color.red);
        cube.GetComponent<Renderer>().sharedMaterial=material;
        var cam=preview.camera;
        cam.orthographic=true;
        cam.orthographicSize=size*0.75f;
        cam.nearClipPlane=0.001f;
        cam.farClipPlane=10;
        cam.transform.position=b.center+Vector3.up*size*3;
        cam.transform.LookAt(b.center,Vector3.forward);
        cam.clearFlags=CameraClearFlags.SolidColor;
        cam.backgroundColor=Color.gray;
        preview.BeginStaticPreview(new Rect(0,0,600,600));
        preview.Render(true);
        var tex=preview.EndStaticPreview();
        File.WriteAllBytes(objectInFront ? "tmp/object-in-front-preview.png" : "tmp/hand-in-front-preview.png",tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        preview.Cleanup();
        Object.DestroyImmediate(mesh);
        Object.DestroyImmediate(material);
    }
}
