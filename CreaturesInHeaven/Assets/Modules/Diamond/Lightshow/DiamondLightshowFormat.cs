using UnityEngine;

// Packing math for a baked Diamond lightshow texture. Shared by the baker (which writes)
// and, mirrored as plain serialized fields, by DiamondManager (which reads at Start to
// seed the shader). Kept editor-agnostic (plain C#, no UnityEditor) so a future runtime
// consumer could reference the same constants.
//
// Texture layout (RGB24), vertical-stacked and interleaved:
//   column = frame f (a fixture's timeline runs horizontally, left to right)
//   row    = fixture i * TexelsPerFixture + slot
//            Fixture i owns TexelsPerFixture adjacent rows, and the shader reads a vertical
//            snippet of pixels at column f spanning those rows.
//
// For the current channel set, TexelsPerFixture = 2:
//   row 2i   (slot 0, colour): (drivenColour.r, drivenColour.g, drivenColour.b)
//            drivenColour = colour * brightness, scaled by 1/ColourScale into [0,1].
//            Shared by the lamp-glow (head) and the beam _Color.
//   row 2i+1 (slot 1, beam):   (zoom, focus, beamIntensity01)
//            zoom = tan(half-angle) raw, focus = 0..1 raw,
//            beamIntensity01 = beamIntensity / BeamScale into [0,1]. Beam-only.
//
// Vertical-stacking rather than side-by-side keeps width = frames (not frames*2), so the
// frame axis doesn't hit Unity's 16384 cap until ~16384 frames (~4.5 min at 60fps) instead
// of ~8192. It also reads cleaner: the even rows alone are the show's colour barcode.
//
// HDR is carried by two per-bake scale scalars (ColourScale, BeamScale): the texture stores
// SDR [0,1] and the shader multiplies back.
public static class DiamondLightshowFormat
{
    // Current channel set packs into this many RGB24 rows per fixture.
    public const int TexelsPerFixture = 2;

    // Slot 0 = colour row, slot 1 = beam row (offsets within a fixture's row band).
    public const int SlotColour = 0;
    public const int SlotBeam   = 1;

    // Unity's hard cap on a single Texture2D axis. Width = frames must be <= this;
    // height = fixtures * TexelsPerFixture must also be <= this (always tiny in practice).
    public const int MaxTextureAxis = 16384;

    // True when a flat layout fits without wrapping (both axes under the cap).
    public static bool FitsFlat(int frameCount, int fixtureCount) =>
        frameCount <= MaxTextureAxis
        && fixtureCount * TexelsPerFixture <= MaxTextureAxis;

    // Flat-layout texture dimensions. Width = one column per frame; height = one row
    // per (fixture, slot). Only valid when FitsFlat(frameCount, fixtureCount).
    public static int FlatWidth(int frameCount)    => Mathf.Max(1, frameCount);
    public static int FlatHeight(int fixtureCount) => Mathf.Max(1, fixtureCount * TexelsPerFixture);

    // Row of a fixture's given slot (colour/beam) in the flat layout.
    public static int RowOf(int fixtureIndex, int slot) => fixtureIndex * TexelsPerFixture + slot;
}
