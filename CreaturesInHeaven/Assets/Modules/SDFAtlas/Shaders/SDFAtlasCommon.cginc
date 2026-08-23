// SDFAtlasCommon.cginc
//
// Shared logic for shaders that render graphics from a packed SDF atlas.
//
// The blend-mode-independent parts live here: atlas sampling with correct derivatives, and
// edge reconstruction. Individual shaders add only their output and blend state.
//
// Mesh UVs are plain atlas texture coordinates: a quad's UV island is placed directly over
// the graphic it should show, in 0..1 atlas space. Selecting a graphic is therefore purely a
// UV authoring matter, and every sign shares one material.
//
// Atlas layout is described by SDFAtlasInfo.cs and the .sdfatlas.json manifest written
// beside each atlas texture. The stored field is single-channel, where 0.5 is the shape
// edge, higher is inside, lower is outside.

#ifndef SDF_ATLAS_COMMON_INCLUDED
#define SDF_ATLAS_COMMON_INCLUDED

#include "UnityCG.cginc"

// --- Shared properties ---------------------------------------------------
// Every SDFAtlas shader declares these in its Properties block; they are defined once here
// so the shaders themselves stay short.

sampler2D _Atlas;
float4 _Atlas_TexelSize;

float4 _Color;
float _Intensity;

float _Spread;

float _EdgeBias;
float _EdgeSoftness;

// --- Vertex plumbing -----------------------------------------------------

struct SDFAtlasVertexInput
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
    float4 color : COLOR;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct SDFAtlasFragmentInput
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
    float4 color : COLOR;
    UNITY_VERTEX_OUTPUT_STEREO
};

// Standard vertex stage. UVs pass through unmodified.
//
// Also pass through vertex colours as the end shader may use it for colouring/tinting graphics.
SDFAtlasFragmentInput SDFAtlasVert(SDFAtlasVertexInput v)
{
    SDFAtlasFragmentInput o;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_INITIALIZE_OUTPUT(SDFAtlasFragmentInput, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

    o.pos = UnityObjectToClipPos(v.vertex);
    o.uv = v.uv;
    o.color = v.color;
    return o;
}

// --- Atlas sampling ------------------------------------------------------

// Samples the atlas at a mesh UV and returns the raw stored distance value.
//
// tex2Dgrad rather than tex2D so the mip footprint is stated explicitly. It matches what
// tex2D would compute here, but the atlas packs unrelated graphics next to each other, so
// mip selection is worth pinning down rather than leaving to be re-derived if this function
// ever grows a UV transform.
float SDFAtlasSample(float2 meshUV)
{
    return tex2Dgrad(_Atlas, meshUV, ddx(meshUV), ddy(meshUV)).r;
}

// --- Edge reconstruction -------------------------------------------------

// Converts a stored distance value into a 0..1 coverage value with an antialiased edge.
//
// fwidth gives how fast the field changes per screen pixel, so smoothstepping across that
// width keeps the edge exactly one pixel wide at any distance or viewing angle.
//
// _EdgeBias shifts the threshold rather than the field. It is expressed in cell texels and
// converted to stored units by inverting the encoder's mapping (half the 0..1 range covers
// `spread` texels), so a bias of 1 dilates the shape by one texel whatever the atlas's
// spread happens to be.
float SDFAtlasCoverage(float distance)
{
    float edgeWidth = max(fwidth(distance), 1e-5) * _EdgeSoftness;
    float threshold = 0.5 - _EdgeBias * (0.5 / max(_Spread, 1e-5));
    return smoothstep(threshold - edgeWidth, threshold + edgeWidth, distance);
}

// Convenience: sample and reconstruct in one step.
float SDFAtlasCoverageAt(float2 meshUV)
{
    return SDFAtlasCoverage(SDFAtlasSample(meshUV));
}

// --- Multi-channel (MSDF) ------------------------------------------------
//
// MSDF atlases store three distance fields rather than one, each covering a different
// subset of the shape's edges. No single channel contains a corner, so none of them
// creases, and bilinear interpolation leaves all three intact. The median recovers the
// true distance everywhere, including at sharp corners, where two channels cross and the
// median discards the two outliers.

// Median of three values: the middle one by magnitude ordering.
//
// Written as min/max rather than with branches because it compiles to a handful of ALU
// instructions with no divergence, which matters in a fragment shader, or so I've been told.
float SDFAtlasMedian(float3 rgb)
{
    return max(min(rgb.r, rgb.g), min(max(rgb.r, rgb.g), rgb.b));
}

// Samples all three channels of an MSDF atlas. Same tex2Dgrad reasoning as single-channel path.
float3 SDFAtlasSampleMulti(float2 meshUV)
{
    return tex2Dgrad(_Atlas, meshUV, ddx(meshUV), ddy(meshUV)).rgb;
}

// Reconstructs coverage from three channels.
//
// The median is taken after the hardware's bilinear filtering, which is the whole point
// and easy to get backwards. Interpolating three channels and then taking the median
// preserves corners.
//
// fwidth is applied to the median rather than to the individual channels, so the
// antialiasing width tracks the reconstructed edge rather than any one channel's edge.
// Near a corner the channels move at different rates, and using one of them here would
// make the edge width flicker as the corner passes across a pixel.
float SDFAtlasCoverageMulti(float3 msd)
{
    float distance = SDFAtlasMedian(msd);

    float edgeWidth = max(fwidth(distance), 1e-5) * _EdgeSoftness;
    float threshold = 0.5 - _EdgeBias * (0.5 / max(_Spread, 1e-5));

    return smoothstep(threshold - edgeWidth, threshold + edgeWidth, distance);
}

// Convenience: sample and reconstruct an MSDF in one step.
float SDFAtlasCoverageMultiAt(float2 meshUV)
{
    return SDFAtlasCoverageMulti(SDFAtlasSampleMulti(meshUV));
}

#endif // SDF_ATLAS_COMMON_INCLUDED
