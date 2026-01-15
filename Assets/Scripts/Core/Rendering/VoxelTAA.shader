Shader "Hidden/VoxelTAA"
{
    Properties
    {
        _BlitTexture ("Current Frame", 2D) = "black" {} // Renamed from _MainTex
        _HistoryTex ("History Frame", 2D) = "black" {}
        _MotionVectorTexture ("Motion Vectors", 2D) = "black" {}
        _Blend ("Blend Factor", Range(0, 1)) = 0.9
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "Temporal Anti-Aliasing"
            
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            // [FIX] Use _BlitTexture to match Blitter.BlitTexture binding
            TEXTURE2D(_BlitTexture);            SAMPLER(sampler_BlitTexture);
            TEXTURE2D(_HistoryTex);             SAMPLER(sampler_HistoryTex);
            TEXTURE2D(_MotionVectorTexture);    SAMPLER(sampler_MotionVectorTexture);

            float4 _BlitTexture_TexelSize; // x=1/w, y=1/h, z=w, w=h
            float _Blend;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            // 3x3 Neighborhood Clamping to prevent ghosting
            float3 ClipHistory(float3 history, float3 current, float2 uv)
            {
                float3 minColor = float3(1000, 1000, 1000);
                float3 maxColor = float3(-1000, -1000, -1000);

                // Sample 3x3 neighborhood
                for(int x = -1; x <= 1; x++)
                {
                    for(int y = -1; y <= 1; y++)
                    {
                        float2 offset = float2(x, y) * _BlitTexture_TexelSize.xy;
                        // [FIX] Sample _BlitTexture
                        float3 neighbor = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv + offset).rgb;
                        minColor = min(minColor, neighbor);
                        maxColor = max(maxColor, neighbor);
                    }
                }

                // Clamp history to the range of the current neighborhood
                return clamp(history, minColor, maxColor);
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;

                // 1. Sample Current Frame (Jittered)
                // [FIX] Sample _BlitTexture
                float3 colorCurrent = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv).rgb;

                // 2. Read Motion Vector
                float2 motion = SAMPLE_TEXTURE2D(_MotionVectorTexture, sampler_MotionVectorTexture, uv).xy;
                float2 prevUV = uv - motion;

                // 3. Sample History (Reprojection)
                if (any(prevUV < 0.0) || any(prevUV > 1.0))
                {
                    return float4(colorCurrent, 1.0);
                }
                
                float3 colorHistory = SAMPLE_TEXTURE2D(_HistoryTex, sampler_HistoryTex, prevUV).rgb;

                // 4. Neighborhood Clamping (Anti-Ghosting)
                colorHistory = ClipHistory(colorHistory, colorCurrent, uv);

                // 5. Blending
                float3 result = lerp(colorCurrent, colorHistory, _Blend);

                return float4(result, 1.0);
            }
            ENDHLSL
        }
    }
}