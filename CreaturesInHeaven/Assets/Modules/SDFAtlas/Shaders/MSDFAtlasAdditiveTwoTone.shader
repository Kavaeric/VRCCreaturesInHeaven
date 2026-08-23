// MSDFAtlasTwoTone.shader
//
// Two-colour emissive graphic rendered from a packed multi-channel (MSDF) atlas. Where
// MSDFAtlasAdditive.shader leaves the area outside the shape transparent, this one fills it
// with a second (background) colour.

Shader "SDFAtlas/MSDF Additive Two-Tone"
{
    Properties
    {
        [NoScaleOffset] _Atlas ("MSDF atlas", 2D) = "black" {}

        // Foreground: the colour of the shape itself.
        _Color ("Foreground colour", Color) = (1, 1, 1, 1)

        // Background: the colour of everything outside the shape, across the UV island.
        // Black makes this shader behave as plain additive.
        _BackColor ("Background colour", Color) = (1, 0, 0, 1)

        // Scales both tones together, so the sign can be dimmed as a unit without disturbing
        // the ratio between foreground and background.
        _Intensity ("Intensity", Float) = 1.0

        [Toggle(_VERTEX_COLOR_ON)] _VertexColor ("Vertex base colour", Float) = 0

        // --- Atlas layout ------------------------------------------------
        // Must match the manifest (.sdfatlas.json) written beside the atlas texture.
        // If there's a mismatch, offer to apply it in the inspector.

        _Spread ("Spread (texels)", Float) = 4

        // --- Edge shaping ------------------------------------------------

        // Width added to the rendered shape, in cell texels. Positive dilates, negative
        // erodes. Useful for optically weight-matching graphics authored at different
        // stroke weights.
        _EdgeBias ("Edge bias (texels)", Range(-4, 4)) = 0

        // Multiplier on the automatic antialiasing width. 1 keeps the edge one screen pixel
        // wide (sharpest without aliasing); higher softens, which reads as a faint glow.
        _EdgeSoftness ("Edge softness", Range(0.5, 4)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "IgnoreProjector" = "True" }

        Blend One One
        ZWrite Off
        ZTest LEqual
        Cull Back

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #pragma multi_compile_instancing
            #pragma shader_feature_local _VERTEX_COLOR_ON

            #include "SDFAtlasCommon.cginc"

            // _Color, _Intensity and the layout properties come from the shared include;
            // only the second tone is new here.
            float4 _BackColor;

            SDFAtlasFragmentInput vert(SDFAtlasVertexInput v)
            {
                return SDFAtlasVert(v);
            }

            fixed4 frag(SDFAtlasFragmentInput i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                // Three channels sampled, median taken after filtering. See
                // SDFAtlasCoverageMulti for why that order is the one that preserves
                // corners.
                float coverage = SDFAtlasCoverageMultiAt(i.uv);

                // Alpha folded into each tone before the blend. On an additive pass there is
                // no destination alpha to fade against, so a colour's alpha is only useful as
                // a per-tone brightness scale, letting either tone be dimmed on its own.
                float3 foreground = _Color.rgb * _Color.a;
                float3 background = _BackColor.rgb * _BackColor.a;

                // Vertex colour tints the foreground only. The background stays the flat
                // authored colour, so one mesh can carry per-face graphic colours over a
                // constant backing panel.
                #if defined(_VERTEX_COLOR_ON)
                foreground *= i.color.rgb;
                #endif

                // Coverage selects between the tones rather than fading to nothing, which is
                // what makes this two-tone: the antialiased edge crossfades foreground into
                // background instead of into the scene.
                float3 rgb = lerp(background, foreground, coverage) * _Intensity;

                // Opaque alpha: the pass covers the whole UV island now, not just the shape.
                // The additive blend ignores this, but it keeps the value honest for anything
                // that reads the target's alpha.
                return fixed4(rgb, 1);
            }
            ENDCG
        }
    }

    Fallback Off
    CustomEditor "SDFAtlasSignGUI"
}
