Shader "Voxel/Debug/SDFAtlasSlicer"
{
    Properties
    {
        _MainTex ("SDF Atlas (3D)", 3D) = "" {}
        _Slice ("Z Slice (0-1)", Range(0,1)) = 0.5
        _ShapeIndex ("Shape Index", Int) = 0
        _TotalShapes ("Total Shapes", Int) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler3D _MainTex;
            float _Slice;
            int _ShapeIndex;
            int _TotalShapes;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 1. Calculate the Z height of this specific shape
                float shapeHeight = 1.0 / (float)_TotalShapes;
                
                // 2. Start Z for this shape
                float startZ = (float)_ShapeIndex * shapeHeight;

                // 3. Sample within that block
                // uv.xy are standard quad coordinates
                // uv.z is the slice slider mapped to the shape's block
                float3 uv3 = float3(i.uv.x, i.uv.y, startZ + (_Slice * shapeHeight));

                float dist = tex3D(_MainTex, uv3).r;

                // VISUALIZATION:
                // Red = Inside Object (Negative Distance)
                // Green = Outside Object (Positive Distance)
                // White Line = Surface (Distance approx 0)
                
                if (abs(dist) < 0.01) return fixed4(1,1,1,1); // Surface
                if (dist < 0) return fixed4(1, 0, 0, 1) * abs(dist) * 10; // Inside (Red gradient)
                return fixed4(0, 1, 0, 1) * dist * 10; // Outside (Green gradient)
            }
            ENDCG
        }
    }
}