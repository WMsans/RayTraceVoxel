Shader "Voxel/EdgeBlend"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_SourceTex); SAMPLER(sampler_SourceTex);
            TEXTURE2D(_EdgeSource); SAMPLER(sampler_EdgeSource);
            float _EdgeWidth;

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
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
                half4 full = SAMPLE_TEXTURE2D(_SourceTex, sampler_SourceTex, i.uv);
                half4 edge = SAMPLE_TEXTURE2D(_EdgeSource, sampler_EdgeSource, i.uv);
                float mask = EdgeMask(i.uv);
                return lerp(full, edge, mask);
            }
            ENDHLSL
        }
    }
}
