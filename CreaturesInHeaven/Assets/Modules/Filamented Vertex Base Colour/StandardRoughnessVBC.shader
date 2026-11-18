// Unity built-in shader source. Copyright (c) 2016 Unity Technologies. MIT license (see license.txt)

Shader "Silent/Filamented (Roughness + Vertex Base Colour)"
{
    Properties
    {
        _Color("Color", Color) = (1,1,1,1)
        _MainTex("Albedo", 2D) = "white" {}

        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        // Note to self: do not change these names or the whole thing explodes
        _Glossiness("Roughness", Range(0.0, 1.0)) = 0.5
        _SpecGlossMap("Roughness Map", 2D) = "white" {}

        [Gamma] _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
        _MetallicGlossMap("Metallic", 2D) = "white" {}

        [ToggleOff] _SpecularHighlights("Specular Highlights", Float) = 1.0
        [ToggleOff] _GlossyReflections("Glossy Reflections", Float) = 1.0

        _BumpScale("Scale", Float) = 1.0
        [Normal] _BumpMap("Normal Map", 2D) = "bump" {}

        _Parallax ("Height Scale", Range (0.005, 0.25)) = 0.02
        _ParallaxMap ("Height Map", 2D) = "black" {}

        _OcclusionStrength("Strength", Range(0.0, 1.0)) = 1.0
        _OcclusionMap("Occlusion", 2D) = "white" {}

        _EmissionColor("Color", Color) = (0,0,0)
        _EmissionMap("Emission", 2D) = "white" {}

        _DetailMask("Detail Mask", 2D) = "white" {}

        _DetailAlbedoMap("Detail Albedo x2", 2D) = "grey" {}
        _DetailNormalMapScale("Scale", Float) = 1.0
        [Normal] _DetailNormalMap("Normal Map", 2D) = "bump" {}

        [Enum(UV0,0,UV1,1)] _UVSec ("UV Set for secondary textures", Float) = 0

        // New settings
        _ExposureOcclusion("Lightmap Occlusion Sensitivity", Range(0, 1)) = 0.2
        [Toggle(_LIGHTMAPSPECULAR)]_LightmapSpecular("Lightmap Specular", Range(0, 1)) = 1
        _LightmapSpecularMaxSmoothness("Lightmap Specular Max Smoothness", Range(0, 1)) = 0.9

        [Toggle(_NORMALMAP_SHADOW)]_NormalMapShadows("Normal Map Shadows", Range(0, 1)) = 0
        _BumpShadowHeightScale("Height Scale", Range(0, 1)) = 0.2
        _BumpShadowHardness("Shadow Hardness", Range(0, 100)) = 50

        _specularAntiAliasingVariance("Specular AA Variance",  Range(0, 1)) = 0.15
        _specularAntiAliasingThreshold("Specular AA Threshold", Range(0, 1)) = 0.25

        [KeywordEnum(None, SH, RNM, MonoSH)] _Bakery ("Bakery Mode", Int) = 0
        [Toggle(_BAKERY_VERTEXLM)]_BakeryVertexLM ("Use Bakery Vertex Lightmaps", Range(0, 1)) = 0
            _RNM0("RNM0", 2D) = "black" {}
            _RNM1("RNM1", 2D) = "black" {}
            _RNM2("RNM2", 2D) = "black" {}

        [Toggle(_LTCGI)] _LTCGI ("LTCGI", Int) = 0
        [Toggle(_VRCLV)] _VRCLV ("VRC Light Volumes", Int) = 0
        [IfDef(_VRCLV)] _VRCLVSurfaceBias("Light Volume Surface Bias", Range(0, 0.5)) = 0.05

        [Enum(UnityEngine.Rendering.CullMode)]_CullMode("Cull Mode", Int) = 2
        [ToggleUI]_AlphaToMaskMode("Alpha to Coverage", Int) = 0

        [NonModifiableTextureData][HideInInspector] _DFG("DFG", 2D) = "white" {}

        // Blending state
        [HideInInspector] _Mode ("__mode", Float) = 0.0
        [HideInInspector] _SrcBlend ("__src", Float) = 1.0
        [HideInInspector] _DstBlend ("__dst", Float) = 0.0
        [HideInInspector] _ZWrite ("__zw", Float) = 1.0
    }

    CGINCLUDE
        #define SHADING_MODEL_METALLIC_ROUGHNESS
        // OcclusionMap is always defined
        #define MATERIAL_HAS_AMBIENT_OCCLUSION
        // Use specular AA controls
        #define USE_GEOMETRIC_SPECULAR_AA
        // _DFG is present
        #define USE_DFG_LUT
        // Add vertex base colour attribute
        #define HAS_ATTRIBUTE_COLOR
    ENDCG

    SubShader
    {
        Tags { "RenderType"="Opaque" "PerformanceChecks"="False" "LTCGI" = "_LTCGI" }
        LOD 300

        Cull [_CullMode]

        // ------------------------------------------------------------------
        //  Base forward pass (directional light, emission, lightmaps, ...)
        Pass
        {
            Name "FORWARD"
            Tags { "LightMode" = "ForwardBase" }

            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            AlphaToMask [_AlphaToMaskMode]

            CGPROGRAM
            #pragma target 4.0

            // -------------------------------------


            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _ _ALPHATEST_ON _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
            #pragma shader_feature _EMISSION
            #pragma shader_feature_local _METALLICGLOSSMAP
            #pragma shader_feature_local _SPECGLOSSMAP
            #pragma shader_feature_local _DETAIL_MULX2
            #pragma shader_feature_local _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local _GLOSSYREFLECTIONS_OFF
            #pragma shader_feature_local _PARALLAXMAP

            #pragma shader_feature_local _LIGHTMAPSPECULAR
            #pragma shader_feature_local _NORMALMAP_SHADOW
            #pragma shader_feature_local _ _BAKERY_RNM _BAKERY_SH _BAKERY_MONOSH
            #pragma shader_feature_local _BAKERY_VERTEXLM
            #pragma shader_feature_local _LTCGI
            #pragma shader_feature_local _VRCLV

            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            // Uncomment the following line to enable dithering LOD crossfade. Note: there are more in the file to uncomment for other passes.
            //#pragma multi_compile _ LOD_FADE_CROSSFADE

            #pragma vertex vertBase
            #pragma fragment fragBase
            #include "FilamentedVBCCoreForward.cginc"

            ENDCG
        }
        // ------------------------------------------------------------------
        //  Additive forward pass (one light per pass)
        Pass
        {
            Name "FORWARD_DELTA"
            Tags { "LightMode" = "ForwardAdd" }
            Blend [_SrcBlend] One
            Fog { Color (0,0,0,0) } // in additive pass fog should be black
            ZWrite Off
            ZTest LEqual
            AlphaToMask [_AlphaToMaskMode]

            CGPROGRAM
            #pragma target 4.0

            // -------------------------------------


            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _ _ALPHATEST_ON _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
            #pragma shader_feature_local _METALLICGLOSSMAP
            #pragma shader_feature_local _SPECGLOSSMAP
            #pragma shader_feature_local _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local _DETAIL_MULX2
            #pragma shader_feature_local _PARALLAXMAP

            #pragma shader_feature_local _NORMALMAP_SHADOW

            #pragma multi_compile_fwdadd_fullshadows
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            // Uncomment the following line to enable dithering LOD crossfade. Note: there are more in the file to uncomment for other passes.
            //#pragma multi_compile _ LOD_FADE_CROSSFADE

            #pragma vertex vertAdd
            #pragma fragment fragAdd
            #include "FilamentedVBCCoreForward.cginc"

            ENDCG
        }
        // ------------------------------------------------------------------
        //  Shadow rendering pass
        Pass {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On ZTest LEqual

            CGPROGRAM
            #pragma target 3.0

            // -------------------------------------


            #pragma shader_feature_local _ _ALPHATEST_ON _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
            #pragma shader_feature_local _METALLICGLOSSMAP
            #pragma shader_feature_local _SPECGLOSSMAP
            #pragma shader_feature_local _PARALLAXMAP
            #pragma multi_compile_shadowcaster
            #pragma multi_compile_instancing
            // Uncomment the following line to enable dithering LOD crossfade. Note: there are more in the file to uncomment for other passes.
            //#pragma multi_compile _ LOD_FADE_CROSSFADE

            #pragma vertex vertShadowCaster
            #pragma fragment fragShadowCaster

            #include "Packages/s-ilent.filamented/Filamented/UnityStandardShadow.cginc"

            ENDCG
        }

        // Deferred not implemented
        UsePass "Standard/DEFERRED"

        // UsePass "Standard/META"

        // Meta pass, mostly for Bakery
        // Copied from FilamentedTemplate.shader
        Pass
        {
            Name "META"
            Tags {"LightMode"="Meta"}
            Cull Off
            CGPROGRAM

            #include "Packages/s-ilent.filamented/Filamented/UnityLightingCommon.cginc"
            #include "Packages/s-ilent.filamented/Filamented/UnityStandardMeta.cginc"

            // Copied from UnityStandardMeta.cginc
            float4 frag_vbc_meta (v2f_meta i) : SV_Target
            {
                MaterialInputs material = SETUP_BRDF_INPUT (i.uv);

                #if defined (HAS_ATTRIBUTE_COLOR)
                    material.baseColor *= i.color;
                #endif
                #if _EMISSION
                    material.emissive.rgb = Emission (i.uv.xy);
                    material.emissive.a = 1.0;
                #endif

                PixelParams pixel = (PixelParams)0;
                getCommonPixelParams(material, pixel);

                UnityMetaInput o;
                UNITY_INITIALIZE_OUTPUT(UnityMetaInput, o);

                #ifdef EDITOR_VISUALIZATION
                    o.Albedo = pixel.diffuseColor;
                    o.VizUV = i.vizUV;
                    o.LightCoord = i.lightCoord;
                #else
                    o.Albedo = UnityLightmappingAlbedo (pixel.diffuseColor, pixel.f0, 1-pixel.perceptualRoughness);
                #endif

                o.SpecularColor = pixel.f0;
                o.Emission = material.emissive;

                return UnityMetaFragment(o);
            }

            #pragma vertex vert_meta
            // #pragma fragment frag_meta2
            #pragma fragment frag_vbc_meta

            #pragma shader_feature _EMISSION
            #pragma shader_feature_local _METALLICGLOSSMAP
            #pragma shader_feature_local _SPECGLOSSMAP
            #pragma shader_feature_local _DETAIL_MULX2

            #pragma shader_feature EDITOR_VISUALIZATION
            ENDCG
        }

    }

    FallBack "VertexLit"
    CustomEditor "SilentTools.FilamentedStandardShaderGUI"
}
