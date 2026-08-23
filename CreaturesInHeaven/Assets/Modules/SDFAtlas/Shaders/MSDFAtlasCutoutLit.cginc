// MSDFAtlasLit.cginc
//
// Shading for MSDFAtlasLit.shader: a surface-lit (PBR) graphic rendered from a packed MSDF
// atlas. The shader model is designed to match Mochie's Standard Shader.

#ifndef MSDF_ATLAS_LIT_INCLUDED
#define MSDF_ATLAS_LIT_INCLUDED

#include "UnityCG.cginc"
#include "UnityPBSLighting.cginc"
#include "AutoLight.cginc"

#include "SDFAtlasCommon.cginc"
#include "MSDFAtlasFilament.cginc"
#include "MSDFAtlasBakery.cginc"

// VRC Light Volumes. Included from Mochie's copy rather than the upstream package so that a
// scene lit with Mochie's Standard shader and one of these signs agree on the same volume
// data.
#include "../../../Mochie/Common/LightVolumes.cginc"

// --- Properties ----------------------------------------------------------
// The atlas and edge-shaping properties (_Atlas, _Spread, _EdgeBias, _EdgeSoftness, _Color)
// are declared in SDFAtlasCommon.cginc. Only the surface parameters are new here.

float _Metallic;
float _Roughness;
float _Cutoff;
float _OcclusionStrength;

// Specular controls, matching the ones Mochie's Standard exposes for the same model.
float _ReflectionStrength;
float _SpecularHighlightStrength;
float _IndirectSpecularOcclusionStrength;
float _RealtimeSpecularOcclusionStrength;

// Light Volume controls. LightVolumes.cginc already declares both at global scope for Mochie.
int _AdditiveLightVolumesToggle;

// Shifts the sampling position along the normal before reading the volume.
float _LightVolumeBias;

// Strength of the specular highlight decoded from a Bakery MonoSH lightmap. Only has any
// effect when the BAKERY_MONOSH and BAKERY_LMSPEC keywords are both set.
float _BakeryLMSpecStrength;

// --- Vertex plumbing -----------------------------------------------------
//
// A separate struct pair from SDFAtlasCommon's: lighting needs the world position, normal,
// tangent frame, shadow coords and lightmap UV that the unlit path has no use for.

struct MSDFLitVertexInput
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float4 tangent : TANGENT;
    float4 color : COLOR;
    float2 uv : TEXCOORD0;
    float2 uv1 : TEXCOORD1;
    float2 uv2 : TEXCOORD2;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct MSDFLitFragmentInput
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
    float4 color : COLOR;
    float3 worldPos : TEXCOORD1;
    float3 worldNormal : TEXCOORD2;

    // Lightmap UV in xy, dynamic (realtime GI) lightmap UV in zw. Packed together because
    // interpolators are the scarce resource here, and neither half needs more than two
    // components.
    float4 lightmapUV : TEXCOORD3;

    UNITY_LIGHTING_COORDS(4, 5)
    UNITY_FOG_COORDS(6)

    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

MSDFLitFragmentInput MSDFLitVert(MSDFLitVertexInput v)
{
    MSDFLitFragmentInput o;
    UNITY_INITIALIZE_OUTPUT(MSDFLitFragmentInput, o);
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_TRANSFER_INSTANCE_ID(v, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

    o.pos = UnityObjectToClipPos(v.vertex);
    o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
    o.worldNormal = UnityObjectToWorldNormal(v.normal);

    // UVs pass through unmodified: they are atlas texture coordinates already.
    o.uv = v.uv;
    o.color = v.color;

    #ifdef LIGHTMAP_ON
        o.lightmapUV.xy = v.uv1 * unity_LightmapST.xy + unity_LightmapST.zw;
    #endif
    #ifdef DYNAMICLIGHTMAP_ON
        o.lightmapUV.zw = v.uv2 * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
    #endif

    UNITY_TRANSFER_LIGHTING(o, v.uv1);
    UNITY_TRANSFER_FOG(o, o.pos);
    return o;
}

// --- Surface -------------------------------------------------------------

// The albedo of the graphic, before lighting.
//
// The atlas contributes shape only. _Color tints, and vertex colour multiplies on top when
// enabled, which lets one material drive many differently-coloured signs while staying
// static-batchable.
float3 MSDFLitAlbedo(MSDFLitFragmentInput i)
{
    float3 albedo = _Color.rgb;

    #if defined(_VERTEX_COLOR_ON)
    albedo *= i.color.rgb;
    #endif

    return albedo;
}

// Coverage as an alpha value, ready to be handed to alpha-to-coverage.
//
// _Cutoff repositions the edge within that ramp. Rescaling rather than comparing keeps the
// result a gradient, so a non-default cutoff still antialiases; a plain step would hand A2C
// a binary value and give back the hard edge A2C exists to avoid.
float MSDFLitCoverage(MSDFLitFragmentInput i)
{
    float coverage = SDFAtlasCoverageMultiAt(i.uv);

    // Guard the degenerate ends, where the rescale would divide by zero.
    float cutoff = clamp(_Cutoff, 1e-4, 1.0 - 1e-4);

    // Remap so `cutoff` becomes the 0.5 crossing, keeping the ramp's slope on each side.
    coverage = coverage < cutoff
        ? (coverage / cutoff) * 0.5
        : 0.5 + ((coverage - cutoff) / (1.0 - cutoff)) * 0.5;

    return saturate(coverage);
}

// --- Lighting ------------------------------------------------------------

// Indirect diffuse light: lightmaps where the object is baked, spherical-harmonic probes
// where it is not.
float3 MSDFLitIndirectDiffuse(MSDFLitFragmentInput i, float3 normal, float3 viewDir, float roughness,
    out float3 volumeL0, out float3 volumeL1r, out float3 volumeL1g, out float3 volumeL1b,
    out float3 lightmapSpecular)
{
    float3 indirect = 0;

    volumeL0 = 0;
    volumeL1r = 0;
    volumeL1g = 0;
    volumeL1b = 0;
    lightmapSpecular = 0;

    #if defined(MSDF_BASE_PASS)
        #if defined(LIGHTMAP_ON)
            indirect = DecodeLightmap(UNITY_SAMPLE_TEX2D(unity_Lightmap, i.lightmapUV.xy));

            // A directional lightmap stores a dominant light direction alongside the colour,
            // which recovers some of the normal-dependent shading a plain lightmap flattens.
            //
            // Bakery MonoSH lives in the same texture but encodes it differently, so it needs
            // the sample whether or not Unity set DIRLIGHTMAP_COMBINED, and decodes it itself
            // instead of going through DecodeDirectionalLightmap.
            #if defined(DIRLIGHTMAP_COMBINED) || defined(BAKERY_MONOSH)
                float4 lightmapDirection = UNITY_SAMPLE_TEX2D_SAMPLER(unity_LightmapInd, unity_Lightmap, i.lightmapUV.xy);

                #if defined(BAKERY_MONOSH)
                    // Squared, because Bakery's specular expects a linear roughness where the
                    // GGX term wants the perceptual value squared.
                    MSDFBakeryMonoSH(indirect, lightmapSpecular, lightmapDirection.rgb, normal,
                        viewDir, roughness * roughness);
                #else
                    indirect = DecodeDirectionalLightmap(indirect, lightmapDirection, normal);
                #endif
            #endif
        #else
            // Not lightmapped, so the volumes are the whole of the indirect diffuse here.
            // LightVolumeSH falls back to the light probes on its own when the scene has no
            // volumes, which is why there is no probe path beside this one.
            [branch]
            if (_UdonLightVolumeEnabled == 1)
            {
                LightVolumeSH(i.worldPos + normal * _LightVolumeBias, volumeL0, volumeL1r, volumeL1g, volumeL1b);
                indirect = max(0, LightVolumeEvaluate(normal, volumeL0, volumeL1r, volumeL1g, volumeL1b));
            }
            else
            {
                indirect = max(0, ShadeSH9(float4(normal, 1)));
            }
        #endif

        #if defined(DYNAMICLIGHTMAP_ON)
            float3 realtimeColor = DecodeRealtimeLightmap(UNITY_SAMPLE_TEX2D(unity_DynamicLightmap, i.lightmapUV.zw));
            #if defined(DIRLIGHTMAP_COMBINED)
                float4 realtimeDirection = UNITY_SAMPLE_TEX2D_SAMPLER(unity_DynamicDirectionality, unity_DynamicLightmap, i.lightmapUV.zw);
                indirect += DecodeDirectionalLightmap(realtimeColor, realtimeDirection, normal);
            #else
                indirect += realtimeColor;
            #endif
        #endif

        // Additive Light Volumes on top of baked light. These are the volumes marked additive,
        // which carry only the light the bake did not capture (a volume animated at runtime,
        // typically), so they layer onto a lightmap instead of replacing it.
        #if defined(LIGHTMAP_ON)
            [branch]
            if (_UdonLightVolumeEnabled == 1 && _AdditiveLightVolumesToggle == 1)
            {
                LightVolumeAdditiveSH(i.worldPos, volumeL0, volumeL1r, volumeL1g, volumeL1b);
                indirect += max(0, LightVolumeEvaluate(normal, volumeL0, volumeL1r, volumeL1g, volumeL1b));
            }
        #endif
    #endif

    return indirect;
}

// --- Forward passes ------------------------------------------------------

float4 MSDFLitFrag(MSDFLitFragmentInput i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

    float coverage = MSDFLitCoverage(i);

    // Discard only fully-empty pixels. The threshold is far below the edge so the
    // antialiased fringe survives to reach alpha-to-coverage; this is an early-out for the
    // large empty area of a typical cell, not the shape test itself.
    clip(coverage - 1e-3);

    float3 baseColor = MSDFLitAlbedo(i);

    // Backfaces are culled, so the interpolated normal is always the front-facing one and
    // needs no flip.
    float3 normal = normalize(i.worldNormal);
    float3 viewDir = normalize(UnityWorldSpaceViewDir(i.worldPos));

    float roughness = saturate(_Roughness);
    float metallic = saturate(_Metallic);
    float occlusion = saturate(_OcclusionStrength);

    // A material set to 0 roughness should still be mirror-like.
    float roughSq = max(roughness * roughness, 0.003);

    UNITY_LIGHT_ATTENUATION(atten, i, i.worldPos);

    float3 lightDir = normalize(UnityWorldSpaceLightDir(i.worldPos));
    float3 halfVector = Unity_SafeNormalize(lightDir + viewDir);

    float NdotL = saturate(dot(normal, lightDir));
    float NdotV = abs(dot(normal, viewDir));
    float NdotH = saturate(dot(normal, halfVector));
    float LdotH = saturate(dot(lightDir, halfVector));

    float3 reflDir = reflect(-viewDir, normal);

    // Fades the reflection out where it would graze past the surface, which is where the
    // split-sum approximation is least accurate and shows as a bright rim.
    float horizon = min(1 + dot(reflDir, normal), 1);

    // Metallic workflow. Dielectrics reflect a fixed 4% (Filament's reflectance 0.5 through
    // 0.16 * r^2); metals reflect their own colour and have no diffuse term at all.
    float oneMinusMetallic = 1.0 - metallic;
    float3 f0 = 0.16 * 0.5 * 0.5 * oneMinusMetallic + baseColor * metallic;
    float3 albedo = baseColor * oneMinusMetallic;

    // Multiscatter energy conservation. Also serves as the diffuse term,
    float2 dfg;
    float3 energyConservation = MSDFFilamentEnergyConservation(NdotV, roughness, f0, dfg);

    float3 directLight = _LightColor0.rgb * atten * NdotL * energyConservation;

    float3 volumeL0, volumeL1r, volumeL1g, volumeL1b;
    float3 lightmapSpecular;
    float3 indirectLight = MSDFLitIndirectDiffuse(i, normal, viewDir, roughness,
        volumeL0, volumeL1r, volumeL1g, volumeL1b, lightmapSpecular);

    // Specular occlusion, from how much indirect light actually reaches this fragment.
    // A surface sitting in a dark corner should not carry a full-strength reflection.
    float indirectSpecularOcclusion = saturate(length(indirectLight) * (1.0 / max(_IndirectSpecularOcclusionStrength, 1e-4)));
    indirectSpecularOcclusion *= lerp(1, atten * NdotL, _RealtimeSpecularOcclusionStrength);

    float3 specularOcclusion = MSDFFilamentMultiBounceAO(
        MSDFFilamentSpecularAO(NdotV, occlusion * indirectSpecularOcclusion, roughSq), f0);

    // The environment BRDF term, from the DFG lookup rather than an analytic fresnel.
    float3 reflAdjust = (f0 * dfg.x + dfg.y) * horizon * horizon;

    // Diffuse lighting.
    float3 col = albedo * (indirectLight * occlusion + directLight);

    // Environment reflection. Gathered in the base pass only: sampling the probe again per
    // additional light would stack the same reflection several times over.
    #if defined(MSDF_BASE_PASS)
        float3 environment = MSDFSampleReflectionProbes(reflDir, i.worldPos, roughness);
        col += environment * reflAdjust * specularOcclusion * occlusion * _ReflectionStrength;
    #endif

    // Baked specular from a Bakery MonoSH lightmap, scaled here rather than where it was
    // decoded because the environment BRDF and specular occlusion terms only exist by now.
    // Mochie multiplies by a specular tint at this point as well; there is no equivalent here,
    // as this shader has no per-material specular tint to apply.
    #if defined(BAKERY_MONOSH) && defined(BAKERY_LMSPEC)
        col += lightmapSpecular * reflAdjust * UNITY_PI * specularOcclusion * _BakeryLMSpecStrength;
    #endif

    // Specular from the Light Volumes' directional (L1) component. A volume carries a rough
    // sense of where its light comes from, so a glossy surface can take a highlight from it
    // even with no realtime light present. Gathered in the base pass only, for the same reason
    // the environment reflection is: the volume does not belong to any one realtime light.
    #if defined(MSDF_BASE_PASS)
        [branch]
        if (_UdonLightVolumeEnabled == 1 && _LightVolumeSpecularity == 1 && _LightVolumeSpecularityStrength > 0)
        {
            col += LightVolumeSpecularDominant(f0, 1 - roughness, normal, viewDir,
                volumeL0, volumeL1r, volumeL1g, volumeL1b)
                * _LightVolumeSpecularityStrength * specularOcclusion;
        }
    #endif

    // Direct specular highlight, standard GGX with a Smith-Joint visibility term.
    float3 fresnelTerm = FresnelTerm(f0, LdotH) * energyConservation;
    float V = SmithJointGGXVisibilityTerm(NdotL, NdotV, roughSq);
    float D = GGXTerm(NdotH, roughSq);
    col += _LightColor0.rgb * atten * NdotL * fresnelTerm * (V * D * UNITY_PI) * _SpecularHighlightStrength;

    // Alpha carries the coverage in both passes, because alpha-to-coverage is a SubShader
    // state and so applies to every pass: returning 1 in the additive pass would make each
    // extra light paint the full quad rather than the shape.
    float4 output = float4(col, coverage);

    #if defined(MSDF_BASE_PASS)
        UNITY_APPLY_FOG(i.fogCoord, output);
    #else
        UNITY_APPLY_FOG_COLOR(i.fogCoord, output, float4(0, 0, 0, 0));
    #endif

    return output;
}

// --- Shadow caster -------------------------------------------------------

struct MSDFLitShadowInput
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct MSDFLitShadowFragmentInput
{
    V2F_SHADOW_CASTER;
    float2 uv : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

MSDFLitShadowFragmentInput MSDFLitShadowVert(MSDFLitShadowInput v)
{
    MSDFLitShadowFragmentInput o;
    UNITY_INITIALIZE_OUTPUT(MSDFLitShadowFragmentInput, o);
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_TRANSFER_INSTANCE_ID(v, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

    TRANSFER_SHADOW_CASTER_NORMALOFFSET(o);
    o.uv = v.uv;
    return o;
}

float4 MSDFLitShadowFrag(MSDFLitShadowFragmentInput i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);

    clip(SDFAtlasCoverageMultiAt(i.uv) - _Cutoff);

    SHADOW_CASTER_FRAGMENT(i);
}

// --- Meta ----------------------------------------------------------------
//
// Lightmapper/GI pass, so a baked scene sees the graphic's actual albedo rather than the
// quad's untextured white.

#ifdef MSDF_META_PASS
#include "UnityMetaPass.cginc"

struct MSDFLitMetaInput
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float4 color : COLOR;
    float2 uv : TEXCOORD0;
    float2 uv1 : TEXCOORD1;
    float2 uv2 : TEXCOORD2;
};

struct MSDFLitMetaFragmentInput
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
    float4 color : COLOR;
};

MSDFLitMetaFragmentInput MSDFLitMetaVert(MSDFLitMetaInput v)
{
    MSDFLitMetaFragmentInput o;
    o.pos = UnityMetaVertexPosition(v.vertex, v.uv1, v.uv2, unity_LightmapST, unity_DynamicLightmapST);
    o.uv = v.uv;
    o.color = v.color;
    return o;
}

float4 MSDFLitMetaFrag(MSDFLitMetaFragmentInput i) : SV_Target
{
    // Coverage cannot be clipped here the way it is in the forward passes: the meta pass
    // rasterises in lightmap UV space, where derivatives of the mesh UV are unrelated to
    // the on-screen footprint, so the antialiasing width doesn't do anything.
    float coverage = SDFAtlasCoverageMultiAt(i.uv);

    float3 albedo = _Color.rgb;
    #if defined(_VERTEX_COLOR_ON)
    albedo *= i.color.rgb;
    #endif

    UnityMetaInput o;
    UNITY_INITIALIZE_OUTPUT(UnityMetaInput, o);
    o.Albedo = albedo * coverage;
    o.Emission = 0;

    return UnityMetaFragment(o);
}
#endif // MSDF_META_PASS

#endif // MSDF_ATLAS_LIT_INCLUDED
