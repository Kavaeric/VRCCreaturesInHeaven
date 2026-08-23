// MSDFAtlasCutoutEmissive.shader
//
// Emissive graphic rendered from a packed multi-channel (MSDF) atlas, as an opaque
// alpha-clipped surface.

Shader "SDFAtlas/MSDF Cutout Emissive"
{
    Properties
    {
        [NoScaleOffset] _Atlas ("MSDF atlas", 2D) = "black" {}

        _Color ("Colour", Color) = (1, 1, 1, 1)
        _Intensity ("Intensity", Float) = 1.0

        [Toggle(_VERTEX_COLOR_ON)] _VertexColor ("Vertex base colour", Float) = 0

        // Where the edge sits within the coverage ramp. 0.5 puts it in the same place as the
        // additive shader's; lower fattens the shape, higher thins it. With alpha-to-coverage
        // this rescales the ramp rather than hard-discarding, so the edge stays antialiased
        // wherever it is placed.
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.5

        // --- Lightmapping ------------------------------------------------
        // How much light this graphic contributes to a bake, via the META pass. Separate from
        // _Intensity so a sign can be visually bright without flooding the scene around it,
        // which is usually what's wanted: the on-screen brightness and the amount of light a
        // sign realistically throws are not the same number.
        //
        // Only has any effect when the renderer is marked Contribute GI.
        _EmissionBakeStrength ("Bake emission strength", Float) = 1

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
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" }

        Cull Back

        // Antialiases the cut edge without a transparent queue.
        AlphaToMask On

        Pass
        {
            Name "FORWARD"
            Tags { "LightMode" = "ForwardBase" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma shader_feature_local _VERTEX_COLOR_ON

            #include "SDFAtlasCommon.cginc"

            float _Cutoff;

            struct FragmentInput
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_FOG_COORDS(1)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            FragmentInput vert(SDFAtlasVertexInput v)
            {
                FragmentInput o;
                UNITY_INITIALIZE_OUTPUT(FragmentInput, o);
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;

                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag(FragmentInput i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                // Three channels sampled, median taken after filtering.
                float coverage = SDFAtlasCoverageMultiAt(i.uv);

                // _Cutoff repositions the edge within the ramp. Rescaling rather than
                // comparing keeps the result a gradient, so a non-default cutoff still
                // antialiases; a plain step would hand alpha-to-coverage a binary value and
                // give back the hard edge it exists to avoid.
                float cutoff = clamp(_Cutoff, 1e-4, 1.0 - 1e-4);
                coverage = coverage < cutoff
                    ? (coverage / cutoff) * 0.5
                    : 0.5 + ((coverage - cutoff) / (1.0 - cutoff)) * 0.5;
                coverage = saturate(coverage);

                // Discard fully-empty pixels so they neither write depth nor occlude. The
                // threshold is far below the edge, so the antialiased fringe survives to reach
                // alpha-to-coverage; this is an early-out for the large empty area of a
                // typical cell, not the shape test itself.
                clip(coverage - 1e-3);

                float3 rgb = _Color.rgb * _Intensity * _Color.a;

                #if defined(_VERTEX_COLOR_ON)
                rgb *= i.color.rgb;
                #endif

                float4 output = float4(rgb, coverage);

                UNITY_APPLY_FOG(i.fogCoord, output);

                return output;
            }
            ENDCG
        }

        Pass
        {
            Name "SHADOWCASTER"
            Tags { "LightMode" = "ShadowCaster" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #pragma multi_compile_instancing
            #pragma multi_compile_shadowcaster

            #include "SDFAtlasCommon.cginc"

            float _Cutoff;

            struct ShadowInput
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowFragmentInput
            {
                V2F_SHADOW_CASTER;
                float2 uv : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            ShadowFragmentInput vert(ShadowInput v)
            {
                ShadowFragmentInput o;
                UNITY_INITIALIZE_OUTPUT(ShadowFragmentInput, o);
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                TRANSFER_SHADOW_CASTER_NORMALOFFSET(o);
                o.uv = v.uv;
                return o;
            }

            float4 frag(ShadowFragmentInput i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);

                clip(SDFAtlasCoverageMultiAt(i.uv) - _Cutoff);

                SHADOW_CASTER_FRAGMENT(i);
            }
            ENDCG
        }

        Pass
        {
            Name "META"
            Tags { "LightMode" = "Meta" }
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #pragma shader_feature_local _VERTEX_COLOR_ON

            #include "SDFAtlasCommon.cginc"
            #include "UnityMetaPass.cginc"

            float _EmissionBakeStrength;

            struct MetaInput
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
                float2 uv2 : TEXCOORD2;
            };

            struct MetaFragmentInput
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            MetaFragmentInput vert(MetaInput v)
            {
                MetaFragmentInput o;
                o.pos = UnityMetaVertexPosition(v.vertex, v.uv1, v.uv2, unity_LightmapST, unity_DynamicLightmapST);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            float4 frag(MetaFragmentInput i) : SV_Target
            {
                // Coverage is used as a weight instead.
                float coverage = SDFAtlasCoverageMultiAt(i.uv);

                float3 emission = _Color.rgb * _Intensity * _Color.a;

                #if defined(_VERTEX_COLOR_ON)
                emission *= i.color.rgb;
                #endif

                UnityMetaInput o;
                UNITY_INITIALIZE_OUTPUT(UnityMetaInput, o);

                o.Albedo = 0;
                o.Emission = emission * coverage * _EmissionBakeStrength;

                return UnityMetaFragment(o);
            }
            ENDCG
        }
    }

    Fallback Off
    CustomEditor "SDFAtlasSignGUI"
}
