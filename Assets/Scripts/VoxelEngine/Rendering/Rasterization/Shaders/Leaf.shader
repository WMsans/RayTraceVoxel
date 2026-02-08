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
                uint packedNormal;
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
                float sizeScale = 1.0;
                float colorVariation = 0.5;
                float3 surfaceNormal = float3(0, 1, 0);
                float spinRotation = 0;

                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                    LeafInstance inst = _LeafInstanceBuffer[input.instanceID];
                    instancePos = inst.position;

                    // Unpack Normal & Spin
                    uint pn = inst.packedNormal;
                    surfaceNormal.x = (float)(pn & 0xFF) / 255.0 * 2.0 - 1.0;
                    surfaceNormal.y = (float)((pn >> 8) & 0xFF) / 255.0 * 2.0 - 1.0;
                    surfaceNormal.z = (float)((pn >> 16) & 0xFF) / 255.0 * 2.0 - 1.0;
                    surfaceNormal = normalize(surfaceNormal);
                    
                    spinRotation = (float)((pn >> 24) & 0xFF) / 255.0 * 6.28318; // 0 to 2PI

                    // Unpack Data
                    uint p = inst.packedData;
                    sizeScale = ((p >> 8) & 0xFF) / 255.0;
                    colorVariation = ((p >> 16) & 0xFFFF) / 65535.0;
                #endif

                // 1. Scale
                posWS *= _BladeHeight * (0.5 + sizeScale);

                // 2. Local Spin (Rotate around Y-axis of the mesh BEFORE aligning to normal)
                // Since the mesh is built along Y, this spins the leaf "in place"
                float s, c;
                sincos(spinRotation, s, c);
                float3 spunPos;
                spunPos.x = posWS.x * c + posWS.z * s;
                spunPos.y = posWS.y;
                spunPos.z = posWS.x * -s + posWS.z * c;
                posWS = spunPos;

                // 3. Align to Surface Normal
                // We want the mesh's UP (0,1,0) to point towards surfaceNormal.
                // We construct a rotation matrix from Basis Vectors.
                float3 up = surfaceNormal;
                // Safe "Right" vector (handle up pointing roughly Y)
                float3 helper = abs(up.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0);
                float3 right = normalize(cross(up, helper));
                float3 forward = cross(right, up);
                
                // Rotate
                // Basis Matrix multiplication: Col0*x + Col1*y + Col2*z
                float3 alignedPos = right * posWS.x + up * posWS.y + forward * posWS.z;
                posWS = alignedPos;

                float3 worldPos = instancePos + posWS;

                // 4. Wind
                float2 windUV = (instancePos.xz * _WindFrequency) + (_Time.y * _WindSpeed);
                float windNoise = SAMPLE_TEXTURE2D_LOD(_WindTex, sampler_WindTex, windUV, 0).r;
                windNoise = (windNoise * 2.0 - 1.0);
                
                // Flutter intensity based on leaf tip (UV.y)
                float flutter = windNoise * _WindStrength * input.uv.y;
                worldPos += surfaceNormal * flutter * 0.2; // Move in/out
                worldPos += right * flutter * 0.5;         // Move side-to-side

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