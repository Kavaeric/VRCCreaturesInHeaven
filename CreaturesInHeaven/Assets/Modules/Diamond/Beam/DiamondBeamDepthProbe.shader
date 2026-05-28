// Diamond - Beam sub-module
// Depth texture probe: renders scene depth as a greyscale gradient.
// Drop on any quad in the scene to confirm _CameraDepthTexture is populated.
// Delete once confirmed working.

Shader "Diamond/DebugDepthProbe"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                float4 screenPos : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex    = UnityObjectToClipPos(v.vertex);
                o.screenPos = ComputeScreenPos(o.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv  = i.screenPos.xy / i.screenPos.w;
                float raw  = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                float linear01 = Linear01Depth(raw);

                // Flat magenta means the texture is missing or entirely zero.
                // A greyscale gradient means it's working.
                if (raw == 0) return fixed4(1, 0, 1, 1);

                return fixed4(linear01, linear01, linear01, 1);
            }
            ENDCG
        }
    }
}
