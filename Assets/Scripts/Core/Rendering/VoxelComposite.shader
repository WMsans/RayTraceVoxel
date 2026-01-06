Shader "Hidden/VoxelComposite"
{
    Properties
    {
        // URP Blitter binds to _BlitTexture, not _MainTex
        _BlitTexture ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Overlay" "RenderPipeline" = "UniversalPipeline" }
        // Controlled by C# but defaults here
        ZTest Always
        ZWrite On
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            
            TEXTURE2D(_BlitTexture);
            SAMPLER(sampler_BlitTexture);
            
            TEXTURE2D(_VoxelDepthTexture);
            
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

            // Modified Vertex Shader for Procedural Blit
            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                // This generates a full-screen triangle covering clip space
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            FragOutput Frag(Varyings input)
            {
                FragOutput output;
                // _BlitTexture is automatically bound by Blitter.BlitTexture
                half4 color = SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, input.uv);
                
                if (color.a <= 0.0)
                {
                    discard;
                }
                
                output.color = color;
                // Sample depth from our compute shader result
                // We use the same sampler as blit texture (point/bilinear depending on setup, but likely point/bilinear matches)
                output.depth = SAMPLE_TEXTURE2D(_VoxelDepthTexture, sampler_BlitTexture, input.uv).r;
                
                return output;
            }
            ENDHLSL
        }
    }
}