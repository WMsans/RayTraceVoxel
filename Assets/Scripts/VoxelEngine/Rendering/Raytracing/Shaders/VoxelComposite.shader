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
        ZTest LEqual
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
            
            float4 _BlitTexture_TexelSize; // x=1/w, y=1/h, z=w, w=h
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

            // --- FSR 1.0 CORE HELPERS ---

            float3 FsrMin3(float3 a, float3 b, float3 c) { return min(a, min(b, c));
            }
            float3 FsrMax3(float3 a, float3 b, float3 c) { return max(a, max(b, c));
            }

            // Luma approximation (FSR style)
            float FsrLuma(float3 rgb) { return dot(rgb, float3(0.5, 0.5, 0.5));
            } 

            // --- EASU (Edge Adaptive Spatial Upsampling) ---
            float3 FsrEasu(float2 uv)
            {
                float2 texSize = _BlitTexture_TexelSize.zw;
                float2 invTexSize = _BlitTexture_TexelSize.xy;
                
                float2 p = uv * texSize - 0.5;
                float2 fp = floor(p);
                float2 pp = frac(p);
            
                float2 p0 = fp * invTexSize;
                float2 off = invTexSize;

                float3 cF = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_BlitTexture, p0 + float2(0,0)*off, 0).rgb;
                float3 cG = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_BlitTexture, p0 + float2(1,0)*off, 0).rgb;
                float3 cJ = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_BlitTexture, p0 + float2(0,1)*off, 0).rgb;
                float3 cK = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_BlitTexture, p0 + float2(1,1)*off, 0).rgb;

                // 3. Analysis - Direction and Length
                float lF = FsrLuma(cF);
                float lG = FsrLuma(cG);
                float lJ = FsrLuma(cJ);
                float lK = FsrLuma(cK);
                
                float lenX = abs(lF - lG) + abs(lJ - lK);
                float lenY = abs(lF - lJ) + abs(lG - lK);
                
                float edgeMetric = max(lenX, lenY);
                float dirFactor = saturate(edgeMetric * 10.0);

                // Basic Bilinear (Safe)
                float3 colBilinear = lerp(lerp(cF, cG, pp.x), lerp(cJ, cK, pp.x), pp.y);
                float3 colSharp = 0;
                {
                    float3 minColor = FsrMin3(cF, cG, cJ);
                    minColor = min(minColor, cK);
                    
                    float3 maxColor = FsrMax3(cF, cG, cJ);
                    maxColor = max(maxColor, cK);
                    
                    // High-quality sample
                    colSharp = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_BlitTexture, uv, 0).rgb;
                    
                    // 3. APPLY CLAMP
                    colSharp = clamp(colSharp, minColor, maxColor);
                }
                
                return colSharp;
            }

            // --- RCAS (Robust Contrast Adaptive Sharpening) ---
            float3 FsrRcas(float3 col, float2 uv)
            {
                float2 p = _BlitTexture_TexelSize.xy;
                // Sample Cross Neighborhood
                float3 colN = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_BlitTexture, uv + float2(0, -p.y), 0).rgb;
                float3 colW = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_BlitTexture, uv + float2(-p.x, 0), 0).rgb;
                float3 colE = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_BlitTexture, uv + float2(p.x, 0), 0).rgb;
                float3 colS = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_BlitTexture, uv + float2(0, p.y), 0).rgb;
                
                float lumaM = FsrLuma(col);
                float lumaN = FsrLuma(colN);
                float lumaW = FsrLuma(colW);
                float lumaE = FsrLuma(colE);
                float lumaS = FsrLuma(colS);
                
                float mn = min(lumaM, min(min(lumaN, lumaW), min(lumaE, lumaS)));
                float mx = max(lumaM, max(max(lumaN, lumaW), max(lumaE, lumaS)));
                
                // Sharpening Logic
                float scale = lerp(0.0, 2.0, _Sharpness);
                float rcpL = 1.0 / (4.0 * mx - mn + 1.0e-5);
                float amp = saturate(min(mn, 2.0 - mx) * rcpL) * scale;
                amp = sqrt(amp);
                
                float w = amp * -1.0;
                float baseW = 4.0 * w + 1.0;
                float rcpWeight = 1.0 / baseW;
                float3 output = (colN + colW + colE + colS) * w + col;
                return output * rcpWeight;
            }

            FragOutput Frag(Varyings input)
            {
                FragOutput output;
                #if defined(_UPSCALING_FSR)
                    // 1. EASU (Upscaling with Clamping)
                    float3 col = FsrEasu(input.uv);
                    // 2. RCAS (Sharpening)
                    col = FsrRcas(col, input.uv);
                    
                    // [FIX] Sample Alpha separately to preserve transparency
                    float alpha = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_BlitTexture, input.uv, 0).a;
                    output.color = float4(saturate(col), alpha);
                #else
                    // Standard Bilinear
                    output.color = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, input.uv);
                #endif

                if (output.color.a <= 0.0) discard;
                // Pass through depth from the low-res buffer
                output.depth = SAMPLE_TEXTURE2D(_VoxelDepthTexture, sampler_BlitTexture, input.uv).r;
                return output;
            }
            ENDHLSL
        }
    }
}