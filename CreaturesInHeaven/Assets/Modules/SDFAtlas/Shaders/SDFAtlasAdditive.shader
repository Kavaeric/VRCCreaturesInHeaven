// SDFAtlasAdditive.shader
//
// Emissive graphic rendered from a packed SDF atlas
//
// Artwork is selected by UV placement: a quad's UV island sits over the graphic it should
// show, in 0..1 atlas space. No per-object material or property block is involved, so every
// sign shares one material and stays static-batchable.
//
// The sampling and edge reconstruction live in SDFAtlasCommon.cginc; this file adds only the
// additive output and blend state. Other blend behaviours belong in their own shaders
// alongside this one rather than as switchable modes here.

Shader "SDFAtlas/SDF Additive"
{
    Properties
    {
        [NoScaleOffset] _Atlas ("SDF atlas", 2D) = "black" {}

        _Color ("Colour", Color) = (1, 1, 1, 1)
        _Intensity ("Intensity", Float) = 1.0

        [Toggle(_VERTEX_COLOR_ON)] _VertexColor ("Vertex base colour", Float) = 0

        // --- Atlas layout ------------------------------------------------
        // Must match the manifest (.sdfatlas.json) written beside the atlas texture, so read
        // it off the manifest and offer to apply it automatically for the user.

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

        // Signs sit on quads offset slightly in front of their wall, so the offset handles
        // ordering and normal depth testing still applies.
        ZTest LEqual

        // Single-sided: a sign's back face is never meant to be seen, and drawing it would
        // double the additive contribution wherever coplanar quads overlap.
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

            SDFAtlasFragmentInput vert(SDFAtlasVertexInput v)
            {
                return SDFAtlasVert(v);
            }

            fixed4 frag(SDFAtlasFragmentInput i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float coverage = SDFAtlasCoverageAt(i.uv);

                // Additive: alpha is folded into the colour and the destination alpha is
                // irrelevant, since the One One blend only ever adds RGB to the target.
                float3 rgb = _Color.rgb * _Intensity * _Color.a * coverage;

                #if defined(_VERTEX_COLOR_ON)
                rgb *= i.color.rgb;
                #endif

                return fixed4(rgb, coverage);
            }
            ENDCG
        }
    }

    Fallback Off
    CustomEditor "SDFAtlasSignGUI"
}
