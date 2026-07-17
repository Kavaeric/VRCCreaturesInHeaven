// ExteriorFog module
// Pseudo-volumetric world fog driven by the camera depth pass.
//
// Put this material on a box that encloses an interior the player stands in. Scene
// geometry inside the box stays clear; geometry outside fades into the fog colour by
// how far past the box wall it sits. The box is the fog boundary, not the camera.
//
// Requires _CameraDepthTexture, which VRChat only generates when something enables it
// (the module's Udon component or the VUdon Depth Buffer Toolkit sets
// VRCCameraSettings.ScreenCamera.depthTextureMode |= DepthTextureMode.Depth).

Shader "ExteriorFog/ExteriorFog"
{
    Properties
    {
        _Color ("Fog Colour", Color) = (0.6, 0.7, 0.8, 1)

        // Metres outside the box over which fog builds to _MaxDensity.
        _VisibilityDistance ("Visibility Distance", Float) = 20

        // Opacity ceiling. Below 1 leaves the outside world faintly visible through the haze.
        _MaxDensity ("Max Density", Range(0, 1)) = 1

        // 1 = plain exponential falloff; higher builds fog up faster near the box wall.
        _FalloffPower ("Falloff Power", Range(0.25, 4)) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }

        Pass
        {
            // Player is inside the box, so its visible faces are the back faces.
            Cull Front
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            fixed4 _Color;
            float  _VisibilityDistance;
            float  _MaxDensity;
            float  _FalloffPower;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 vertex    : SV_POSITION;
                float4 screenPos : TEXCOORD0;
                float3 worldRay  : TEXCOORD1; // camera -> vertex, world space
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex    = UnityObjectToClipPos(v.vertex);
                o.screenPos = ComputeScreenPos(o.vertex);
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldRay  = worldPos - _WorldSpaceCameraPos;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv  = i.screenPos.xy / i.screenPos.w;
                float  raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);

                // Reconstruct the scene geometry's world position. LinearEyeDepth is distance
                // along the camera forward axis, so scale the view ray by depth / its forward
                // component.
                float3 rayDir     = i.worldRay;
                float  camForward = dot(rayDir, -UNITY_MATRIX_V[2].xyz);
                float  sceneDepth = LinearEyeDepth(raw);
                float3 worldPos   = _WorldSpaceCameraPos + rayDir * (sceneDepth / max(camForward, 1e-4));

                // In the box's object space a unit cube spans [-0.5, 0.5]; |coord| > 0.5 is
                // outside. Object space handles the box's position, rotation and scale for free.
                float3 objPos = mul(unity_WorldToObject, float4(worldPos, 1.0)).xyz;
                float3 outAmt = max(abs(objPos) - 0.5, 0.0); // overshoot past the wall, per axis

                // Convert the object-space overshoot back to world metres so _VisibilityDistance
                // stays in real units regardless of box scale.
                float3 axisWorldLen = float3(
                    length(unity_ObjectToWorld._m00_m10_m20),
                    length(unity_ObjectToWorld._m01_m11_m21),
                    length(unity_ObjectToWorld._m02_m12_m22));
                float outsideDist = length(outAmt * axisWorldLen);

                // Inside the box (outsideDist == 0) stays clear; outside fogs exponentially.
                float d       = outsideDist / max(_VisibilityDistance, 1e-4);
                float density = 1.0 - exp(-pow(d, _FalloffPower));

                float alpha = saturate(density) * _MaxDensity * _Color.a;
                return fixed4(_Color.rgb, alpha);
            }
            ENDCG
        }
    }
}
