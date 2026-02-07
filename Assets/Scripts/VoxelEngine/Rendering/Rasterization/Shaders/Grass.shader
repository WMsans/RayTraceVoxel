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
                float3 position;
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
                
                // Cel Shading
                float _CelSteps;
                float _ShadowBrightness;
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
                float4 rootShadowCoord : TEXCOORD4; // Shadow coord for the root position
                float3 terrainNormal : TEXCOORD5;   // Normal of the terrain for back-side shading
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
                float3 terrainNormal = float3(0,1,0);

                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                    GrassInstance inst = _GrassInstanceBuffer[input.instanceID];
                    instancePos = inst.position;
                    rotation = inst.rotation;
                    
                    uint p = inst.packedData;
                    
                    // Unpack Data:
                    // Bits 00-15: Normal (XZ packed)
                    // Bits 16-23: Height
                    // Bits 24-31: Color Var
                    
                    uint packedNormal = p & 0xFFFF;
                    uint h = (p >> 16) & 0xFF;
                    uint c = (p >> 24) & 0xFF;

                    heightScale = h / 255.0 * 2.0 + 0.5; // Map 0..1 to 0.5..2.5
                    colorVariation = c / 255.0;

                    // Unpack Normal
                    float nx = (packedNormal & 0xFF) / 255.0 * 2.0 - 1.0;
                    float nz = ((packedNormal >> 8) & 0xFF) / 255.0 * 2.0 - 1.0;
                    // Reconstruct Y (assuming it's upward facing)
                    float ny = sqrt(saturate(1.0 - nx*nx - nz*nz));
                    terrainNormal = normalize(float3(nx, ny, nz));
                #endif

                // Dimensions
                posWS.xz *= _BladeWidth;
                posWS.y *= _BladeHeight * heightScale;

                // Rotation
                float s, c_rot;
                sincos(rotation, s, c_rot);
                float3 rotPos;
                rotPos.x = posWS.x * c_rot + posWS.z * s;
                rotPos.y = posWS.y;
                rotPos.z = posWS.x * -s + posWS.z * c_rot;
                posWS = rotPos;

                float3 worldPos = instancePos + posWS;

                // --- Improved Wind ---
                float2 windUV = (instancePos.xz * _WindFrequency) + (_Time.y * _WindSpeed * _WindDirection.xy);
                float windNoise = SAMPLE_TEXTURE2D_LOD(_WindTex, sampler_WindTex, windUV, 0).r;
                windNoise = (windNoise * 2.0 - 1.0);
                
                // Curve factor: input.uv.y is 0 at bottom, 1 at top.
                float bendFactor = pow(input.uv.y, 2.0);
                
                // Displacement
                worldPos.xz += windNoise * _WindStrength * bendFactor * _WindDirection.xy;
                worldPos.y -= abs(windNoise) * _WindStrength * 0.3 * bendFactor;

                // --- Output ---
                output.positionCS = TransformWorldToHClip(worldPos);
                output.uv = input.uv;
                output.positionWS = worldPos;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS); // Grass blade normal (mostly UP)
                output.terrainNormal = terrainNormal; // Pass terrain normal for lighting

                // Shadow Coord based on ROOT position
                // This ensures the whole blade gets the same shadow value as the ground it stands on.
                output.rootShadowCoord = TransformWorldToShadowCoord(instancePos);

                // Pre-calc Gradient Color
                float3 localBase = lerp(_BaseColor.rgb * 0.5, _BaseColor.rgb, colorVariation);
                localBase *= 0.5; // Darken root (AO)
                output.color = lerp(localBase, _TipColor.rgb, input.uv.y);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // 1. Get Main Light using ROOT shadow coordinates
                Light mainLight = GetMainLight(input.rootShadowCoord);
                
                // 2. Cel Shading Logic (Matching Voxel Raytracer)
                // Use Terrain Normal for NdotL to ensure back-side of hill is dark
                float NdotL_Raw = dot(input.terrainNormal, mainLight.direction);
                
                // Attenuate NdotL by shadow (If root is in shadow, shadowAttenuation is 0)
                // We multiply NdotL by shadow BEFORE stepping to ensure shadowed areas fall into the darkest band.
                float shadow = mainLight.shadowAttenuation;
                float litVal = max(NdotL_Raw, 0.0) * shadow;

                float steps = max(1.0, _CelSteps);
                float minBrightness = _ShadowBrightness;
                
                // Calculate Steps
                float t = litVal * steps;
                float stepIndex = floor(t);
                float fraction = t - stepIndex;
                float smoothFraction = smoothstep(0.0, 0.05 * steps, fraction);
                float rawLevel = (stepIndex + smoothFraction) / steps;
                
                // Final Stepped Diffuse
                float celDiffuse = lerp(minBrightness, 1.0, saturate(rawLevel));

                // 3. Final Color
                float3 lighting = celDiffuse * mainLight.color;
                
                // Add fake ambient
                float3 ambient = float3(0.2, 0.25, 0.3) * 0.5;
                
                float3 finalColor = input.color * (lighting + ambient);

                return half4(finalColor, 1.0);
            }
            ENDHLSL
        }
    }
}