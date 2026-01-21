Shader "VoxelEngine/Leaf"
{
    Properties
    {
        [Header(Shading)]
        _BaseColor("Inner Color", Color) = (0.05, 0.2, 0.05, 1)
        _TipColor("Outer Color", Color) = (0.1, 0.4, 0.1, 1)
        
        [Header(Wind)]
        _WindTex("Wind Noise", 2D) = "white" {}
        _WindSpeed("Wind Speed", Float) = 0.5
        _WindStrength("Wind Strength", Float) = 0.2
        _WindFrequency("Wind Frequency", Float) = 0.5
        _WindDirection("Wind Direction", Vector) = (1, 0, 1, 0)

        [Header(Geometry)]
        _BladeHeight("Leaf Size", Float) = 0.8
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline" = "UniversalPipeline" }
        LOD 100
        Cull Off 

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:setup

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct LeafInstance
            {
                float3 position;
                float rotation;
                uint packedData;
            };

            StructuredBuffer<LeafInstance> _LeafInstanceBuffer;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _TipColor;
                float4 _WindTex_ST;
                float _WindSpeed;
                float _WindStrength;
                float _WindFrequency;
                float4 _WindDirection;
                float _BladeHeight;
            CBUFFER_END

            TEXTURE2D(_WindTex);
            SAMPLER(sampler_WindTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 color : TEXCOORD1;
            };

            void setup() {}

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);

                float3 posWS = input.positionOS.xyz;
                float3 instancePos = float3(0, 0, 0);
                float rotation = 0;
                float sizeScale = 1.0;
                float colorVariation = 0.5;

                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                    LeafInstance inst = _LeafInstanceBuffer[input.instanceID];
                    instancePos = inst.position;
                    rotation = inst.rotation;

                    uint p = inst.packedData;
                    // Size stored in bits 8-15
                    sizeScale = ((p >> 8) & 0xFF) / 255.0;
                    // Color variation in bits 16-31
                    colorVariation = ((p >> 16) & 0xFFFF) / 65535.0;
                #endif

                // Scale geometry
                posWS *= _BladeHeight * (0.5 + sizeScale); // Randomize size

                // Rotate (Y-Axis)
                float s, c;
                sincos(rotation, s, c);
                float3 rotPos;
                rotPos.x = posWS.x * c + posWS.z * s;
                rotPos.y = posWS.y;
                rotPos.z = posWS.x * -s + posWS.z * c;
                posWS = rotPos;

                float3 worldPos = instancePos + posWS;

                // Wind (Fluttering effect)
                float2 windUV = (instancePos.xz * _WindFrequency) + (_Time.y * _WindSpeed);
                float windNoise = SAMPLE_TEXTURE2D_LOD(_WindTex, sampler_WindTex, windUV, 0).r;
                windNoise = (windNoise * 2.0 - 1.0);
                
                // Leaves flutter more at the tips (UV.y)
                float flutter = windNoise * _WindStrength * input.uv.y;
                worldPos.x += flutter;
                worldPos.z += flutter * 0.5;
                worldPos.y += flutter * 0.2;

                output.positionCS = TransformWorldToHClip(worldPos);
                output.uv = input.uv;

                // Color Variation
                float3 localBase = lerp(_BaseColor.rgb, _BaseColor.rgb * 0.6, colorVariation);
                float3 localTip = lerp(_TipColor.rgb, _TipColor.rgb * 1.2, colorVariation);
                output.color = lerp(localBase, localTip, input.uv.y);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return half4(input.color, 1.0);
            }
            ENDHLSL
        }
    }
}