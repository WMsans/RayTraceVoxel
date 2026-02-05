Shader "VoxelEngine/Grass"
{
    Properties
    {
        [Header(Shading)]
        _BaseColor("Base Color (Root)", Color) = (0.1, 0.3, 0.1, 1)
        _TipColor("Tip Color (Top)", Color) = (0.4, 0.6, 0.2, 1)
        _SpecularColor("Specular Color", Color) = (0.2, 0.5, 0.2, 1)
        
        [Header(Wind)]
        _WindTex("Wind Noise (Grayscale)", 2D) = "white" {}
        _WindSpeed("Wind Speed", Float) = 1.0
        _WindStrength("Wind Strength", Float) = 0.5
        _WindFrequency("Wind Frequency", Float) = 0.1
        _WindDirection("Wind Direction", Vector) = (1, 0.5, 0, 0)

        [Header(Geometry)]
        _BladeHeight("Blade Height Scale", Float) = 1.0
        _BladeWidth("Blade Width Scale", Float) = 1.0
        _Cutoff("Alpha Cutoff", Range(0,1)) = 0.5
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
            
            // Shadow Support
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct GrassInstance
            {
                float3 position;
                float rotation;
                uint packedData; 
            };

            StructuredBuffer<GrassInstance> _GrassInstanceBuffer;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _TipColor;
                float4 _SpecularColor;
                float4 _WindTex_ST;
                float _WindSpeed;
                float _WindStrength;
                float _WindFrequency;
                float4 _WindDirection;
                float _BladeHeight;
                float _BladeWidth;
                float _Cutoff;
            CBUFFER_END

            TEXTURE2D(_WindTex);
            SAMPLER(sampler_WindTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float3 normalOS : NORMAL; // Mesh now has UP normals
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 color : TEXCOORD1;
                float3 normalWS : NORMAL;
                float3 positionWS : TEXCOORD3;
                float4 shadowCoord : TEXCOORD4;
            };

            void setup() {}

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);

                float3 posWS = input.positionOS.xyz;
                float3 instancePos = float3(0,0,0);
                float rotation = 0;
                float heightScale = 1.0;
                float colorVariation = 0.5;

                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                    GrassInstance inst = _GrassInstanceBuffer[input.instanceID];
                    instancePos = inst.position;
                    rotation = inst.rotation;
                    
                    uint p = inst.packedData;
                    heightScale = ((p >> 8) & 0xFF) / 255.0 * 2.0 + 0.5; 
                    colorVariation = ((p >> 16) & 0xFFFF) / 65535.0; 
                #endif

                // Dimensions
                posWS.xz *= _BladeWidth;
                posWS.y *= _BladeHeight * heightScale;

                // Rotation
                float s, c;
                sincos(rotation, s, c);
                float3 rotPos;
                rotPos.x = posWS.x * c + posWS.z * s;
                rotPos.y = posWS.y;
                rotPos.z = posWS.x * -s + posWS.z * c;
                posWS = rotPos;

                float3 worldPos = instancePos + posWS;

                // --- Improved Wind ---
                float2 windUV = (instancePos.xz * _WindFrequency) + (_Time.y * _WindSpeed * _WindDirection.xy);
                float windNoise = SAMPLE_TEXTURE2D_LOD(_WindTex, sampler_WindTex, windUV, 0).r;
                windNoise = (windNoise * 2.0 - 1.0);
                
                // Curve factor: input.uv.y is 0 at bottom, 1 at top.
                // Pow(2) creates a nice parabolic bend.
                float bendFactor = pow(input.uv.y, 2.0);
                
                // Displacement
                worldPos.xz += windNoise * _WindStrength * bendFactor * _WindDirection.xy;
                // Height reduction (keep length consistent-ish)
                worldPos.y -= abs(windNoise) * _WindStrength * 0.3 * bendFactor;

                // --- Output ---
                output.positionCS = TransformWorldToHClip(worldPos);
                output.uv = input.uv;
                output.positionWS = worldPos;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS); // Uses (0,1,0) mostly

                // Shadow Coord
                output.shadowCoord = TransformWorldToShadowCoord(worldPos);

                // Pre-calc Gradient Color
                float3 localBase = lerp(_BaseColor.rgb * 0.5, _BaseColor.rgb, colorVariation);
                // Darken root (Ambient Occlusion effect)
                localBase *= 0.5; 
                output.color = lerp(localBase, _TipColor.rgb, input.uv.y);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // Light Data
                Light mainLight = GetMainLight(input.shadowCoord);
                
                // Half-Lambert Lighting (Softer, better for foliage)
                float NdotL = dot(input.normalWS, mainLight.direction) * 0.5 + 0.5;
                
                // Shadows
                float shadow = mainLight.shadowAttenuation;
                
                // Final Diffuse
                float3 lighting = NdotL * mainLight.color * shadow;
                
                // Simple Ambient (Fake)
                lighting += float3(0.2, 0.25, 0.3) * 0.5; 

                float3 finalColor = input.color * lighting;

                return half4(finalColor, 1.0);
            }
            ENDHLSL
        }
    }
}