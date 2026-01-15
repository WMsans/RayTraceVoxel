Shader "Hidden/VoxelFXAA"
{
    Properties
    {
        _BlitTexture ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZTest Always ZWrite Off Cull Off
        // [FIX] Enable Blending to preserve skybox background
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "FXAA"
          
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            TEXTURE2D(_BlitTexture);
            SAMPLER(sampler_BlitTexture);
            
            float4 _BlitTexture_TexelSize;

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

            // --- FXAA 3.11 Implementation ---
            #define FXAA_SPAN_MAX 8.0
            #define FXAA_REDUCE_MUL (1.0/8.0)
            #define FXAA_REDUCE_MIN (1.0/128.0)

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float2 rcpFrame = _BlitTexture_TexelSize.xy;
                
                // [FIX] Sample Alpha to pass it through
                float alpha = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv).a;

                float3 rgbM = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv).rgb;
                float3 rgbNW = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv + float2(-1, -1) * rcpFrame).rgb;
                float3 rgbNE = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv + float2( 1, -1) * rcpFrame).rgb;
                float3 rgbSW = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv + float2(-1,  1) * rcpFrame).rgb;
                float3 rgbSE = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv + float2( 1,  1) * rcpFrame).rgb;
                
                const float3 lumaCoef = float3(0.299, 0.587, 0.114);
                float lumaNW = dot(rgbNW, lumaCoef);
                float lumaNE = dot(rgbNE, lumaCoef);
                float lumaSW = dot(rgbSW, lumaCoef);
                float lumaSE = dot(rgbSE, lumaCoef);
                float lumaM  = dot(rgbM,  lumaCoef);

                float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
                float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));
                
                float range = lumaMax - lumaMin;
                if (range < max(FXAA_REDUCE_MIN, lumaMax * FXAA_REDUCE_MUL))
                {
                    return float4(rgbM, alpha);
                }

                float dirSWMinusNE = lumaSW - lumaNE;
                float dirSEMinusNW = lumaSE - lumaNW;
                
                float2 dir;
                dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
                dir.y =  ((lumaNW + lumaSW) - (lumaNE + lumaSE));
                float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * (0.25 * FXAA_REDUCE_MUL), FXAA_REDUCE_MIN);
                float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
                
                dir = min(float2(FXAA_SPAN_MAX, FXAA_SPAN_MAX),
                      max(float2(-FXAA_SPAN_MAX, -FXAA_SPAN_MAX),
                      dir * rcpDirMin)) * rcpFrame;
                
                float3 rgbA = 0.5 * (
                    SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv + dir * (1.0/3.0 - 0.5)).rgb +
                    SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv + dir * (2.0/3.0 - 0.5)).rgb);
                float3 rgbB = rgbA * 0.5 + 0.25 * (
                    SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv + dir * -0.5).rgb +
                    SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv + dir * 0.5).rgb);
                float lumaB = dot(rgbB, lumaCoef);

                if ((lumaB < lumaMin) || (lumaB > lumaMax))
                {
                    return float4(rgbA, alpha);
                }
                else
                {
                    return float4(rgbB, alpha);
                }
            }
            ENDHLSL
        }
    }
}