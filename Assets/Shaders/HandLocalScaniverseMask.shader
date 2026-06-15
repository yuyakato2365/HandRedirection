Shader "HandRedirection/Hand Local Scaniverse Mask"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _LeftHandPos ("Left Hand Position", Vector) = (9999, 9999, 9999, 0)
        _RightHandPos ("Right Hand Position", Vector) = (9999, 9999, 9999, 0)
        _LeftForearmStart ("Left Forearm Start", Vector) = (9999, 9999, 9999, 0)
        _LeftForearmEnd ("Left Forearm End", Vector) = (9999, 9999, 9999, 0)
        _LeftForearmRight ("Left Forearm Right", Vector) = (1, 0, 0, 0)
        _LeftForearmUp ("Left Forearm Up", Vector) = (0, 1, 0, 0)
        _RightForearmStart ("Right Forearm Start", Vector) = (9999, 9999, 9999, 0)
        _RightForearmEnd ("Right Forearm End", Vector) = (9999, 9999, 9999, 0)
        _RightForearmRight ("Right Forearm Right", Vector) = (1, 0, 0, 0)
        _RightForearmUp ("Right Forearm Up", Vector) = (0, 1, 0, 0)
        _ForearmBoxHalfSize ("Forearm Box Half Size", Vector) = (0.3, 0.3, 0, 0)
        _ForearmBoxFeather ("Forearm Box Feather", Float) = 0.07
        _ForearmBoxDepthBias ("Forearm Box Depth Bias", Float) = 0.02
        _Radius ("Hand Radius", Float) = 0.18
        _Feather ("Feather", Float) = 0.04
        _DepthBias ("Hand Depth Bias", Float) = 0.02
        _ObjectMaskRadius ("Object Mask Radius", Float) = 0.18
        _ObjectMaskFeather ("Object Mask Feather", Float) = 0.04
        _ObjectMaskDepthBias ("Object Mask Depth Bias", Float) = 0.02
        _MaskSoftness ("Mask Softness", Float) = 1
        _Opacity ("Opacity", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry-50"
            "RenderType" = "Transparent"
        }

        Pass
        {
            Name "HandLocalMask"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float4 _LeftHandPos;
                float4 _RightHandPos;
                float4 _LeftForearmStart;
                float4 _LeftForearmEnd;
                float4 _LeftForearmRight;
                float4 _LeftForearmUp;
                float4 _RightForearmStart;
                float4 _RightForearmEnd;
                float4 _RightForearmRight;
                float4 _RightForearmUp;
                float4 _ForearmBoxHalfSize;
                float _ForearmBoxFeather;
                float _ForearmBoxDepthBias;
                float _Radius;
                float _Feather;
                float _DepthBias;
                float4 _ObjectMaskPositions[16];
                int _ObjectMaskCount;
                float _ObjectMaskRadius;
                float _ObjectMaskFeather;
                float _ObjectMaskDepthBias;
                float _MaskSoftness;
                float _Opacity;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            float ViewConeMask(float3 worldPosition, float3 fragmentDirection, float fragmentDistance, float radius, float featherMeters, float depthBias)
            {
                float3 cameraToPoint = worldPosition - _WorldSpaceCameraPos.xyz;
                float pointDistance = length(cameraToPoint);
                if (pointDistance >= 1000.0)
                    return 0.0;

                float angularRadius = atan2(max(radius, 0.001), max(pointDistance, 0.001));
                float angularFeather = atan2(max(featherMeters, 0.0001), max(pointDistance, 0.001));
                float angle = acos(saturate(dot(fragmentDirection, cameraToPoint / max(pointDistance, 0.0001))));
                float behindPoint = step(pointDistance + depthBias, fragmentDistance);
                return (1.0 - smoothstep(max(angularRadius - angularFeather, 0.0), angularRadius, angle)) * behindPoint;
            }

            float ForearmBoxMask(
                float3 start,
                float3 end,
                float3 rightAxis,
                float3 upAxis,
                float3 fragmentDirection,
                float fragmentDistance,
                float halfWidth,
                float halfHeight,
                float featherMeters,
                float depthBias)
            {
                if (start.x >= 1000.0 || end.x >= 1000.0)
                    return 0.0;

                float3 segment = end - start;
                float segmentLength = length(segment);
                if (segmentLength <= 0.001)
                    return 0.0;

                float radius = max(max(halfWidth, halfHeight), 0.001);
                float mask = 0.0;
                [unroll]
                for (int i = 0; i < 9; i++)
                {
                    float t = i / 8.0;
                    float3 samplePosition = lerp(start, end, t);
                    mask = max(mask, ViewConeMask(samplePosition, fragmentDirection, fragmentDistance, radius, featherMeters, depthBias));
                }

                return mask;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 cameraToFragment = input.positionWS - _WorldSpaceCameraPos.xyz;
                float fragmentDistance = length(cameraToFragment);
                float3 fragmentDirection = cameraToFragment / max(fragmentDistance, 0.0001);

                float mask = max(
                    ViewConeMask(_LeftHandPos.xyz, fragmentDirection, fragmentDistance, _Radius, _Feather, _DepthBias),
                    ViewConeMask(_RightHandPos.xyz, fragmentDirection, fragmentDistance, _Radius, _Feather, _DepthBias));

                mask = max(mask, ForearmBoxMask(
                    _LeftForearmStart.xyz,
                    _LeftForearmEnd.xyz,
                    _LeftForearmRight.xyz,
                    _LeftForearmUp.xyz,
                    fragmentDirection,
                    fragmentDistance,
                    _ForearmBoxHalfSize.x,
                    _ForearmBoxHalfSize.y,
                    _ForearmBoxFeather,
                    _ForearmBoxDepthBias));

                mask = max(mask, ForearmBoxMask(
                    _RightForearmStart.xyz,
                    _RightForearmEnd.xyz,
                    _RightForearmRight.xyz,
                    _RightForearmUp.xyz,
                    fragmentDirection,
                    fragmentDistance,
                    _ForearmBoxHalfSize.x,
                    _ForearmBoxHalfSize.y,
                    _ForearmBoxFeather,
                    _ForearmBoxDepthBias));

                int objectMaskCount = min(_ObjectMaskCount, 16);
                for (int i = 0; i < objectMaskCount; i++)
                {
                    mask = max(mask, ViewConeMask(_ObjectMaskPositions[i].xyz, fragmentDirection, fragmentDistance, _ObjectMaskRadius, _ObjectMaskFeather, _ObjectMaskDepthBias));
                }

                mask = pow(saturate(mask), max(_MaskSoftness, 0.001));
                clip(mask - 0.001);

                half4 color = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                color.a = _Opacity * mask;
                return color;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
