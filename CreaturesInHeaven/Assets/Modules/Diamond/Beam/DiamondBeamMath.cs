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

    // The lateral diffusion rate. Mirror of DIAMOND_SCATTER_K in
    // DiamondBeamCommon.cginc: the scale that both spill sources grow by per
    // metre of depth. Keep in lockstep with the shader.
    public const float ScatterK = 1.0f;

    // Worst-case lateral half-extent of the beam at its far cap, along one axis.
    // Mirror of the per-axis half-width ExpandUnitCubeToFrustumBounds computes in
    // DiamondBeamCommon.cginc, so the CPU-side renderer bounds enclose exactly the
    // geometry the vertex shader rasterises. Keep the two in lockstep.
    //
    // The half-extent is the emitter half-size plus, over the max beam length,
    // the geometric spread, the lateral spill (defocus + haze scatter, combined
    // in quadrature the way the shader does), and the shear lean:
    //
    //   emitterHalf + (spread + spillSpread + |shear|) * maxLen
    //
    // Worst case on focus (fully defocused, rate = spread) so the bound never
    // undersizes regardless of the animated _Focus. haze/strength/shear are
    // material-level, so callers pass the material's values.
    //
    //   emitterHalf   - emitter size on this axis / 2
    //   spreadTan     - worst-case geometric spread on this axis (tan half-angle)
    //   shear         - |shear| on this axis (0 for the round profile)
    //   haze          - material _HazeDensity
    //   scatterStr    - material _ScatterStrength (0..1)
    //   maxLen        - worst-case beam length (_BeamLengthMax)
    public static float LateralHalfExtent(float emitterHalf, float spreadTan, float shear,
        float haze, float scatterStr, float maxLen)
    {
        maxLen = Mathf.Max(maxLen, 0f);

        // Haze-scatter spill rate (per metre), matching the shader's scatterRate.
        float scatterRate = ScatterK * Mathf.Max(haze, 0f) * Mathf.Clamp01(scatterStr);
        // Focus spill worst case is the spread itself (focus = 0 -> rate = spread),
        // combined with scatter in quadrature exactly as the shader's spillSpread.
        float focusRate  = ScatterK * Mathf.Max(spreadTan, 0f);
        float spillSpread = Mathf.Sqrt(focusRate * focusRate + scatterRate * scatterRate);

        return emitterHalf + (Mathf.Max(spreadTan, 0f) + spillSpread + Mathf.Abs(shear)) * maxLen;
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
