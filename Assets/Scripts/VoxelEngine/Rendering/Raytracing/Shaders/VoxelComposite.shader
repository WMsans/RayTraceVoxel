Shader "Hidden/VoxelComposite"
{
    Properties
    {
        _BlitTexture ("Texture", 2D) = "white" {}
        _Sharpness ("Sharpness", Range(0, 1)) = 0.5
        _OutlineParams ("Outline Params", Vector) = (1, 0.5, 0, 0)
        _OutlineColor ("Outline Color", Color) = (0,0,0,1)
        _NormalOutlineParams ("Normal Outline Params", Vector) = (0.6, 0.5, 50.0, 0)
        _NormalOutlineColor ("Normal Outline Color", Color) = (1,1,1,1)
        _MainLightDirection ("Main Light Direction", Vector) = (0,1,0,0)
        _OutlineShadowStrength ("Outline Shadow Strength", Float) = 0.5
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
            TEXTURE2D(_VoxelNormalTexture);
            
            float4 _BlitTexture_TexelSize;
            float _Sharpness;
            float4 _OutlineParams;
            // x: thickness, y: strength
            float4 _OutlineColor;
            float4 _NormalOutlineParams;
            // x: threshold, y: strength, z: maxDistance
            float4 _NormalOutlineColor;
            
            float4 _MainLightDirection;
            float _OutlineShadowStrength;

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
            float FsrLuma(float3 rgb) { return dot(rgb, float3(0.5, 0.5, 0.5));
            } 

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
                // 1. Fetch Depth
                float rawDepth = SAMPLE_TEXTURE2D(_VoxelDepthTexture, sampler_BlitTexture, input.uv).r;
                float currentDepth = rawDepth;

                // 2. Fetch Color
                #if defined(_UPSCALING_FSR)
                    float3 col = FsrEasu(input.uv);
                    col = FsrRcas(col, input.uv);
                    float alpha = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_BlitTexture, input.uv, 0).a;
                #else
                    float4 rawCol = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, input.uv);
                    float3 col = rawCol.rgb;
                    float alpha = rawCol.a;
                #endif

                // 3. Apply Outline (Depth + Normal)
                #if defined(_OUTLINE_ON)
                    float2 e = _BlitTexture_TexelSize.xy * _OutlineParams.x;
                    
                    // --- Sample Normal for Light Calculation ---
                    float3 normal = SAMPLE_TEXTURE2D(_VoxelNormalTexture, sampler_BlitTexture, input.uv).xyz * 2.0 - 1.0;
                    
                    // --- Calculate Light Factor ---
                    // MainLightDirection (passed from C#) is the ray direction (points FROM light TO world).
                    // We need vector TO light for NdotL, so negate it.
                    float3 lightDir = normalize(-_MainLightDirection.xyz);
                    float NdotL = saturate(dot(normal, lightDir));
                    
                    // Interpolate between (1.0 - Strength) and 1.0
                    // If Strength is 0, factor is always 1 (no change).
                    // If Strength is 1, shadow factor is 0 (black outlines in shadow).
                    float lightFactor = lerp(1.0 - _OutlineShadowStrength, 1.0, NdotL);
                    
                    float3 finalDepthOutlineCol = _OutlineColor.rgb * lightFactor;
                    float3 finalNormalOutlineCol = _NormalOutlineColor.rgb * lightFactor;

                    // --- A. Depth Based Highlighting ---
                    float depth = LinearEyeDepth(currentDepth, _ZBufferParams);
                    float du = LinearEyeDepth(SAMPLE_TEXTURE2D(_VoxelDepthTexture, sampler_BlitTexture, input.uv + float2(0, -e.y)).r, _ZBufferParams);
                    float dr = LinearEyeDepth(SAMPLE_TEXTURE2D(_VoxelDepthTexture, sampler_BlitTexture, input.uv + float2(e.x, 0)).r, _ZBufferParams);
                    float dd = LinearEyeDepth(SAMPLE_TEXTURE2D(_VoxelDepthTexture, sampler_BlitTexture, input.uv + float2(0, e.y)).r, _ZBufferParams);
                    float dl = LinearEyeDepth(SAMPLE_TEXTURE2D(_VoxelDepthTexture, sampler_BlitTexture, input.uv + float2(-e.x, 0)).r, _ZBufferParams);
                    float depth_diff = 0.0;
                    float invDepth = 1.0 / (depth + 1e-6);
                    depth_diff += clamp((du - depth) * invDepth, 0.0, 1.0);
                    depth_diff += clamp((dd - depth) * invDepth, 0.0, 1.0);
                    depth_diff += clamp((dr - depth) * invDepth, 0.0, 1.0);
                    depth_diff += clamp((dl - depth) * invDepth, 0.0, 1.0);
                    float depthEdge = smoothstep(0.2, 0.3, depth_diff);

                    // --- B. Normal Based Highlighting ---
                    float neighborDepths[4] = { du, dd, dr, dl };
                    float2 offsets[4] = {
                        float2(0, -e.y),
                        float2(0, e.y),
                        float2(e.x, 0),
                        float2(-e.x, 0)
                    };
                    float normal_sum = 0.0;
                    
                    [unroll]
                    for (int i = 0; i < 4; i++) 
                    {
                        float3 n = SAMPLE_TEXTURE2D(_VoxelNormalTexture, sampler_BlitTexture, input.uv + offsets[i]).xyz * 2.0 - 1.0;
                        float3 normal_diff = normal - n;
                        float diffSq = dot(normal_diff, normal_diff);
                        // [Fix] 1-Pixel Thickness Logic
                        
                        float d_neighbor = neighborDepths[i];
                        float d_diff = d_neighbor - depth;
                        // Positive if neighbor is background
                        
                        // Condition 1: Silhouette (Neighbor is deeper/background).
                        bool isForeground = (d_diff > 0.001);
                        // Condition 2: Crease (Depths are roughly equal).
                        bool isCrease = (abs(d_diff) <= 0.001);
                        bool biasPass = (i == 1 || i == 2);
                        if (isForeground || (isCrease && biasPass))
                        {
                            normal_sum += diffSq;
                        }
                    }

                    float indicator = sqrt(normal_sum);
                    float normalThreshold = _NormalOutlineParams.x;
                    float maxDistance = max(0.01, _NormalOutlineParams.z);
                    
                    float distFactor = saturate(depth / maxDistance);
                    float dynThreshold = lerp(normalThreshold, 5.0, distFactor);
                    
                    float normalEdge = step(dynThreshold, indicator);

                    // --- C. Combine ---
                    // Apply Depth Outline
                    float3 depthMix = lerp(col, finalDepthOutlineCol, _OutlineParams.y);
                    col = lerp(col, depthMix, depthEdge);
                    
                    // Apply Normal Outline (Layered on top)
                    float3 normalMix = lerp(col, finalNormalOutlineCol, _NormalOutlineParams.y);
                    col = lerp(col, normalMix, normalEdge * (1.0 - depthEdge));

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