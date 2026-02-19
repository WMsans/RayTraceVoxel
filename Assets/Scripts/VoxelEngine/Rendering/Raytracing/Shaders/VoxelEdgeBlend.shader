Shader "Voxel/EdgeBlend"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
      
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            TEXTURE2D(_BlitTexture);
            SAMPLER(sampler_BlitTexture);
            TEXTURE2D(_EdgeSource); 
            SAMPLER(sampler_EdgeSource);
            
            float _EdgeWidth;

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

            float EdgeMask(float2 uv)
            {
                float2 centered = abs(uv * 2.0 - 1.0);
                float edge = max(centered.x, centered.y);
                float start = 1.0 - _EdgeWidth;
                return smoothstep(start, 1.0, edge);
            }

            half4 Frag(Varyings i) : SV_Target
            {
                half4 full = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, i.uv);
                half4 edge = SAMPLE_TEXTURE2D(_EdgeSource, sampler_EdgeSource, i.uv);
                float mask = EdgeMask(i.uv);
                return lerp(full, edge, mask);
            }
            ENDHLSL
        }
    }
}