using UnityEngine;
using UnityEngine.Rendering;

public class ForceClearEyeTargetAlpha : MonoBehaviour
{
    // 透明でクリア（RGBA全部0）
    public Color clearColor = new Color(0, 0, 0, 0);

    void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
    }

    void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
    {
        // XRのメインカメラだけに適用したいなら条件を追加
        // 例: if (!cam.stereoEnabled) return;

        var cmd = CommandBufferPool.Get("ForceClearEyeTargetAlpha");
        // depthもcolorもクリア（α含む）
        cmd.ClearRenderTarget(true, true, clearColor);
        ctx.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }
}
