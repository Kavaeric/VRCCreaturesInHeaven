
using UnityEngine;

#if UNITY_EDITOR

// TexelDensity
// Angular and texel-density math for the Asset Resolution tools. Converts between viewing
// distance, texel density (px/m) and smallest resolvable feature size, given a headset spec.
//
// The underlying relation is:
//   minDetail = 2 * dist * tan(PI / (ppd * 360))
//   texelDensity = 1 / minDetail
// where ppd is pixels per degree. Everything here is a rearrangement of that.
//
// Takes HeadsetSpec from HeadsetSpecs.cs but holds no headset data of its own.
public static class TexelDensity
{
    // Pixels per degree on the worst of the two axes. Using the worst axis means every
    // result is the conservative case: largest detail, lowest resolvable density.
    public static float PixelsPerDegree(HeadsetSpec hs)
    {
        return Mathf.Min(hs.resX / hs.fovH, hs.resY / hs.fovV);
    }

    // Angular size of a single pixel, halved — the shared term in the conversions below.
    static float TanHalfPixel(HeadsetSpec hs)
    {
        return Mathf.Tan(Mathf.PI / (PixelsPerDegree(hs) * 360f));
    }

    // Distance (m) at which texel density falls to targetDensity px/m.
    // dist = minDetail / (2 * tan(...))
    public static float DensityToDistance(float targetDensity, HeadsetSpec hs)
    {
        if (targetDensity <= 0f) return float.PositiveInfinity;
        float minDetail = 1f / targetDensity;
        return minDetail / (2f * TanHalfPixel(hs));
    }

    // Inverse of DensityToDistance: max resolvable texel density (px/m) at a given distance.
    public static float DistanceToDensity(float dist, HeadsetSpec hs)
    {
        if (dist <= 0f) return float.PositiveInfinity;
        return 1f / (2f * dist * TanHalfPixel(hs));
    }

    // Smallest feature (m) the headset can resolve at the given distance.
    public static float DistanceToMinDetail(float dist, HeadsetSpec hs)
    {
        float density = DistanceToDensity(dist, hs);
        if (float.IsInfinity(density)) return 0f;
        return 1f / density;
    }
}

#endif
