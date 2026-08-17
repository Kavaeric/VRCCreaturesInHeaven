// MSDFAtlasAdditive.shader
//
// Emissive graphic rendered from a packed multi-channel (MSDF) atlas. The single-channel
// counterpart is SDFAtlasAdditive.shader.
//
// Addressing is identical to the single-channel shader: the integer part of the mesh UV is
// the atlas cell (UDIM tile), the fractional part is the position within it. One material
// serves every sign, so signage stays static-batchable.

Shader "SDFAtlas/MSDF Additive"
{
    Properties
    {
        [NoScaleOffset] _Atlas ("MSDF atlas", 2D) = "black" {}

        _Color ("Colour", Color) = (1, 1, 1, 1)
        _Intensity ("Intensity", Float) = 1.0

        [Toggle(_VERTEX_COLOR_ON)] _VertexColor ("Vertex base colour", Float) = 0

        // --- Atlas layout ------------------------------------------------
        // These must match the manifest (.sdfatlas.json) written beside the atlas texture.
        // If there's a mismatch, offer to apply them in the inspector.

        _GridSize ("Grid size (cells across, down)", Vector) = (16, 16, 0, 0)
        _CellSize ("Cell size (texels)", Float) = 64
        _Padding ("Padding (texels)", Float) = 2
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
