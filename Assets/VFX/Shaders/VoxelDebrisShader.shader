Shader "VFX/VoxelDebris"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _AlbedoArray ("Albedo Array", 2DArray) = "white" {}
        _MaterialID ("Material ID", Int) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D_ARRAY(_AlbedoArray);
            SAMPLER(sampler_AlbedoArray);

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(int, _MaterialID)
            UNITY_INSTANCING_BUFFER_END(Props)

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = input.uv;
                output.color = input.color;
                
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                
                int matId = UNITY_ACCESS_INSTANCED_PROP(Props, _MaterialID);
                half4 texColor = SAMPLE_TEXTURE2D_ARRAY(_AlbedoArray, sampler_AlbedoArray, input.uv, matId);
                
                return texColor * input.color;
            }
            ENDHLSL
        }
    }
}
