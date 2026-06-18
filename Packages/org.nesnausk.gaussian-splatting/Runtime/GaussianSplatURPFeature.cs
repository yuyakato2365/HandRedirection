// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

#if !UNITY_6000_0_OR_NEWER
#error Unity Gaussian Splatting URP support only works in Unity 6 or later
#endif

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace GaussianSplatting.Runtime
{
    // Note: I have no idea what is the purpose of ScriptableRendererFeature vs ScriptableRenderPass, which one of those
    // is supposed to do resource management vs logic, etc. etc. Code below "seems to work" but I'm just fumbling along,
    // without understanding any of it.
    //
    // ReSharper disable once InconsistentNaming
    class GaussianSplatURPFeature : ScriptableRendererFeature
    {
        class GSRenderPass : ScriptableRenderPass
        {
            const string GaussianSplatRTName = "_GaussianSplatRT";

            const string ProfilerTag = "GaussianSplatRenderGraph";
            static readonly ProfilingSampler s_profilingSampler = new(ProfilerTag);
            static readonly int s_gaussianSplatRT = Shader.PropertyToID(GaussianSplatRTName);

            class PassData
            {
                internal UniversalCameraData CameraData;
                internal TextureHandle SourceTexture;
                internal TextureHandle SourceDepth;
                internal TextureHandle GaussianSplatRT;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                using var builder = renderGraph.AddUnsafePass(ProfilerTag, out PassData passData);

                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();

                RenderTextureDescriptor rtDesc = cameraData.cameraTargetDescriptor;
                rtDesc.depthBufferBits = 0;
                rtDesc.msaaSamples = 1;
                rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                var textureHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, rtDesc, GaussianSplatRTName, true);

                passData.CameraData = cameraData;
                passData.SourceTexture = resourceData.activeColorTexture;
                passData.SourceDepth = resourceData.activeDepthTexture;
                passData.GaussianSplatRT = textureHandle;

                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(resourceData.activeDepthTexture);
                builder.UseTexture(textureHandle, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    using var _ = new ProfilingScope(commandBuffer, s_profilingSampler);
                    commandBuffer.SetGlobalTexture(s_gaussianSplatRT, data.GaussianSplatRT);
                    CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT, data.SourceDepth, ClearFlag.Color, Color.clear);
                    Material matComposite = GaussianSplatRenderSystem.instance.SortAndRenderSplats(data.CameraData.camera, commandBuffer);
                    commandBuffer.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
                    Blitter.BlitCameraTexture(commandBuffer, data.GaussianSplatRT, data.SourceTexture, matComposite, 0);
                    commandBuffer.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
                });
            }
        }

        GSRenderPass m_Pass;
        bool m_HasCamera;
        float m_NextFeatureDiagnosticTime;
        bool m_LogCurrentCameraPass;

        public override void Create()
        {
            m_Pass = new GSRenderPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_HasCamera = false;
            bool shouldLog = Time.realtimeSinceStartup >= m_NextFeatureDiagnosticTime;
            if (shouldLog)
                m_NextFeatureDiagnosticTime = Time.realtimeSinceStartup + 1f;
            m_LogCurrentCameraPass = shouldLog;

            var system = GaussianSplatRenderSystem.instance;
            if (!system.GatherSplatsForCamera(cameraData.camera))
            {
                if (shouldLog)
                {
                    Debug.LogWarning(
                        $"[GaussianSplatDiagnostics] URP OnCameraPreCull no splats camera={cameraData.camera.name} " +
                        $"cameraType={cameraData.camera.cameraType} renderer={renderer?.GetType().Name ?? "<null>"} " +
                        $"renderType={cameraData.renderType} target={cameraData.camera.targetTexture?.name ?? "<backbuffer>"}");
                }
                return;
            }

            m_HasCamera = true;
            if (shouldLog)
            {
                Debug.Log(
                    $"[GaussianSplatDiagnostics] URP OnCameraPreCull found splats camera={cameraData.camera.name} " +
                    $"cameraType={cameraData.camera.cameraType} renderer={renderer?.GetType().Name ?? "<null>"} " +
                    $"renderType={cameraData.renderType} target={cameraData.camera.targetTexture?.name ?? "<backbuffer>"}");
            }
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!m_HasCamera)
            {
                if (m_LogCurrentCameraPass)
                {
                    Debug.LogWarning(
                        $"[GaussianSplatDiagnostics] URP AddRenderPasses skipped camera={renderingData.cameraData.camera.name} " +
                        $"cameraType={renderingData.cameraData.camera.cameraType} renderer={renderer?.GetType().Name ?? "<null>"}");
                }
                return;
            }
            if (m_LogCurrentCameraPass)
            {
                Debug.Log(
                    $"[GaussianSplatDiagnostics] URP AddRenderPasses enqueue camera={renderingData.cameraData.camera.name} " +
                    $"cameraType={renderingData.cameraData.camera.cameraType} renderer={renderer?.GetType().Name ?? "<null>"}");
            }
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass = null;
        }
    }
}

#endif // #if GS_ENABLE_URP
