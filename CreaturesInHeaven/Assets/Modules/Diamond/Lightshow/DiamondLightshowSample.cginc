// DiamondLightshowSample.cginc
//
// The single source of truth for reading a baked Diamond lightshow texture on the GPU.
// Shared verbatim by every shader that samples the show (the beam shapes via
// DiamondBeamCommon.cginc, and the lamp glow via DiamondLampGlow.shader), so the texture
// globals, the addressing math, and the frame lerp can't drift between them. With one copy,
// a change to the frame-lerp or the colour-row index reaches the lamp glow and the beam
// colour together rather than desyncing them.
//
// Guarded by DIAMOND_LIGHTSHOW_TEX: a shader includes this unconditionally, but only the
// texture-path variant compiles the body. See DIAMOND-GPU-ACCEL.md.
//
// Texture layout (RGBA32), authored by DiamondLightshowBaker / DiamondLightshowFormat:
//   column = frame f
//   row    = _FixtureRow * _UdonDiamondLightshowTexelsPerFixture + slot
//            slot 0 = colour (drivenColour.rgb, 1) scaled by 1/ColourScale
//            slot 1 = beam   (zoom, focus, beamIntensity/BeamScale, 1)
// The current frame column is _UdonDiamondLightshowFrames[_ShowIndex]; callers lerp the two
// bracketing columns for smoothness, since the bake fps is below the render fps.
#ifndef DIAMOND_LIGHTSHOW_SAMPLE_INCLUDED
#define DIAMOND_LIGHTSHOW_SAMPLE_INCLUDED

#ifdef DIAMOND_LIGHTSHOW_TEX

// These are set from Udon via VRCShader.SetGlobal*, so they must start with _Udon: VRChat
// blocks Udon from writing any global shader property outside that namespace. They're
// prefixed with _UdonDiamondLightshow rather than just _UdonLightshow to avoid colliding
// with any other world or package's own _Udon-namespaced globals. Per-instance _FixtureRow
// and _ShowIndex are set via MaterialPropertyBlock rather than as globals, so they keep
// plain names and are declared by each shader's own instancing buffer, not here.
// Load-only (integer texel fetch), so no SamplerState is needed.
Texture2D   _UdonDiamondLightshowTex;
float       _UdonDiamondLightshowTexelsPerFixture;   // rows per fixture (=2 now)
float       _UdonDiamondLightshowColourScale;        // HDR de-scale for the colour row
float       _UdonDiamondLightshowBeamScale;          // HDR de-scale for the beam-intensity channel
float       _UdonDiamondLightshowFrameCount;         // total baked columns

// One entry per manager/show; a fixture reads slot _ShowIndex. Fixed length is the max
// concurrent managers supported; a manager writes only its own slot. Must match
// DIAMOND_MAX_SHOWS wherever the frame array is sized on the CPU side.
#define DIAMOND_MAX_SHOWS 16
float       _UdonDiamondLightshowFrames[DIAMOND_MAX_SHOWS];

// Loads one row's texel at an integer frame column (point/Load, no filtering).
float4 DiamondLoadTexel(int row, int frameCol)
{
    // Clamp the column into range so the far end doesn't wrap.
    frameCol = clamp(frameCol, 0, (int)_UdonDiamondLightshowFrameCount - 1);
    return _UdonDiamondLightshowTex.Load(int3(frameCol, row, 0));
}

// Resolves _ShowIndex to this show's current fractional frame. Rounds the (float-stored)
// show index to its integer slot and clamps into the array bounds.
float DiamondCurrentFrame(float showIndex)
{
    int slot = (int)(showIndex + 0.5);
    slot = clamp(slot, 0, DIAMOND_MAX_SHOWS - 1);
    return _UdonDiamondLightshowFrames[slot];
}

// The colour row index for a fixture (slot 0 of its row band).
int DiamondColourRow(float fixtureRow)
{
    return (int)(fixtureRow * _UdonDiamondLightshowTexelsPerFixture + 0.5);
}

// Reads a fixture's colour row (drivenColour = colour x brightness) at the current
// fractional frame, lerping the two bracketing columns, and undoes the HDR de-scale.
// This is all the lamp glow needs; the beam layers zoom/focus/intensity on top of it.
float3 DiamondSampleColour(float fixtureRow, float showIndex)
{
    float frame = DiamondCurrentFrame(showIndex);
    int   f0    = (int)floor(frame);
    float frac  = frame - f0;

    int rowC = DiamondColourRow(fixtureRow);
    float3 c0 = DiamondLoadTexel(rowC, f0).rgb;
    float3 c1 = DiamondLoadTexel(rowC, f0 + 1).rgb;
    return lerp(c0, c1, frac) * _UdonDiamondLightshowColourScale;
}

// Reads a fixture's full animated state (colour + beam channels) at the current fractional
// frame. Colour is the same value DiamondSampleColour returns; the beam row adds
// zoom/focus/intensity. The beam intensity is HDR-de-scaled but not master-scaled here: the
// caller folds in _UdonDiamondBeamIntensityScale so it flows through the early-out and
// beam-length derivation, matching the proxy path.
void DiamondSampleLightshow(float fixtureRow, float showIndex,
    out float4 color, out float zoom, out float focus, out float intensity)
{
    float frame = DiamondCurrentFrame(showIndex);
    int   f0    = (int)floor(frame);
    float frac  = frame - f0;

    int rowC = DiamondColourRow(fixtureRow);   // colour row
    int rowB = rowC + 1;                        // beam row

    float4 c0 = DiamondLoadTexel(rowC, f0);
    float4 c1 = DiamondLoadTexel(rowC, f0 + 1);
    float4 b0 = DiamondLoadTexel(rowB, f0);
    float4 b1 = DiamondLoadTexel(rowB, f0 + 1);

    float4 c = lerp(c0, c1, frac);
    float4 b = lerp(b0, b1, frac);

    color     = float4(c.rgb * _UdonDiamondLightshowColourScale, 1);
    zoom      = b.r;
    focus     = b.g;
    intensity = b.b * _UdonDiamondLightshowBeamScale;
}

#endif // DIAMOND_LIGHTSHOW_TEX
#endif // DIAMOND_LIGHTSHOW_SAMPLE_INCLUDED
