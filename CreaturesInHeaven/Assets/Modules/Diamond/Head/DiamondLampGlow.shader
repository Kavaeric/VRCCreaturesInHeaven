// DiamondLampGlow.shader
//
// The additive glow pass for a fixture's lamp lens. This is the "on" look of the
// lamp: baked colour x brightness added on top of whatever the lens's off/material
// shader already drew.
//
// Usage: apply to a submesh of a lamp fixture model that sits atop the lens.
//
// Two paths, gated by DIAMOND_LIGHTSHOW_TEX (same keyword the beam shaders use):
//   ON  (play mode) -- sample this fixture's colour row from the bake texture.
//   OFF (edit mode) -- read a plain _EmissionColor, which DiamondFixtureMapPreview
//                      writes from the live proxy transforms. This is what lets the
//                      lamp glow prewview while scrubbing the clip in edit mode (Udon
//                      doesn't run then, so the texture path has no frame to read).
// DiamondManager forces the keyword on in play; DiamondFixtureMapPreview forces it off
// in edit. They never run simultaneously, so the material's saved state doesn't matter.
Shader "Diamond/LampGlow"
{
    Properties
    {
        // Per-instance addressing, seeded once by DiamondManager into the lamp lens's
        // MaterialPropertyBlock (same values it seeds into the beam block):
        //   _FixtureRow -- this fixture's row band in the bake texture
        //   _ShowIndex  -- which manager/show owns it (its slot in the frame array)
        _FixtureRow ("Fixture row", Float) = 0
        _ShowIndex  ("Show index", Float) = 0

        // Edit-mode preview glow (colour x brightness), written per-fixture by
        // DiamondFixtureMapPreview. Only read when DIAMOND_LIGHTSHOW_TEX is off.
        [HDR] _EmissionColor ("Emission (edit preview)", Color) = (0,0,0,1)

        // Overall glow multiplier (art control, per material). Lets the lens glow be
        // tuned relative to the beam without re-baking. 1 = exactly the baked colour.
        _GlowScale ("Glow scale", Float) = 1.0
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Blend One One
        ZWrite Off
        Cull Back

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 5.0
            #pragma multi_compile_instancing
            // Same keyword the beam shaders use: ON = texture path, OFF = edit-preview
            // (_EmissionColor) path. multi_compile_local so both variants always build
            // and the keyword can be toggled at runtime / edit time.
            #pragma multi_compile_local __ DIAMOND_LIGHTSHOW_TEX

            #include "UnityCG.cginc"

            // Baked lightshow sampler: the texture globals, addressing math, and frame
            // lerp, shared verbatim with the beam shaders (DiamondBeamCommon.cginc also
            // includes this) so the lamp glow and the beam colour can't sample the show
            // differently. Only the DIAMOND_LIGHTSHOW_TEX body compiles; the edit-preview
            // path below is used when the keyword is off.
            #include "../Lightshow/DiamondLightshowSample.cginc"

#ifndef DIAMOND_LIGHTSHOW_TEX
            // Edit-preview path: the glow colour comes straight from _EmissionColor,
            // which is written from the live proxy transforms.
            float4    _EmissionColor;
#endif

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float, _FixtureRow)
                UNITY_DEFINE_INSTANCED_PROP(float, _ShowIndex)
                UNITY_DEFINE_INSTANCED_PROP(float, _GlowScale)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                // Baked colour x brightness for this fixture at the current frame,
                // sampled once in vert (a per-instance constant, flat across the mesh).
                float3 glow : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                o.vertex = UnityObjectToClipPos(v.vertex);

                float glowScale = UNITY_ACCESS_INSTANCED_PROP(Props, _GlowScale);

#ifdef DIAMOND_LIGHTSHOW_TEX
                // Play mode: sample this fixture's colour row from the bake texture.
                // DiamondSampleColour is the shared sampler (DiamondLightshowSample.cginc).
                // This row is also read by DiamondBeam.
                float fixtureRow = UNITY_ACCESS_INSTANCED_PROP(Props, _FixtureRow);
                float showIndex  = UNITY_ACCESS_INSTANCED_PROP(Props, _ShowIndex);
                o.glow = DiamondSampleColour(fixtureRow, showIndex) * glowScale;
#else
                // Edit mode: DiamondFixtureMapPreview writes the driven colour (colour x
                // brightness) into _EmissionColor from the live proxies.
                o.glow = _EmissionColor.rgb * glowScale;
#endif
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                // Additive: alpha is ignored by Blend One One, but keep it 1 for clarity.
                return half4(i.glow, 1);
            }

            ENDCG
        }
    }
}
