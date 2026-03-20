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
                float3 position; // Local
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
                float _CelSteps;
                float _ShadowBrightness;
                // [UPDATED] Matrix
                float4x4 _ObjectToWorld;
            CBUFFER_END

            TEXTURE2D(_WindTex);
            SAMPLER(sampler_WindTex);
            TEXTURE2D(_VoxelDepthCopy);
            float4 _VegetationScreenSize; // xy = width/height, zw = 1/width, 1/height

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

            struct FragOutput
            {
                half4 color : SV_Target0;
                float depth : SV_Target1;
                half4 normal : SV_Target2;
            };

            void setup() {}

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);

                float3 posLocalOffset = input.positionOS.xyz;
                float3 instancePosLocal = float3(0, 0, 0);
                float sizeScale = 1.0;
                float colorVariation = 0.5;
                float3 surfaceNormalLocal = float3(0, 1, 0);
                float spinRotation = 0;

                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                    LeafInstance inst = _LeafInstanceBuffer[input.instanceID];
                    instancePosLocal = inst.position;

                    uint pn = inst.packedNormal;
                    surfaceNormalLocal.x = (float)(pn & 0xFF) / 255.0 * 2.0 - 1.0;
                    surfaceNormalLocal.y = (float)((pn >> 8) & 0xFF) / 255.0 * 2.0 - 1.0;
                    surfaceNormalLocal.z = (float)((pn >> 16) & 0xFF) / 255.0 * 2.0 - 1.0;
                    surfaceNormalLocal = normalize(surfaceNormalLocal);
                    spinRotation = (float)((pn >> 24) & 0xFF) / 255.0 * 6.28318;

                    uint p = inst.packedData;
                    sizeScale = ((p >> 8) & 0xFF) / 255.0;
                    colorVariation = ((p >> 16) & 0xFFFF) / 65535.0;
                #endif

                // 1. Scale
                posLocalOffset *= _BladeHeight * (0.5 + sizeScale);

                // 2. Local Spin
                float s, c;
                sincos(spinRotation, s, c);
                float3 spunPos;
                spunPos.x = posLocalOffset.x * c + posLocalOffset.z * s;
                spunPos.y = posLocalOffset.y;
                spunPos.z = posLocalOffset.x * -s + posLocalOffset.z * c;
                posLocalOffset = spunPos;

                // 3. Align to Surface Normal (Local)
                float3 up = surfaceNormalLocal;
                float3 helper = abs(up.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0);
                float3 right = normalize(cross(up, helper));
                float3 forward = cross(right, up);
                
                float3 alignedPos = right * posLocalOffset.x + up * posLocalOffset.y + forward * posLocalOffset.z;
                posLocalOffset = alignedPos;

                float3 totalLocalPos = instancePosLocal + posLocalOffset;

                // [UPDATED] Transform to World Space
                float3 worldPos = mul(_ObjectToWorld, float4(totalLocalPos, 1.0)).xyz;
                float3 rootWorldPos = mul(_ObjectToWorld, float4(instancePosLocal, 1.0)).xyz;
                float3 surfaceNormalWS = normalize(mul((float3x3)_ObjectToWorld, surfaceNormalLocal));

                // Rotated Basis vectors for normal calculation
                float3 rightWS = normalize(mul((float3x3)_ObjectToWorld, right));
                float3 upWS = normalize(mul((float3x3)_ObjectToWorld, up));
                float3 forwardWS = normalize(mul((float3x3)_ObjectToWorld, forward));

                // Old: float2 windUV = (rootWorldPos.xz * _WindFrequency) + (_Time.y * _WindSpeed);
                float2 windUV = (instancePosLocal.xz * _WindFrequency) + (_Time.y * _WindSpeed);
                
                float windNoise = SAMPLE_TEXTURE2D_LOD(_WindTex, sampler_WindTex, windUV, 0).r;
                windNoise = (windNoise * 2.0 - 1.0);
                float flutter = windNoise * _WindStrength * input.uv.y;
                worldPos += surfaceNormalWS * flutter * 0.2;
                worldPos += rightWS * flutter * 0.5;

                // --- Outputs ---
                output.positionCS = TransformWorldToHClip(worldPos);
                output.uv = input.uv;
                output.positionWS = worldPos;
                
                // Approximate normal in WS
                float3 normalWS = rightWS * input.normalOS.x + upWS * input.normalOS.y + forwardWS * input.normalOS.z;
                output.normalWS = normalize(normalWS);
                
                output.terrainNormal = surfaceNormalWS; 

                output.rootShadowCoord = TransformWorldToShadowCoord(rootWorldPos);

                float3 localBase = lerp(_BaseColor.rgb, _BaseColor.rgb * 0.6, colorVariation);
                float3 localTip = lerp(_TipColor.rgb, _TipColor.rgb * 1.2, colorVariation);
                output.color = lerp(localBase, localTip, input.uv.y);

                return output;
            }

            FragOutput frag(Varyings input)
            {
                float2 screenUV = input.positionCS.xy * _VegetationScreenSize.zw;
                float voxelDepth = SAMPLE_TEXTURE2D(_VoxelDepthCopy, sampler_PointClamp, screenUV).r;
                float myDepth = input.positionCS.z;

                #if UNITY_REVERSED_Z
                    if (voxelDepth > 0.0 && myDepth < voxelDepth) discard;
                #else
                    if (voxelDepth < 1.0 && myDepth > voxelDepth) discard;
                #endif

                Light mainLight = GetMainLight(input.rootShadowCoord);
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

                float3 lighting = celDiffuse * mainLight.color;
                float3 ambient = float3(0.2, 0.25, 0.3) * 0.5;
                
                float3 finalColor = input.color * (lighting + ambient);
                
                FragOutput o;
                o.color = half4(finalColor, 1.0);
                o.depth = myDepth;
                o.normal = half4(normalize(input.normalWS) * 0.5 + 0.5, 1.0);
                return o;
            }
            ENDHLSL
        }
    }
}