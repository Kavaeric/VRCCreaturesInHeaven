// MSDFAtlasFilament.cginc
//
// Google Filament specular model, ported for MSDFAtlasLit.shader.
//
// This is a port rather than an include: Mochie's implementation lives in
// Assets/Mochie/Standard Shader/StandardBRDF.cginc, but that file cannot be included on its
// own. It is reached through StandardDefines.cginc, which pulls in the entire Mochie
// property set (rain, puddles, parallax, SSR, detail maps, Bakery, AreaLit, LTCGI) and
// declares the structs its functions take. Copying the thirty-odd lines that matter keeps
// this module standalone.
//
// Original implementations by Mochie (https://github.com/MochiesCode/Mochies-Unity-Shaders),
// themselves following Google Filament's material model
// (https://google.github.io/filament/Filament.html). The DFG lookup texture is Mochie's
// dfg-multiscatter.exr, referenced from Assets/Mochie/Unity/Textures/ rather than copied.
//
// Why Filament rather than Unity's built-in BRDF:
//
//   Unity's UNITY_BRDF_PBS approximates the split-sum environment term with an analytic
//   fresnel-and-surface-reduction fudge that loses energy as roughness rises, so rough
//   metals read as too dark. Filament looks the same term up in a precomputed DFG texture
//   and adds a multiscatter compensation factor, which puts that energy back. On signage
//   with a flat metallic finish the difference is visible as a brighter, more even sheen at
//   grazing angles.

#ifndef MSDF_ATLAS_FILAMENT_INCLUDED
#define MSDF_ATLAS_FILAMENT_INCLUDED

// --- DFG lookup ----------------------------------------------------------

// Precomputed split-sum lookup, indexed by (NdotV, perceptual roughness).
//
// x is the fresnel-scale term and y the fresnel-bias term of the environment BRDF, with the
// multiscatter energy already folded in. Sampled rather than approximated, which is the
// whole point of the model.
Texture2D _DFG;
SamplerState sampler_DFG;

// --- Ported from Mochie's StandardBRDF.cginc -----------------------------

// Energy conservation factor for multiple scattering, and the DFG sample it needs.
//
// Returned as a colour rather than a scalar because f0 is a colour on metals: a gold surface
// loses and regains a different amount of energy per channel.
float3 MSDFFilamentEnergyConservation(float NdotV, float perceptualRoughness, float3 f0, out float2 dfg)
{
    float2 dfguv = float2(NdotV, perceptualRoughness);
    dfg = _DFG.Sample(sampler_DFG, dfguv).xy;
    return 1.0 + f0 * (1.0 / max(dfg.y, 1e-4) - 1.0);
}

// Specular ambient occlusion. A surface that is occluded for diffuse light is not equally
// occluded for specular, and the amount of difference depends on roughness and view angle,
// which is what this curve encodes.
float MSDFFilamentSpecularAO(float NdotV, float ao, float roughness)
{
    return saturate(pow(NdotV + ao, exp2(-16.0 * roughness - 1.0)) - 1.0 + ao);
}

// Jimenez et al. 2016, "Practical Realtime Strategies for Accurate Indirect Occlusion".
//
// Occlusion darkens by absorbing light, but a coloured surface re-emits some of what it
// absorbs, so a red wall's crevices stay reddish rather than going neutral grey. This
// approximates that bounce.
float3 MSDFFilamentMultiBounceAO(float visibility, float3 baseColor)
{
    float3 a =  2.0404 * baseColor - 0.3324;
    float3 b = -4.7951 * baseColor + 0.6417;
    float3 c =  2.7552 * baseColor + 0.6903;

    return max(visibility, ((visibility * a + b) * visibility + c) * visibility);
}

// --- Box-projected reflection probes --------------------------------------
//
// Also ported from Mochie, which took the intersection maths from Unity's HDRP
// (GeometricTools.hlsl / LightEvaluation.hlsl) and simplified it for the built-in pipeline.
//
// Sampling probes by hand is unavoidable here: UnityGlobalIllumination would do it, but it
// applies Unity's own environment BRDF on the way out, which is exactly the term Filament
// replaces. So the diffuse GI still comes from Unity and the specular probe sample is taken
// directly.

// Simplified ray-box intersection, valid only from inside the box, which is the only case a
// reflection probe's influence volume produces.
float MSDFIntersectRayAABB(float3 start, float3 dir, float3 boxMin, float3 boxMax)
{
    float3 invDir = rcp(dir);

    float3 rbmin = (boxMin - start) * invDir;
    float3 rbmax = (boxMax - start) * invDir;

    float3 rbminmax = float3(
        (dir.x > 0.0) ? rbmax.x : rbmin.x,
        (dir.y > 0.0) ? rbmax.y : rbmin.y,
        (dir.z > 0.0) ? rbmax.z : rbmin.z);

    return min(min(rbminmax.x, rbminmax.y), rbminmax.z);
}

// Re-aims a reflection direction at where it actually strikes the probe's box, and returns
// how far away that was. Without this a box-projected probe looks correct only at its centre.
float MSDFProjectProbe(float3 worldPos, inout float3 R, float3 boxMin, float3 boxMax, float3 probePos)
{
    float projectionDistance = MSDFIntersectRayAABB(worldPos, R, boxMin, boxMax);
    R = (worldPos + projectionDistance * R) - probePos;
    return projectionDistance;
}

// Samples the scene's reflection probes, blending probe 0 and 1 as Unity does.
//
// The roughness remap (`1.7 - 0.7 * roughness`) is Unity's own convention for turning
// perceptual roughness into a cubemap mip, kept so this matches what every other shader in
// the scene picks out of the same probe.
float3 MSDFSampleReflectionProbes(float3 reflDir, float3 worldPos, float roughness)
{
    float3 baseReflDir = reflDir;
    float roughness0 = roughness;

    #ifdef UNITY_SPECCUBE_BOX_PROJECTION
        UNITY_BRANCH
        if (unity_SpecCube0_ProbePosition.w > 0)
        {
            MSDFProjectProbe(worldPos, baseReflDir, unity_SpecCube0_BoxMin.xyz, unity_SpecCube0_BoxMax.xyz, unity_SpecCube0_ProbePosition.xyz);
        }
    #endif

    roughness0 *= 1.7 - 0.7 * roughness0;
    float4 envSample0 = UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, baseReflDir, roughness0 * UNITY_SPECCUBE_LOD_STEPS);
    float3 probe = DecodeHDR(envSample0, unity_SpecCube0_HDR);

    // unity_SpecCube0_BoxMin.w is the blend weight between the two probes; below 1 means a
    // second probe overlaps here and both must be sampled.
    UNITY_BRANCH
    if (unity_SpecCube0_BoxMin.w < 0.99999)
    {
        float3 blendReflDir = reflDir;
        float roughness1 = roughness;

        #ifdef UNITY_SPECCUBE_BOX_PROJECTION
            UNITY_BRANCH
            if (unity_SpecCube1_ProbePosition.w > 0)
            {
                MSDFProjectProbe(worldPos, blendReflDir, unity_SpecCube1_BoxMin.xyz, unity_SpecCube1_BoxMax.xyz, unity_SpecCube1_ProbePosition.xyz);
            }
        #endif

        roughness1 *= 1.7 - 0.7 * roughness1;
        float4 envSample1 = UNITY_SAMPLE_TEXCUBE_SAMPLER_LOD(unity_SpecCube1, unity_SpecCube0, blendReflDir, roughness1 * UNITY_SPECCUBE_LOD_STEPS);
        float3 probe1 = DecodeHDR(envSample1, unity_SpecCube1_HDR);
        probe = lerp(probe1, probe, unity_SpecCube0_BoxMin.w);
    }

    return probe;
}

#endif // MSDF_ATLAS_FILAMENT_INCLUDED
