Shader "Hidden/VoxelGodRays"
{
    Properties
    {
        _BlitTexture("Texture", 2D) = "white" {}
        _OcclusionTex("Occlusion", 2D) = "black" {}
        _LightColor("Light Color", Color) = (1, 0.95, 0.8, 1)
        _LightPosition("Light Position UV", Vector) = (0.5, 0.5, 0, 0)
        _SunThreshold("Sun Threshold", Range(0, 1)) = 0.95
        _Density("Density", Float) = 1.0
        _Decay("Decay", Float) = 0.95
        _Weight("Weight", Float) = 0.01
        _Exposure("Exposure", Float) = 1.0
        _Samples("Samples", Int) = 32
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZTest Always ZWrite Off Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

        TEXTURE2D(_BlitTexture); SAMPLER(sampler_BlitTexture);
        TEXTURE2D(_VoxelDepthTexture); SAMPLER(sampler_VoxelDepthTexture); 
        
        float4 _BlitTexture_TexelSize;
        float4 _LightColor;
        float4 _LightPosition; // xy = uv, z = visible (0/1)
        float _SunThreshold;
        float _Density;
        float _Decay;
        float _Weight;
        float _Exposure;
        int _Samples;

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
        ENDHLSL

        // --- PASS 0: Occluder Map Generation ---
        Pass
        {
            Name "OccluderMap"
            
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragOccluder

            float4 FragOccluder(Varyings input) : SV_Target
            {
                // 1. Sample Depth
                float rawDepth = SAMPLE_TEXTURE2D(_VoxelDepthTexture, sampler_VoxelDepthTexture, input.uv).r;
                
                // [FIX] Use Linear01Depth to reliably detect sky/background
                float linearDepth = Linear01Depth(rawDepth, _ZBufferParams);
                bool isSky = linearDepth > 0.9;

                // 2. Identify Objects vs Sky
                if (!isSky)
                {
                    // It's an object (voxel/geometry), so it occludes the light (return black)
                    return float4(0, 0, 0, 0);
                }

                // 3. Sun/Sky Rendering
                // We only want the sun disk itself to emit god rays, not the whole sky.
                float dist = distance(input.uv, _LightPosition.xy);
                
                // Note: _SunThreshold should be high (e.g., 0.95)
                if (dist > (1.0 - _SunThreshold)) 
                {
                   return float4(0, 0, 0, 0);
                }

                return float4(_LightColor.rgb, 0.0);
            }
            ENDHLSL
        }

        // --- PASS 1: Radial Blur ---
        Pass
        {
            Name "RadialBlur"
            
            HLSLPROGRAM
            // *** FIX BELOW: Combined #pragma vertex Vert onto one line ***
            #pragma vertex Vert
            #pragma fragment FragBlur

            float4 FragBlur(Varyings input) : SV_Target
            {
                if (_LightPosition.z < 0.5) return float4(0,0,0,0);
                
                float2 uv = input.uv;
                float2 lightPos = _LightPosition.xy;
                
                float2 deltaTextCoord = uv - lightPos;
                deltaTextCoord *= 1.0 / float(_Samples) * _Density;
                
                float3 color = 0;
                float illuminationDecay = 1.0;

                for (int i = 0; i < _Samples; i++)
                {
                    uv -= deltaTextCoord;
                    float3 sampleCol = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv).rgb;
                    
                    sampleCol *= illuminationDecay * _Weight;
                    color += sampleCol;
                    illuminationDecay *= _Decay;
                }
                
                return float4(color * _Exposure, 0.0);
            }
            ENDHLSL
        }

        // --- PASS 2: Additive Blend --- 
        Pass
        {
            Name "AdditiveBlend"
            Blend One One 
            
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlend

            float4 FragBlend(Varyings input) : SV_Target
            {
                return SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, input.uv);
            }
            ENDHLSL
        }
    }
}