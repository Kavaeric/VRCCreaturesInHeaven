
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

    // Smallest triangle edge (m) worth authoring at the given screen-pixel density, as a
    // rule of thumb against quad overdraw.
    //
    // The GPU rasterises in 2x2 pixel quads: a triangle covering fewer pixels than that still
    // costs a full quad's worth of fragment shading, and the waste compounds where many such
    // triangles overlap. The real cost depends on how the triangles tile the screen, which is
    // not worth modelling here, so the tool uses a flat "keep triangles at least this many
    // pixels across" rule instead.
    //
    // pixelFloor is that pixel count. At 1 px/m the answer is pixelFloor metres; density
    // scales it down from there.
    public static float DensityToMinTriangleEdge(float density, float pixelFloor)
    {
        if (density <= 0f) return float.PositiveInfinity;
        return pixelFloor / density;
    }

    // Angular resolution (radians) of one pixel: the smallest angle the display can express,
    // and the acuity figure the imposter-distance math below is limited by.
    //
    // TanHalfPixel is half a pixel's angular size as a tangent, so the full angle is twice its
    // arctangent. At these magnitudes atan(x) == x to many decimal places, but the exact form
    // costs nothing here and stays correct if it is ever fed a coarse display.
    public static float AngularResolution(HeadsetSpec hs)
    {
        return 2f * Mathf.Atan(TanHalfPixel(hs));
    }

    // ---- Imposter distance -------------------------------------------------------------
    //
    // How far away an object can be before its parallax self-occlusion stops being visible,
    // at which point a flat billboard is indistinguishable from the real geometry.
    //
    // A viewer who moves sideways by `wander` sees near and far parts of an object shift
    // relative to each other. For an object of depth `objectDepth` at distance d, that
    // relative shift subtends roughly (wander * objectDepth) / d^2 radians, and measures
    // roughly (wander * objectDepth) / d metres across the object's own surface.
    //
    // Two separate things can make that shift invisible, so there are two distances and the
    // smaller one governs.

    // Acuity limit: the parallax shift falls below one pixel of angular resolution.
    //   acuity = (wander * objectDepth) / d^2   ->   d = sqrt(wander * objectDepth / acuity)
    public static float ImposterDistanceAcuity(float wanderDistance, float objectDepth, float acuityRadians)
    {
        if (wanderDistance <= 0f || objectDepth <= 0f) return 0f;
        if (acuityRadians <= 0f) return float.PositiveInfinity;
        return Mathf.Sqrt((wanderDistance * objectDepth) / acuityRadians);
    }

    // Weber limit: the parallax shift falls below the fraction of a nearby feature's spacing
    // that the eye can discriminate. Even a shift larger than one pixel goes unnoticed if it
    // is small next to the silhouette features it has to be judged against, so widely spaced
    // features hide parallax and closely spaced ones expose it.
    //   weber * separation = (wander * objectDepth) / d
    //   -> d = (wander * objectDepth) / (weber * separation)
    public static float ImposterDistanceWeber(float wanderDistance, float objectDepth, float weberFraction, float detailSeparation)
    {
        if (wanderDistance <= 0f || objectDepth <= 0f) return 0f;
        if (weberFraction <= 0f || detailSeparation <= 0f) return float.PositiveInfinity;
        return (wanderDistance * objectDepth) / (weberFraction * detailSeparation);
    }

    // The governing imposter distance: whichever limit kicks in first.
    public static float ImposterDistance(float wanderDistance, float objectDepth, float acuityRadians, float weberFraction, float detailSeparation)
    {
        return Mathf.Min(
            ImposterDistanceAcuity(wanderDistance, objectDepth, acuityRadians),
            ImposterDistanceWeber(wanderDistance, objectDepth, weberFraction, detailSeparation));
    }
}

#endif
