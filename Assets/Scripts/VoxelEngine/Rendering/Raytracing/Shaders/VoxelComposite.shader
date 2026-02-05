Shader "Hidden/VoxelComposite"
{
    Properties
    {
        _BlitTexture ("Texture", 2D) = "white" {}
        _Sharpness ("Sharpness", Range(0, 1)) = 0.5
        
        // Exposed properties for material inspection/defaults
        _OutlineColor ("Outline Color", Color) = (0,0,0,1)
        _OutlineParams ("Outline Params", Vector) = (1, 0.01, 0, 0)
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
            #pragma multi_compile_local _ _OUTLINE_ON
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            
            TEXTURE2D(_BlitTexture);
            SAMPLER(sampler_BlitTexture);
            TEXTURE2D(_VoxelDepthTexture);
            
            float4 _BlitTexture_TexelSize; // x=1/w, y=1/h, z=w, w=h
            float _Sharpness;
            // Outline Uniforms
            float4 _OutlineColor;
            float4 _OutlineParams;
            // x: thickness, y: threshold

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
            float3 FsrMin3(float3 a, float3 b, float3 c) { return min(a, min(b, c)); }
            float3 FsrMax3(float3 a, float3 b, float3 c) { return max(a, max(b, c)); }
            float FsrLuma(float3 rgb) { return dot(rgb, float3(0.5, 0.5, 0.5)); } 

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
                float lF = FsrLuma(cF); float lG = FsrLuma(cG);
                float lJ = FsrLuma(cJ); float lK = FsrLuma(cK);
                float lenX = abs(lF - lG) + abs(lJ - lK);
                float lenY = abs(lF - lJ) + abs(lG - lK);
                float edgeMetric = max(lenX, lenY);
                // float dirFactor = saturate(edgeMetric * 10.0); // Unused in basic Easu implementation here
                float3 colBilinear = lerp(lerp(cF, cG, pp.x), lerp(cJ, cK, pp.x), pp.y);
                float3 colSharp = 0;
                {
                    float3 minColor = FsrMin3(cF, cG, cJ);
                    minColor = min(minColor, cK);
                    float3 maxColor = FsrMax3(cF, cG, cJ); maxColor = max(maxColor, cK);
                    colSharp = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_BlitTexture, uv, 0).rgb;
                    colSharp = clamp(colSharp, minColor, maxColor);
                }
                return colSharp;
            }

            float3 FsrRcas(float3 col, float2 uv)
            {
                float2 p = _BlitTexture_TexelSize.xy;
                float3 colN = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_BlitTexture, uv + float2(0, -p.y), 0).rgb;
                float3 colW = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_BlitTexture, uv + float2(-p.x, 0), 0).rgb;
                float3 colE = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_BlitTexture, uv + float2(p.x, 0), 0).rgb;
                float3 colS = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_BlitTexture, uv + float2(0, p.y), 0).rgb;
                float lumaM = FsrLuma(col); float lumaN = FsrLuma(colN); float lumaW = FsrLuma(colW); float lumaE = FsrLuma(colE); float lumaS = FsrLuma(colS);
                float mn = min(lumaM, min(min(lumaN, lumaW), min(lumaE, lumaS)));
                float mx = max(lumaM, max(max(lumaN, lumaW), max(lumaE, lumaS)));
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
                // 1. Fetch Depth (needed for both output and outline)
                float rawDepth = SAMPLE_TEXTURE2D(_VoxelDepthTexture, sampler_BlitTexture, input.uv).r;
                float currentDepth = rawDepth;

                // 2. Fetch/Compute Color
                #if defined(_UPSCALING_FSR)
                    float3 col = FsrEasu(input.uv);
                    col = FsrRcas(col, input.uv);
                    float alpha = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_BlitTexture, input.uv, 0).a;
                #else
                    float4 rawCol = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, input.uv);
                    float3 col = rawCol.rgb;
                    float alpha = rawCol.a;
                #endif

                // 3. Apply Outline
                #if defined(_OUTLINE_ON)
                    float2 e = _BlitTexture_TexelSize.xy * _OutlineParams.x;
                    
                    // Fetch Linear depths
                    float depth = LinearEyeDepth(currentDepth, _ZBufferParams);
                    float du = LinearEyeDepth(SAMPLE_TEXTURE2D(_VoxelDepthTexture, sampler_BlitTexture, input.uv + float2(0, -e.y)).r, _ZBufferParams);
                    float dr = LinearEyeDepth(SAMPLE_TEXTURE2D(_VoxelDepthTexture, sampler_BlitTexture, input.uv + float2(e.x, 0)).r, _ZBufferParams);
                    float dd = LinearEyeDepth(SAMPLE_TEXTURE2D(_VoxelDepthTexture, sampler_BlitTexture, input.uv + float2(0, e.y)).r, _ZBufferParams);
                    float dl = LinearEyeDepth(SAMPLE_TEXTURE2D(_VoxelDepthTexture, sampler_BlitTexture, input.uv + float2(-e.x, 0)).r, _ZBufferParams);

                    float depth_diff = 0.0;
                    float neg_depth_diff = 0.5;

                    // [FIX] Use Relative Depth Difference
                    // Dividing by 'depth' ensures that distant objects (where derivatives are large in world units)
                    // do not trigger the threshold. This fixes the "whole world outline" issue.
                    float invDepth = 1.0 / (depth + 1e-6);

                    depth_diff += clamp((du - depth) * invDepth, 0.0, 1.0);
                    depth_diff += clamp((dd - depth) * invDepth, 0.0, 1.0);
                    depth_diff += clamp((dr - depth) * invDepth, 0.0, 1.0);
                    depth_diff += clamp((dl - depth) * invDepth, 0.0, 1.0);

                    neg_depth_diff += (depth - du) * invDepth;
                    neg_depth_diff += (depth - dd) * invDepth;
                    neg_depth_diff += (depth - dr) * invDepth;
                    neg_depth_diff += (depth - dl) * invDepth;

                    neg_depth_diff = clamp(neg_depth_diff, 0.0, 1.0);
                    // smoothstep(0.5, 0.5, x) behaves like a hard step function
                    neg_depth_diff = clamp(step(0.5, neg_depth_diff) * 10.0, 0.0, 1.0);

                    // A threshold of 0.2 now represents a ~20% relative change in depth, 
                    // which is consistent regardless of distance.
                    float outlineVal = smoothstep(0.2, 0.3, depth_diff);
                    
                    // Combine with negative depth diff if you want ridges, 
                    // otherwise strictly follow user snippet which only used depth_diff for lerp.
                    col = lerp(col, _OutlineColor.rgb, outlineVal * _OutlineColor.a);
                #endif

                if (alpha <= 0.0) discard;
                output.color = float4(saturate(col), alpha);
                output.depth = currentDepth;
                return output;
            }
            ENDHLSL
        }
    }
}