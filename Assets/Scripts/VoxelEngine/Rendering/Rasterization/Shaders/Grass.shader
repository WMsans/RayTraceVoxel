Shader "VoxelEngine/Grass"
{
    Properties
    {
        [Header(Shading)]
        _BaseColor("Base Color (Root)", Color) = (0.1, 0.3, 0.1, 1)
        _TipColor("Tip Color (Top)", Color) = (0.4, 0.6, 0.2, 1)

        [Header(Cel Shading)]
        _CelSteps("Cel Steps", Float) = 3
        _ShadowBrightness("Shadow Brightness", Float) = 0.2
        
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
                float3 position; // Now Local
                float rotation;
                uint packedData; 
            };

            StructuredBuffer<GrassInstance> _GrassInstanceBuffer;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _TipColor;
                float4 _WindTex_ST;
                float _WindSpeed;
                float _WindStrength;
                float _WindFrequency;
                float4 _WindDirection;
                float _BladeHeight;
                float _BladeWidth;
                float _Cutoff;
                float _CelSteps;
                float _ShadowBrightness;
                // [UPDATED] Add Matrix
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

                float3 localPosOffset = input.positionOS.xyz;
                float3 instanceLocalPos = float3(0,0,0);
                float rotation = 0;
                float heightScale = 1.0;
                float colorVariation = 0.5;
                float3 terrainNormalLocal = float3(0,1,0);

                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                    GrassInstance inst = _GrassInstanceBuffer[input.instanceID];
                    instanceLocalPos = inst.position;
                    rotation = inst.rotation;
                    
                    uint p = inst.packedData;
                    
                    uint packedNormal = p & 0xFFFF;
                    uint h = (p >> 16) & 0xFF;
                    uint c = (p >> 24) & 0xFF;

                    heightScale = h / 255.0 * 2.0 + 0.5; 
                    colorVariation = c / 255.0;
                    float nx = (packedNormal & 0xFF) / 255.0 * 2.0 - 1.0;
                    float nz = ((packedNormal >> 8) & 0xFF) / 255.0 * 2.0 - 1.0;
                    float ny = sqrt(saturate(1.0 - nx*nx - nz*nz));
                    terrainNormalLocal = normalize(float3(nx, ny, nz));
                #endif

                // Dimensions (Local)
                localPosOffset.xz *= _BladeWidth;
                localPosOffset.y *= _BladeHeight * heightScale;

                // Rotation (Local)
                float s, c_rot;
                sincos(rotation, s, c_rot);
                float3 rotPos;
                rotPos.x = localPosOffset.x * c_rot + localPosOffset.z * s;
                rotPos.y = localPosOffset.y;
                rotPos.z = localPosOffset.x * -s + localPosOffset.z * c_rot;
                localPosOffset = rotPos;

                float3 totalLocalPos = instanceLocalPos + localPosOffset;
                
                // [UPDATED] Transform to World Space
                float3 worldPos = mul(_ObjectToWorld, float4(totalLocalPos, 1.0)).xyz;
                float3 rootWorldPos = mul(_ObjectToWorld, float4(instanceLocalPos, 1.0)).xyz;

                // Old: float2 windUV = (rootWorldPos.xz * _WindFrequency) + (_Time.y * _WindSpeed * _WindDirection.xy);
                float2 windUV = (instanceLocalPos.xz * _WindFrequency) + (_Time.y * _WindSpeed * _WindDirection.xy);

                float windNoise = SAMPLE_TEXTURE2D_LOD(_WindTex, sampler_WindTex, windUV, 0).r;
                windNoise = (windNoise * 2.0 - 1.0);
                
                float bendFactor = pow(input.uv.y, 2.0);
                worldPos.xz += windNoise * _WindStrength * bendFactor * _WindDirection.xy;
                worldPos.y -= abs(windNoise) * _WindStrength * 0.3 * bendFactor;

                // --- Output ---
                output.positionCS = TransformWorldToHClip(worldPos);
                output.uv = input.uv;
                output.positionWS = worldPos;
                
                // Rotate Normals
                output.normalWS = normalize(mul((float3x3)_ObjectToWorld, input.normalOS));
                output.terrainNormal = normalize(mul((float3x3)_ObjectToWorld, terrainNormalLocal));

                output.rootShadowCoord = TransformWorldToShadowCoord(rootWorldPos);

                float3 localBase = lerp(_BaseColor.rgb * 0.5, _BaseColor.rgb, colorVariation);
                localBase *= 0.5;
                // Darken root (AO)
                output.color = lerp(localBase, _TipColor.rgb, input.uv.y);

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