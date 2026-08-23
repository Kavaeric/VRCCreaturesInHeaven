// MSDFAtlasBakery.cginc
//
// Bakery MonoSH lightmap decoding for MSDFAtlasLit.shader.
//
// Ported from Mochie's StandardBakery.cginc, which in turn follows Bakery's own shaders
// (Bakery is by Mr F). Ported as we only need MONOSH.

#ifndef MSDF_ATLAS_BAKERY_INCLUDED
#define MSDF_ATLAS_BAKERY_INCLUDED

// Geomerics' non-linear L1 spherical harmonic evaluation.
//
// A plain linear SH reconstruction can go negative and tends to wash out at low light levels.
// This reweights the result by how directional the light actually is, which keeps contrast in
// dimly lit areas at the cost of a little arithmetic.
float MSDFBakeryEvaluateDiffuseL1Geomerics(float L0, float3 L1, float3 n)
{
    // Average energy.
    float R0 = L0;

    // Average direction of incoming light.
    float3 R1 = 0.5 * L1;

    // Directional brightness.
    float lenR1 = length(R1);

    // Linear angle between the normal and that direction, mapped to 0-1.
    float q = dot(normalize(R1), n) * 0.5 + 0.5;
    q = saturate(q);

    // Exponent for q, lerping from 1 (linear) to 3 (cubic) with how directional the light is.
    float p = 1.0 + 2.0 * lenR1 / R0;

    // Normalisation constant.
    float a = (1.0 - lenR1 / R0) / (1.0 + lenR1 / R0);

    return R0 * (a + (1.0 - a) * (p + 1.0) * pow(q, p));
}

// Decodes a Bakery MonoSH lightmap.
//
// `diffuseColor` arrives holding the plain lightmap colour (the L0 term) and leaves holding
// the directionally-shaded result. `directionalSample` is the raw unity_LightmapInd texel.
void MSDFBakeryMonoSH(inout float3 diffuseColor, out float3 specularColor,
    float3 directionalSample, float3 normal, float3 viewDir, float roughness)
{
    float3 L0 = diffuseColor;

    // The directional map stores the direction biased into 0-1, so undo that first.
    float3 nL1 = directionalSample * 2 - 1;
    float3 L1x = nL1.x * L0 * 2;
    float3 L1y = nL1.y * L0 * 2;
    float3 L1z = nL1.z * L0 * 2;

    float3 sh;

    #if defined(BAKERY_SHNONLINEAR)
        // Evaluated on luminance rather than per channel, then applied as a single scale.
        // Bakery does it this way because three separate Geomerics evaluations cost three
        // times as much and the hue barely differs between them.
        float lumaL0 = dot(L0, 1);
        float lumaL1x = dot(L1x, 1);
        float lumaL1y = dot(L1y, 1);
        float lumaL1z = dot(L1z, 1);
        float lumaSH = MSDFBakeryEvaluateDiffuseL1Geomerics(lumaL0, float3(lumaL1x, lumaL1y, lumaL1z), normal);

        sh = L0 + normal.x * L1x + normal.y * L1y + normal.z * L1z;
        float regularLumaSH = dot(sh, 1);

        // Faded in rather than applied outright: the ratio is unstable as the linear result
        // approaches zero, so near-black texels keep the linear value.
        sh *= lerp(1, lumaSH / regularLumaSH, saturate(regularLumaSH * 16));
    #else
        sh = L0 + normal.x * L1x + normal.y * L1y + normal.z * L1z;
    #endif

    diffuseColor = max(sh, 0.0);

    specularColor = 0;

    #if defined(BAKERY_LMSPEC)
        // Specular from baked light.
        float3 dominantDir = nL1;
        float3 halfDir = Unity_SafeNormalize(normalize(dominantDir) + viewDir);
        float NdotH = saturate(dot(normal, halfDir));
        float spec = GGXTerm(NdotH, roughness);

        // Evaluate the SH along the dominant direction rather than the normal: this is the
        // radiance arriving from where the highlight is being reflected from.
        sh = L0 + dominantDir.x * L1x + dominantDir.y * L1y + dominantDir.z * L1z;

        specularColor = max(spec * sh, 0.0);
    #endif
}

#endif // MSDF_ATLAS_BAKERY_INCLUDED
