Shader "VoxelEngine/Grass"
{
    Properties
    {
        [Header(Shading)]
        _BaseColor("Base Color (Root)", Color) = (0.1, 0.3, 0.1, 1)
        _TipColor("Tip Color (Top)", Color) = (0.4, 0.6, 0.2, 1)
        
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
        Cull Off // Draw both sides of the blades

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

            // --- Structure matching C# GrassInstance ---
            // Stride = 20 bytes (float3 + float + uint)
            struct GrassInstance
            {
                float3 position;
                float rotation;
                uint packedData; // [Color 16] [Height 8] [Type 8]
            };

            // Read-Only Buffer from Compute Shader
            StructuredBuffer<GrassInstance> _GrassInstanceBuffer;

            // --- Uniforms ---
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

            // Mandatory for procedural instancing
            void setup()
            {
                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                    // We don't use the unity_ObjectToWorld matrix because we transform manually in vertex shader.
                    // However, for shadows/depth passes, Unity might expect this to be set.
                    // For now, we leave it simple.
                #endif
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);

                float3 posWS = input.positionOS.xyz;
                float3 instancePos = float3(0, 0, 0);
                float rotation = 0;
                float heightScale = 1.0;
                float colorVariation = 0.5;

                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                    // 1. Fetch Instance Data
                    GrassInstance inst = _GrassInstanceBuffer[input.instanceID];
                    instancePos = inst.position;
                    rotation = inst.rotation;

                    // 2. Unpack Data
                    // Packed: [Color 16] [Height 8] [Type 8]
                    uint p = inst.packedData;
                    
                    // Height (0-255 mapped to 0.5x - 2.5x)
                    uint hRaw = (p >> 8) & 0xFF;
                    heightScale = (hRaw / 255.0) * 2.0 + 0.5; 

                    // Color Var (0-65535 mapped to 0.0 - 1.0)
                    uint cRaw = (p >> 16) & 0xFFFF;
                    colorVariation = cRaw / 65535.0; 
                #endif

                // 3. Apply Dimensions
                posWS.xz *= _BladeWidth;
                posWS.y *= _BladeHeight * heightScale;

                // 4. Apply Rotation (Y-Axis)
                float s, c;
                sincos(rotation, s, c);
                float3 rotPos;
                rotPos.x = posWS.x * c + posWS.z * s;
                rotPos.y = posWS.y;
                rotPos.z = posWS.x * -s + posWS.z * c;
                posWS = rotPos;

                // 5. Move to World Space
                float3 worldPos = instancePos + posWS;

                // 6. Wind Displacement (Vertex Shader)
                // Sample noise based on world position and time
                float2 windUV = (instancePos.xz * _WindFrequency) + (_Time.y * _WindSpeed * _WindDirection.xy);
                float windNoise = SAMPLE_TEXTURE2D_LOD(_WindTex, sampler_WindTex, windUV, 0).r;
                
                // Remap 0..1 to -1..1
                windNoise = (windNoise * 2.0 - 1.0);

                // Pin the root: Multiply by UV.y (0 at bottom, 1 at top)
                // Using pow(uv.y, 2) creates a nice curve where the tip bends more than the middle
                float bendFactor = pow(input.uv.y, 2.0);
                
                worldPos.xz += windNoise * _WindStrength * bendFactor * _WindDirection.xy;
                
                // Slight Y depression to simulate bending down (simple approximation)
                worldPos.y -= abs(windNoise) * _WindStrength * 0.2 * bendFactor;

                // 7. Output
                output.positionCS = TransformWorldToHClip(worldPos);
                output.uv = input.uv;

                // 8. Calculate Color
                // Darken the root color slightly based on random variation
                float3 localBase = lerp(_BaseColor.rgb * 0.5, _BaseColor.rgb, colorVariation);
                // Gradient from Base (Root) to Tip
                output.color = lerp(localBase, _TipColor.rgb, input.uv.y);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // Simple Unlit/Gradient output
                // Can be upgraded to Lit if normals are processed
                return half4(input.color, 1.0);
            }
            ENDHLSL
        }
    }
}