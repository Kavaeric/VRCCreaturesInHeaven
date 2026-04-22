Shader "Unlit/ClearDepth"
{
    Properties
    {
        [IntRange] _StencilRef ("Stencil Ref", Range(1, 255)) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+1" }
        LOD 100

        Pass
        {
            ColorMask 0
            ZWrite Off
            ZTest LEqual

            Stencil
            {
                Ref [_StencilRef]
                Comp Always
                Pass Replace
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f   { float4 vertex : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target { return 0; }
            ENDCG
        }

        Pass
        {
            ColorMask 0
            ZWrite On
            ZTest Always

            Stencil
            {
                Ref [_StencilRef]
                Comp Equal
                Pass Keep
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f   { float4 vertex : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                return o;
            }

            #if UNITY_REVERSED_Z
                #define FAR_DEPTH 0.0
                #define DEPTH_SEMANTIC SV_DepthLessEqual
            #else
                #define FAR_DEPTH 1.0
                #define DEPTH_SEMANTIC SV_DepthGreaterEqual
            #endif

            void frag(v2f i, out float4 col : SV_Target, out float depth : DEPTH_SEMANTIC)
            {
                col = 0;
                depth = FAR_DEPTH;
            }
            ENDCG
        }
    }
}
