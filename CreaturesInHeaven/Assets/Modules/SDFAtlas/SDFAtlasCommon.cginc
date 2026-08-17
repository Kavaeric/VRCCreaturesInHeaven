// SDFAtlasCommon.cginc
//
// Shared machinery for shaders that render signage from a packed SDF atlas.
//
// The blend-mode-independent parts live here: UDIM cell addressing, atlas sampling with
// correct derivatives, and edge reconstruction. Individual shaders add only their output
// and blend state.
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

float4 _GridSize;
float _CellSize;
float _Padding;
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

// Standard vertex stage. UVs pass through unmodified, integer part included -- the whole
// UDIM addressing scheme depends on the integer part surviving to the fragment stage.
//
// Vertex colour passes through unmodified too, whether or not a given shader ends up using
// it, since it costs nothing to carry and keeps this stage usable by any blend mode.
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

// --- Atlas addressing ----------------------------------------------------

// Converts a mesh UV into the atlas UV of the corresponding cell, and returns the
// derivatives to sample that atlas UV with.
//
// The integer part of the mesh UV selects the cell; the fractional part positions within it.
// Derivatives are returned separately because they cannot be recomputed correctly from the
// atlas UV.
void SDFAtlasAddress(float2 meshUV, out float2 atlasUV, out float2 dx, out float2 dy)
{
    float2 cell = floor(meshUV);
    float2 local = meshUV - cell;

    // Guard the exact-integer case. A UV island whose far edge lands precisely on the next
    // tile boundary (e.g. v = 4.0) has a fractional part of 0.0, which wraps to the wrong
    // side of the cell and shows a sliver of the neighbouring graphic. Stepping the cell
    // back keeps that last row inside the cell the island was authored in.
    cell -= (local <= 0.0 && meshUV > 0.0) ? 1.0 : 0.0;
    local = meshUV - cell;

    // Cell coordinates follow UV space: (0,0) is the bottom-left cell, +Y runs up, exactly
    // as UDIM tiles do, e.g. a UV island authored in tile (3,1) in Blender lands on cell
    // (3,1) here.
    float2 gridSize = _GridSize.xy;
    float2 cellUV = cell;

    // Inset the sampled region by the padding so `local` spans only the artwork area. The
    // padding carries distance values continuing past the artwork's edge, there to give
    // bilinear taps near the boundary something correct to read; it is not part of the image.
    float paddingUV = _Padding / _CellSize;
    float2 artworkLocal = lerp(paddingUV, 1.0 - paddingUV, local);

    // Clamp half a texel inside the cell, so even a tap exactly on the boundary cannot reach
    // into the neighbour.
    float halfTexel = 0.5 / _CellSize;
    artworkLocal = clamp(artworkLocal, halfTexel, 1.0 - halfTexel);

    atlasUV = (cellUV + artworkLocal) / gridSize;

    // Derivatives come from the un-fracted mesh UV. The fractional part is discontinuous at
    // tile boundaries, so implicit derivatives spike to garbage along the seam of any quad
    // that straddles one, producing a line of wrong-mip pixels. The mesh UV is continuous
    // across the quad, so scaling its derivatives into atlas space gives a correct footprint
    // everywhere.
    dx = ddx(meshUV) / gridSize;
    dy = ddy(meshUV) / gridSize;
}

// Samples the atlas at a mesh UV and returns the raw stored distance value.
//
// tex2Dgrad rather than tex2D because the derivatives must be the ones computed from the
// un-fracted UV; letting the GPU infer them from the atlas UV reintroduces exactly the
// seam artifact the explicit derivatives exist to avoid.
float SDFAtlasSample(float2 meshUV)
{
    float2 atlasUV, dx, dy;
    SDFAtlasAddress(meshUV, atlasUV, dx, dy);
    return tex2Dgrad(_Atlas, atlasUV, dx, dy).r;
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

// Convenience: sample and reconstruct in one step, which is all most shaders need.
float SDFAtlasCoverageAt(float2 meshUV)
{
    return SDFAtlasCoverage(SDFAtlasSample(meshUV));
}

#endif // SDF_ATLAS_COMMON_INCLUDED
