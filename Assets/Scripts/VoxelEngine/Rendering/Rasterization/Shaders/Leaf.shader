Shader "VoxelEngine/Leaf"
{
    Properties
    {
        [Header(Shading)]
        _BaseColor("Inner Color", Color) = (0.05, 0.2, 0.05, 1)
        _TipColor("Outer Color", Color) = (0.1, 0.4, 0.1, 1)

        [Header(Cel Shading)]
        _CelSteps("Cel Steps", Float) = 3
        _ShadowBrightness("Shadow Brightness", Float) = 0.2
        
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
            
            // Shadow Support
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

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
                // Cel Shading
                float _CelSteps;
                float _ShadowBrightness;
            CBUFFER_END

            TEXTURE2D(_WindTex);
            SAMPLER(sampler_WindTex);
            // Texture for manual occlusion (from VoxelRaytracerFeature)
            TEXTURE2D(_VoxelDepthCopy);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float3 normalOS : NORMAL;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 color : TEXCOORD1;
                float3 normalWS : NORMAL;
                float3 positionWS : TEXCOORD3;
                float4 rootShadowCoord : TEXCOORD4;
                float3 terrainNormal : TEXCOORD5;
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

                // 2. Local Spin
                float s, c;
                sincos(spinRotation, s, c);
                float3 spunPos;
                spunPos.x = posWS.x * c + posWS.z * s;
                spunPos.y = posWS.y;
                spunPos.z = posWS.x * -s + posWS.z * c;
                posWS = spunPos;

                // 3. Align to Surface Normal
                float3 up = surfaceNormal;
                float3 helper = abs(up.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0);
                float3 right = normalize(cross(up, helper));
                float3 forward = cross(right, up);
                
                float3 alignedPos = right * posWS.x + up * posWS.y + forward * posWS.z;
                posWS = alignedPos;

                float3 worldPos = instancePos + posWS;

                // 4. Wind
                float2 windUV = (instancePos.xz * _WindFrequency) + (_Time.y * _WindSpeed);
                float windNoise = SAMPLE_TEXTURE2D_LOD(_WindTex, sampler_WindTex, windUV, 0).r;
                windNoise = (windNoise * 2.0 - 1.0);
                
                float flutter = windNoise * _WindStrength * input.uv.y;
                worldPos += surfaceNormal * flutter * 0.2; 
                worldPos += right * flutter * 0.5;

                // --- Outputs ---
                output.positionCS = TransformWorldToHClip(worldPos);
                output.uv = input.uv;
                output.positionWS = worldPos;
                
                // Approximate normal in WS (rotate object normal by alignment basis)
                // Note: alignedPos calculation effectively used [right, up, forward] as rotation matrix columns
                float3 normalWS = right * input.normalOS.x + up * input.normalOS.y + forward * input.normalOS.z;
                output.normalWS = normalize(normalWS);
                
                output.terrainNormal = surfaceNormal; // For back-lighting

                // Shadow Coord based on ROOT position for stability
                output.rootShadowCoord = TransformWorldToShadowCoord(instancePos);

                // Color Variation
                float3 localBase = lerp(_BaseColor.rgb, _BaseColor.rgb * 0.6, colorVariation);
                float3 localTip = lerp(_TipColor.rgb, _TipColor.rgb * 1.2, colorVariation);
                output.color = lerp(localBase, localTip, input.uv.y);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // [Occlusion Test against Voxel Depth]
                float2 screenUV = input.positionCS.xy / _ScaledScreenParams.xy;
                // Use sampler_PointClamp (provided by URP Core) for depth sampling
                float voxelDepth = SAMPLE_TEXTURE2D(_VoxelDepthCopy, sampler_PointClamp, screenUV).r;
                
                float myDepth = input.positionCS.z;

                #if UNITY_REVERSED_Z
                    // 1.0 is Near, 0.0 is Far. Smaller = Further.
                    if (voxelDepth > 0.0 && myDepth < voxelDepth) discard;
                #else
                    // 0.0 is Near, 1.0 is Far. Larger = Further.
                    if (voxelDepth < 1.0 && myDepth > voxelDepth) discard;
                #endif

                // 1. Get Main Light
                Light mainLight = GetMainLight(input.rootShadowCoord);

                // 2. Cel Shading Logic
                float NdotL_Raw = dot(input.terrainNormal, mainLight.direction);
                float shadow = mainLight.shadowAttenuation;
                float litVal = max(NdotL_Raw, 0.0) * shadow;

                float steps = max(1.0, _CelSteps);
                float minBrightness = _ShadowBrightness;
                
                float t = litVal * steps;
                float stepIndex = floor(t);
                float fraction = t - stepIndex;
                float smoothFraction = smoothstep(0.0, 0.05 * steps, fraction);
                float rawLevel = (stepIndex + smoothFraction) / steps;
                
                float celDiffuse = lerp(minBrightness, 1.0, saturate(rawLevel));

                // 3. Final Color
                float3 lighting = celDiffuse * mainLight.color;
                float3 ambient = float3(0.2, 0.25, 0.3) * 0.5;
                
                float3 finalColor = input.color * (lighting + ambient);

                return half4(finalColor, 1.0);
            }
            ENDHLSL
        }
    }
}