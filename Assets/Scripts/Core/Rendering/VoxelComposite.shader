Shader "Hidden/VoxelComposite"
{
    Properties
    {
        _BlitTexture ("Texture", 2D) = "white" {}
        _Sharpness ("Sharpness", Range(0, 1)) = 0.5
    }
    SubShader
    {
        Tags { "RenderType"="Overlay" "RenderPipeline" = "UniversalPipeline" }
        ZTest Always
        ZWrite On
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_local _ _UPSCALING_FSR
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            
            TEXTURE2D(_BlitTexture);
            SAMPLER(sampler_BlitTexture);
            
            TEXTURE2D(_VoxelDepthTexture);
            
            float4 _BlitTexture_TexelSize;
            float _Sharpness;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            struct FragOutput
            {
                half4 color : SV_Target;
                float depth : SV_Depth;
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            // --- CAS (Contrast Adaptive Sharpening) Logic ---
            float3 FsrRcas(float2 uv)
            {
                //    [b]
                // [d][e][f]
                //    [h]
                
                float2 p = _BlitTexture_TexelSize.xy;
                
                float3 e = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv).rgb;
                float3 b = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv + float2(0, -p.y)).rgb;
                float3 d = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv + float2(-p.x, 0)).rgb;
                float3 f = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv + float2(p.x, 0)).rgb;
                float3 h = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv + float2(0, p.y)).rgb;

                float bL = b.g;
                float dL = d.g; float eL = e.g; float fL = f.g; float hL = h.g;

                float mn = min(eL, min(min(bL, dL), min(fL, hL)));
                float mx = max(eL, max(max(bL, dL), max(fL, hL)));
                
                // Scale sharpness: 0.0 (Standard) -> 1.0 (Maximum)
                float scale = lerp(0.0, 2.0, _Sharpness);
                
                // [FIX] Added epsilon (1.0e-5) to prevent division by zero on flat/black areas (prevents NaNs/Noise)
                float rcpL = 1.0 / (4.0 * mx - mn + 1.0e-5);
                
                float amp = saturate(min(mn, 2.0 - mx) * rcpL) * scale;
                amp = sqrt(amp); 
                
                float w = amp * -1.0;
                float baseW = 4.0 * w + 1.0;
                float rcpWeight = 1.0 / baseW;
                
                float3 output = (b + d + f + h) * w + e;
                return output * rcpWeight;
            }

            FragOutput Frag(Varyings input)
            {
                FragOutput output;

                #if defined(_UPSCALING_FSR)
                    float3 col = FsrRcas(input.uv);
                    col = saturate(col);
                    output.color = float4(col, 1.0);
                #else
                    output.color = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, input.uv);
                #endif

                if (output.color.a <= 0.0) discard;
                
                output.depth = SAMPLE_TEXTURE2D(_VoxelDepthTexture, sampler_BlitTexture, input.uv).r;

                return output;
            }
            ENDHLSL
        }
    }
}