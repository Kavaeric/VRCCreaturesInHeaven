using UnityEngine;

// Profile-independent beam math shared between the runtime/shader path and the
// editor-only Bakery bake path. This is the C# mirror of the beam-length
// derivation in DiamondBeamCommon.cginc (DIAMOND_DERIVE_BEAM_LENGTH +
// BeamDensityAtDistance).
//
// Why this exists: the Bakery sub-module needs to know where a beam visually
// ends so a baked light's range can terminate with the shaft. Rather than let
// Bakery reach into the shader's internals (and silently drift when the shader
// math changes), both sides go through this one helper. Keep this in lockstep
// with DiamondBeamCommon.cginc -- if the falloff formula there changes, change
// it here too.
//
// Currently implements the ROUND cross-section only (pi * r^2), since round
// fixtures are the ones that bake as a cone/Spot light and need range tracking.
// Rect beams bake as mesh/point lights and don't use this path; calling the
// round derivation for a rect profile would give a wrong length, so callers
// should gate on shape.
public static class DiamondBeamMath
{
    // Per-point brightness density at a distance from the emitter. Mirror of
    // BeamDensityAtDistance in DiamondBeamCommon.cginc. crossArea is the cone's
    // cross-section area at the given distance; emitterArea is the area at the
    // emitter face. (Round: both are pi * r^2.)
    public static float BeamDensityAtDistance(float distance, float crossArea, float emitterArea,
        float beamIntensity, float haze)
    {
        float geometric  = emitterArea / Mathf.Max(crossArea, 1e-6f);
        float extinction = Mathf.Exp(-haze * distance);
        return geometric * haze * extinction * beamIntensity;
    }

    // Cross-section area of a round cone at a given distance from the emitter.
    // radius grows by spread (tan of half-angle) per metre. Mirror of the
    // DIAMOND_CROSS_AREA expression in DiamondBeamRound.shader (without the
    // soft-edge term, which only dilutes the visible halo, not the length).
    static float RoundCrossArea(float distance, float emitterRadius, float spreadTan)
    {
        float radius = emitterRadius + spreadTan * distance;
        return Mathf.PI * radius * radius;
    }

    // Finds the distance at which beam density falls below the cutoff threshold,
    // for a ROUND cone. Mirror of DIAMOND_DERIVE_BEAM_LENGTH: same 8-iteration
    // bisection against the same density formula, so C# and the shader agree on
    // where the beam ends.
    //
    //   emitterRadius    - emitter diameter / 2 (FixtureWidth * 0.5)
    //   spreadTan        - tan(half-angle), i.e. BeamProps.localEulerAngles.x
    //   beamIntensity    - animated beam intensity (BeamProps.localScale.y)
    //   haze             - material _HazeDensity
    //   cutoffThreshold  - material _BeamCutoffThreshold
    //   beamLengthMax    - material _BeamLengthMax (hard cap)
    public static float DeriveRoundBeamLength(float emitterRadius, float spreadTan,
        float beamIntensity, float haze, float cutoffThreshold, float beamLengthMax)
    {
        float threshold = Mathf.Max(cutoffThreshold, 1e-5f);
        float intensity = Mathf.Max(beamIntensity, 0f);
        haze            = Mathf.Max(haze, 1e-5f);
        float emArea    = RoundCrossArea(0f, emitterRadius, spreadTan);

        float DensityAt(float d) =>
            BeamDensityAtDistance(d, RoundCrossArea(d, emitterRadius, spreadTan), emArea, intensity, haze);

        // Below threshold right at the emitter: no visible beam.
        if (DensityAt(0f) <= threshold)
            return 0f;

        // Still above threshold at the hard cap: clamp to the cap.
        if (DensityAt(beamLengthMax) > threshold)
            return beamLengthMax;

        // Bisect for the crossing. 8 iterations matches the shader's [unroll].
        float lo = 0f;
        float hi = beamLengthMax;
        for (int it = 0; it < 8; it++)
        {
            float mid = 0.5f * (lo + hi);
            if (DensityAt(mid) > threshold) lo = mid; else hi = mid;
        }
        return hi;
    }
}
