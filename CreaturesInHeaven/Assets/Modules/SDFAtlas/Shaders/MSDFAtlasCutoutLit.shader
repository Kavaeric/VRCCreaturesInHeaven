// MSDFAtlasCutoutLit.shader
//
// Surface-lit (PBR) graphic rendered from a packed multi-channel (MSDF) atlas.
//
// Unlike the additive shader this is an opaque, alpha-clipped surface. It writes depth,
// casts shadows and takes part in lightmapping, so a graphic sits in the scene as painted
// signage rather than as a glowing overlay. There is deliberately no single-channel (SDF)
// counterpart to this shader.
//
// The only texture input is the MSDF atlas, used for shape alone. Colour comes from _Color
// and optionally vertex colour; roughness and metallic are flat material constants.

Shader "SDFAtlas/MSDF Cutout Lit"
{
    Properties
    {
        [NoScaleOffset] _Atlas ("MSDF atlas", 2D) = "black" {}

        _Color ("Colour", Color) = (1, 1, 1, 1)

        [Toggle(_VERTEX_COLOR_ON)] _VertexColor ("Vertex base colour", Float) = 0

        // --- Surface -----------------------------------------------------
        // Flat constants rather than maps: signage graphics are a single material finish
        // across the whole shape, and the atlas has no room for per-pixel surface data.

        _Roughness ("Roughness", Range(0, 1)) = 0.5
        _Metallic ("Metallic", Range(0, 1)) = 0
        _OcclusionStrength ("Occlusion", Range(0, 1)) = 1

        // --- Specularity -------------------------------------------------
        // Shading follows Google Filament's model, matching Mochie Standard's "Google
        // Filament" specular mode. See MSDFAtlasFilament.cginc for why it is ported rather
        // than included.

        _ReflectionStrength ("Environment reflections", Float) = 1
        _SpecularHighlightStrength ("Specular highlights", Float) = 1
        _IndirectSpecularOcclusionStrength ("Indirect specular occlusion", Range(0, 1)) = 0.2
        _RealtimeSpecularOcclusionStrength ("Realtime specular occlusion", Range(0, 1)) = 0

        // --- Light Volumes -----------------------------------------------
        // VRC Light Volumes stand in for light probes where the scene has them. Defaults match
        // Mochie Standard's so a sign and its surroundings agree without being retuned.

        [ToggleUI] _AdditiveLightVolumesToggle ("Additive light volumes", Int) = 1
        _LightVolumeBias ("Light volume bias", Float) = 0
        [ToggleUI] _LightVolumeSpecularity ("Light volume highlights", Int) = 0
        _LightVolumeSpecularityStrength ("Light volume highlight strength", Float) = 1

        // --- Bakery ------------------------------------------------------
        // Bakery's MonoSH lightmap mode. Unlike Bakery's SH and RNM modes it needs no extra
        // lightmap textures, so it is the only one supported here.
        //
        // These are plain floats rather than [Toggle] because they drive shader keywords the
        // inspector sets by hand: Unity's [Toggle] would name the keyword after the property,
        // and these keyword names are Bakery's, not ours.

        [ToggleUI] _BakeryMonoSH ("Bakery MonoSH", Float) = 0
        [ToggleUI] _BAKERY_LMSPEC ("Lightmap specular", Float) = 0
        _BakeryLMSpecStrength ("Lightmap specular strength", Float) = 1
        [ToggleUI] _BAKERY_SHNONLINEAR ("Non-linear SH", Float) = 0

        // Precomputed split-sum DFG lookup the Filament model indexes by (NdotV, roughness).
        // Hidden because it is a fixed data table, not an artistic choice: it must be set to
        // Mochie's dfg-multiscatter.exr, which the material default below points at.
        [HideInInspector] [NoScaleOffset] _DFG ("DFG LUT", 2D) = "white" {}

        // Where the edge sits within the coverage ramp. 0.5 puts it in the same place as the
        // additive shader's; lower fattens the shape, higher thins it. With alpha-to-coverage
        // this rescales the ramp rather than hard-discarding, so the edge stays antialiased
        // wherever it is placed.
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.5

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
        // wide (sharpest without aliasing); higher softens it.
        _EdgeSoftness ("Edge softness", Range(0.5, 4)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" }

        Cull Back

        AlphaToMask On

        Pass
        {
            Name "FORWARD"
            Tags { "LightMode" = "ForwardBase" }

            CGPROGRAM
            #pragma vertex MSDFLitVert
            #pragma fragment MSDFLitFrag
            #pragma target 5.0
            #pragma multi_compile_instancing
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #pragma shader_feature_local _VERTEX_COLOR_ON

            // Base pass only: a lightmap is only read here, so the additive pass has no use
            // for any of these and should not pay for the extra variants.
            #pragma shader_feature_local BAKERY_MONOSH
            #pragma shader_feature_local BAKERY_LMSPEC
            #pragma shader_feature_local BAKERY_SHNONLINEAR

            #define MSDF_BASE_PASS
            #include "MSDFAtlasCutoutLit.cginc"
            ENDCG
        }

        Pass
        {
            Name "FORWARD_DELTA"
            Tags { "LightMode" = "ForwardAdd" }
            Blend One One
            ZWrite Off

            CGPROGRAM
            #pragma vertex MSDFLitVert
            #pragma fragment MSDFLitFrag
            #pragma target 5.0
            #pragma multi_compile_instancing
            #pragma multi_compile_fwdadd_fullshadows
            #pragma multi_compile_fog
            #pragma shader_feature_local _VERTEX_COLOR_ON

            #include "MSDFAtlasCutoutLit.cginc"
            ENDCG
        }

        Pass
        {
            Name "SHADOWCASTER"
            Tags { "LightMode" = "ShadowCaster" }

            CGPROGRAM
            #pragma vertex MSDFLitShadowVert
            #pragma fragment MSDFLitShadowFrag
            #pragma target 5.0
            #pragma multi_compile_instancing
            #pragma multi_compile_shadowcaster

            #include "MSDFAtlasCutoutLit.cginc"
            ENDCG
        }

        Pass
        {
            Name "META"
            Tags { "LightMode" = "Meta" }
            Cull Off

            CGPROGRAM
            #pragma vertex MSDFLitMetaVert
            #pragma fragment MSDFLitMetaFrag
            #pragma target 5.0
            #pragma shader_feature_local _VERTEX_COLOR_ON

            #define MSDF_META_PASS
            #include "MSDFAtlasCutoutLit.cginc"
            ENDCG
        }
    }

    Fallback "Diffuse"
    CustomEditor "SDFAtlasSignGUI"
}
