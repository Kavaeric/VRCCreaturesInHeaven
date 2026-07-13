// DiamondLampGlow.shader
//
// The additive glow pass for a fixture's lamp lens. This is the "on" look of the
// lamp: baked colour x brightness added on top of whatever the lens's off/material
// shader already drew.
//
// Usage: apply to a submesh of a lamp fixture model that sits atop the lens.
//
// Two paths, selected at runtime by the _UdonDiamondLightshowEnabled uniform (same selector the
// beam shaders use), lerped in vert:
//   1 (play mode) -- sample this fixture's colour row from the bake texture.
//   0 (edit mode) -- read a plain _EmissionColor, which DiamondFixtureMapPreview writes from the
//                    live proxy transforms. This is what lets the lamp glow preview while
//                    scrubbing the clip in edit mode (Udon doesn't run then, so the texture path
//                    has no frame to read).
// DiamondManager sets the global to 1 in play; DiamondFixtureMapPreview sets it (0 or 1) in edit.
// It's a global uniform, not saved material state, so there's no stale-keyword risk between them.
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
        // DiamondFixtureMapPreview. Only selected when _UdonDiamondLightshowEnabled is 0.
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
            // Same runtime selector the beam shaders use: _UdonDiamondLightshowEnabled == 1 =>
            // texture path, 0 => proxy/edit-preview (_EmissionColor) path. A uniform, not a
            // shader keyword, so nothing toggles sticky material state; the manager (and the
            // editor preview) sets one global.

            #include "UnityCG.cginc"

            // Baked lightshow sampler: the texture globals, addressing math, and frame
            // lerp, shared verbatim with the beam shaders (DiamondBeamCommon.cginc also
            // includes this) so the lamp glow and the beam colour can't sample the show
            // differently. Always compiled; the runtime selector below picks whether its
            // result is used.
            #include "../Lightshow/DiamondLightshowSample.cginc"

            // Proxy/edit path: the glow colour comes straight from _EmissionColor, which is
            // written from the live proxy transforms (by the manager at runtime, or the editor
            // preview in edit mode). Always declared now; selected against the texture path on
            // _UdonDiamondLightshowEnabled.
            float4    _EmissionColor;

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

                // Texture path: sample this fixture's colour row from the bake texture.
                // DiamondSampleColour is the shared sampler (DiamondLightshowSample.cginc); this
                // is the same row DiamondBeam reads. Always evaluated (a couple of texture Loads,
                // cheap per-vertex), then selected against the proxy colour below.
                float fixtureRow = UNITY_ACCESS_INSTANCED_PROP(Props, _FixtureRow);
                float showIndex  = UNITY_ACCESS_INSTANCED_PROP(Props, _ShowIndex);
                float3 texGlow   = DiamondSampleColour(fixtureRow, showIndex);

                // Proxy/edit path: the driven colour (colour x brightness) written into
                // _EmissionColor from the live proxies. Selected on the runtime uniform: 1 =>
                // texture, 0 => proxy. Branchless lerp, exact for a 0/1 selector.
                float3 proxyGlow = _EmissionColor.rgb;

                o.glow = lerp(proxyGlow, texGlow, _UdonDiamondLightshowEnabled) * glowScale;
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
