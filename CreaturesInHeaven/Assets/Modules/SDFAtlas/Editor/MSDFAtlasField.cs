using UnityEngine;

// Multi-channel signed distance field generation. Based on Victor Chlumský's msdfgen and
// master's thesis (https://github.com/Chlumsky/msdfgen).
//
// Each channel stores the distance to only those edges assigned to it by the colourer, so
// no single channel contains a corner. A field without creases survives bilinear
// interpolation intact. The shader takes the median of the three channels, which reproduces
// the true distance everywhere including at corners, where two channels cross.
public static class MSDFAtlasField
{
    // Per-channel distance at a point, before encoding.
    public struct MultiDistance
    {
        public double r;
        public double g;
        public double b;
    }

    // --- Generation --------------------------------------------------------

    // Generates an MSDF over a pixel grid.
    //
    // The shape must be framed into the target pixel space and already coloured (see
    // MSDFAtlasEdgeColouring.Apply). Returns three interleaved floats per texel, row-major:
    // r, g, b, r, g, b... in raw signed pixel distances.
    public static float[] Generate(SDFAtlasShape shape, int width, int height)
    {
        var field = new float[width * height * 3];
        if (shape == null || shape.EdgeCount == 0) return field;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var point = new Vector2(x + 0.5f, y + 0.5f);
                MultiDistance distance = DistanceAt(shape, point);

                int index = (y * width + x) * 3;
                field[index + 0] = (float)distance.r;
                field[index + 1] = (float)distance.g;
                field[index + 2] = (float)distance.b;
            }
        }
        return field;
    }

    // Per-channel signed distance at a single point.
    //
    // Three independent nearest-edge searches run in lockstep over the same edge list, each
    // considering only edges that carry its channel. They are kept in one pass rather than
    // three because the edge iteration and the distance solve are the expensive parts, and
    // an edge in two channels would otherwise be solved twice.
    //
    // Each channel's result keeps its own sign -- see the note on per-channel signs below.
    public static MultiDistance DistanceAt(SDFAtlasShape shape, Vector2 point)
    {
        var red = ChannelState.Empty;
        var green = ChannelState.Empty;
        var blue = ChannelState.Empty;

        foreach (SDFAtlasShape.Contour contour in shape.contours)
        {
            int edgeCount = contour.edges.Count;
            if (edgeCount == 0) continue;

            for (int i = 0; i < edgeCount; i++)
            {
                SDFAtlasShape.Edge edge = contour.edges[i];

                int channels = (int)edge.colour;

                // An edge in no channel cannot contribute anything; skip before paying for
                // the distance solve.
                if (channels == 0) continue;

                var distance = SDFAtlasEdgeDistance.Distance(edge, point, out double param);

                // Neighbours within the contour, wrapping at both ends. Every edge's
                // perpendicular extension is bounded by the bisector with its neighbour, so
                // the accumulator needs both regardless of which edge ends up nearest.
                SDFAtlasShape.Edge previous = contour.edges[(i - 1 + edgeCount) % edgeCount];
                SDFAtlasShape.Edge next = contour.edges[(i + 1) % edgeCount];

                if ((channels & (int)SDFAtlasShape.EdgeColour.Red) != 0)
                    red.Consider(edge, previous, next, distance, param, point);

                if ((channels & (int)SDFAtlasShape.EdgeColour.Green) != 0)
                    green.Consider(edge, previous, next, distance, param, point);

                if ((channels & (int)SDFAtlasShape.EdgeColour.Blue) != 0)
                    blue.Consider(edge, previous, next, distance, param, point);
            }
        }

        return new MultiDistance
        {
            r = red.Resolve(point),
            g = green.Resolve(point),
            b = blue.Resolve(point),
        };
    }

    // Tracks one channel's accumulated perpendicular-distance state across every edge in
    // that channel. Thin wrapper so DistanceAt reads the same as before; all the actual work
    // is in SDFAtlasEdgeDistance.PerpendicularAccumulator (see its comment for why every
    // edge must be folded in, and not just the nearest).
    struct ChannelState
    {
        SDFAtlasEdgeDistance.PerpendicularAccumulator accumulator;

        public static ChannelState Empty => new ChannelState
        {
            accumulator = SDFAtlasEdgeDistance.PerpendicularAccumulator.Empty,
        };

        public void Consider(SDFAtlasShape.Edge edge,
                             SDFAtlasShape.Edge previous, SDFAtlasShape.Edge next,
                             SDFAtlasEdgeDistance.SignedDistance distance, double param, Vector2 point)
        {
            accumulator.Add(edge, previous, next, distance, param, point);
        }

        public double Resolve(Vector2 point) => accumulator.Resolve(point);
    }

    // --- On per-channel signs -----------------------------------------------
    //
    // Each channel keeps the sign its own nearest edge gives it, and the three are NOT
    // harmonised. This is load-bearing and worth stating explicitly, because forcing them
    // to agree is an easy and destructive mistake.
    //
    // At a corner texel the channels genuinely disagree about which side of the shape the
    // point is on: each measures against a different edge, and near the corner those edges
    // put the point on opposite sides. That disagreement is exactly what the median
    // resolves into a sharp corner. Overwriting the signs from a single per-texel fill test
    // leaves the channels differing only in magnitude, which collapses the median to what a
    // single channel would have produced, re-introducing the corner rounding problem.
    //
    // Nested contours (a letter counter inside its glyph) are handled by contour winding
    // during colouring and by the edge orientations themselves, not by a fill test.

    // --- Encoding ----------------------------------------------------------

    // Remaps raw signed distances to 0..1 for storage, matching the single-channel encoding
    // so spread means the same thing in both.
    public static float[] Encode(float[] field, float spreadPixels)
    {
        var encoded = new float[field.Length];
        float invRange = 0.5f / Mathf.Max(spreadPixels, 1e-6f);

        for (int i = 0; i < field.Length; i++)
            encoded[i] = Mathf.Clamp01(0.5f + field[i] * invRange);

        return encoded;
    }

    // The shader's reconstruction, in C#.
    //
    // Used by the validation harness so previews show exactly what the GPU will produce
    // rather than an idealised version of it.
    public static float Median(float a, float b, float c) =>
        Mathf.Max(Mathf.Min(a, b), Mathf.Min(Mathf.Max(a, b), c));

    // --- Error correction ---------------------------------------------------

    // Parameter values this close to a texel are ignored when looking for channel crossings.
    const double ArtifactTEpsilon = 0.01;

    // How far the interpolated median may stray beyond its endpoints before it counts as an
    // artifact, as a multiple of the distance it travelled. msdfgen's default.
    const double DefaultMinDeviationRatio = 1.11111111111111111;

    // Removes interpolation artifacts by collapsing offending texels to their median.
    // Ported from msdfgen's BaseArtifactClassifier path (the variant that works from
    // the SDF alone, without re-evaluating the true shape).
    //
    // It's tempting to flag texels whose three channels disagree strongly, but that so
    // happens to describe every corner, so that's out the window.
    //
    // The real test is about interpolation. Between two adjacent texels, the median is
    // piecewise linear, switching which channel wins wherever two channels cross. At such a
    // crossing the median can spike to a value neither endpoint justifies, resulting in an
    // isolated bright or dark pixel along an otherwise clean edge. By contrast, a corner
    // produces crossings whose medians (almost) stay within the range its endpoints bracket.
    // Right at a corner, the crossing does stray outside that bracket, legitimately, and that
    // excursion is what a sharp corner looks like in the interpolated median. The range test
    // alone can't tell that excursion apart from a real artifact, so texels next to an actual
    // corner must be exempted from the "outside bounds" half of the test before it runs.
    // We use `protectCorners` to flag exemptions and not touch them.
    //
    // So: protect the four texels around every true corner, then for each neighbouring pair
    // find where each pair of channels crosses, interpolate the median there, and check
    // whether it stayed in range. Protected texels should only fail that check on an
    // outright sign inversion.
    public static int CorrectErrors(SDFAtlasShape shape, float[] encoded, int width, int height,
                                    float spreadTexels = 4f,
                                    double minDeviationRatio = DefaultMinDeviationRatio)
    {
        bool[] protectedTexels = ProtectCorners(shape, width, height);

        // Which texels need collapsing. Decided entirely from the original field, then
        // applied in a second pass.
        var flagged = new bool[width * height];
        int count = 0;

        // The stored field's rate of change per texel step. The range test compares the
        // median's excursion against how far it could legitimately move over that step, so
        // it needs to know what one texel of distance is worth in stored units.
        //
        // Half the 0..1 range covers `spread` texels, so one texel step moves the stored
        // value by 0.5 / spread.
        double span = minDeviationRatio * (0.5 / Mathf.Max(spreadTexels, 1e-6f));

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 3;

                float cr = encoded[index + 0];
                float cg = encoded[index + 1];
                float cb = encoded[index + 2];
                float centreMedian = Median(cr, cg, cb);

                bool centreProtected = protectedTexels[y * width + x];
                bool artifact = false;

                // Orthogonal neighbours: the median varies linearly between them.
                if (x > 0)
                    artifact |= HasLinearArtifact(encoded, index, (y * width + x - 1) * 3, centreMedian, span, centreProtected);

                if (!artifact && x < width - 1)
                    artifact |= HasLinearArtifact(encoded, index, (y * width + x + 1) * 3, centreMedian, span, centreProtected);

                if (!artifact && y > 0)
                    artifact |= HasLinearArtifact(encoded, index, ((y - 1) * width + x) * 3, centreMedian, span, centreProtected);

                if (!artifact && y < height - 1)
                    artifact |= HasLinearArtifact(encoded, index, ((y + 1) * width + x) * 3, centreMedian, span, centreProtected);

                if (artifact)
                {
                    flagged[y * width + x] = true;
                    count++;
                }
            }
        }

        for (int i = 0; i < flagged.Length; i++)
        {
            if (!flagged[i]) continue;

            int index = i * 3;
            float median = Median(encoded[index], encoded[index + 1], encoded[index + 2]);

            encoded[index + 0] = median;
            encoded[index + 1] = median;
            encoded[index + 2] = median;
        }

        return count;
    }

    // Marks the texels immediately surrounding a true corner as protected from the range
    // test's "outside bounds" check.
    // Ported from msdfgen's MSDFErrorCorrection::protectCorners.
    static bool[] ProtectCorners(SDFAtlasShape shape, int width, int height)
    {
        var protectedTexels = new bool[width * height];
        if (shape == null) return protectedTexels;

        foreach (SDFAtlasShape.Contour contour in shape.contours)
        {
            int edgeCount = contour.edges.Count;
            if (edgeCount == 0) continue;

            SDFAtlasShape.EdgeColour previousColour = contour.edges[edgeCount - 1].colour;

            for (int i = 0; i < edgeCount; i++)
            {
                SDFAtlasShape.Edge edge = contour.edges[i];
                int commonColour = (int)previousColour & (int)edge.colour;

                // A single set bit (a power of two) means exactly one channel is shared:
                // the colour changed here, so it's a corner corner. Zero shared bits or more
                // than one both mean it isn't.
                bool isCorner = commonColour != 0 && (commonColour & (commonColour - 1)) == 0;

                if (isCorner) ProtectAround(protectedTexels, width, height, edge.Start);

                previousColour = edge.colour;
            }
        }

        return protectedTexels;
    }

    // Flags the four texels enveloping a corner point, matching msdfgen's texel-centre
    // convention (a texel at integer coordinates (x, y) is centred at (x + 0.5, y + 0.5)).
    static void ProtectAround(bool[] protectedTexels, int width, int height, Vector2 point)
    {
        int left = Mathf.FloorToInt(point.x - 0.5f);
        int bottom = Mathf.FloorToInt(point.y - 0.5f);
        int right = left + 1;
        int top = bottom + 1;

        if (left >= 0 && bottom >= 0 && left < width && bottom < height)
            protectedTexels[bottom * width + left] = true;
        if (right >= 0 && bottom >= 0 && right < width && bottom < height)
            protectedTexels[bottom * width + right] = true;
        if (left >= 0 && top >= 0 && left < width && top < height)
            protectedTexels[top * width + left] = true;
        if (right >= 0 && top >= 0 && right < width && top < height)
            protectedTexels[top * width + right] = true;
    }

    // Whether interpolating between two adjacent texels produces a median spike.
    //
    // Checks all three channel pairings, since the median switches winner at any of them.
    static bool HasLinearArtifact(float[] field, int aIndex, int bIndex, float aMedian, double span,
                                  bool aProtected)
    {
        float b0 = field[bIndex + 0];
        float b1 = field[bIndex + 1];
        float b2 = field[bIndex + 2];
        float bMedian = Median(b0, b1, b2);

        // Of the two texels, only the one further from the edge is corrected. Both would
        // otherwise flag each other, and collapsing the one nearer the edge does more damage
        // to the shape for the same artifact.
        if (Mathf.Abs(aMedian - 0.5f) < Mathf.Abs(bMedian - 0.5f)) return false;

        float a0 = field[aIndex + 0];
        float a1 = field[aIndex + 1];
        float a2 = field[aIndex + 2];

        return
            CrossingArtifact(field, aIndex, bIndex, aMedian, bMedian, a1 - a0, b1 - b0, span, aProtected) ||
            CrossingArtifact(field, aIndex, bIndex, aMedian, bMedian, a2 - a1, b2 - b1, span, aProtected) ||
            CrossingArtifact(field, aIndex, bIndex, aMedian, bMedian, a0 - a2, b0 - b2, span, aProtected);
    }

    // Tests the point where one specific pair of channels crosses.
    //
    // dA and dB are the difference between the same two channels at each texel. Where that
    // difference passes through zero, the two channels are equal -- and the median is at an
    // extreme, because a value that two of three channels agree on is necessarily the
    // median. Those are precisely the points worth checking.
    static bool CrossingArtifact(float[] field, int aIndex, int bIndex,
                                 float aMedian, float bMedian, float dA, float dB, double span,
                                 bool aProtected)
    {
        // Where mix(dA, dB, t) == 0.
        double denominator = dA - dB;
        if (denominator == 0.0) return false;

        double t = (double)dA / denominator;
        if (t <= ArtifactTEpsilon || t >= 1.0 - ArtifactTEpsilon) return false;

        float crossingMedian = InterpolatedMedian(field, aIndex, bIndex, t);

        return RangeTest(0.0, 1.0, t, aMedian, bMedian, crossingMedian, span, aProtected);
    }

    // Median of the linear interpolation between two texels at t.
    static float InterpolatedMedian(float[] field, int aIndex, int bIndex, double t)
    {
        float f = (float)t;
        return Median(
            Mathf.LerpUnclamped(field[aIndex + 0], field[bIndex + 0], f),
            Mathf.LerpUnclamped(field[aIndex + 1], field[bIndex + 1], f),
            Mathf.LerpUnclamped(field[aIndex + 2], field[bIndex + 2], f));
    }

    // Whether an interpolated median indicates an artifact rather than legitimate detail.
    //
    // Two conditions must both hold. First, the median must actually misbehave: either it
    // crosses the 0.5 edge threshold when neither endpoint does (an inversion -- a spurious
    // sliver of fill, or a hole punched in solid area), or -- for an unprotected texel only --
    // it simply falls outside the range its endpoints bracket. Second, the excursion must
    // exceed what the field's own rate of change can account for over that distance.
    //
    // The bounds half of the first condition is exactly what a real corner triggers: right at
    // a corner the interpolated median legitimately strays outside its endpoints' bracket,
    // because that is what a sharp corner looks like under linear interpolation. A texel
    // `ProtectCorners` has marked as adjoining a true corner is exempted from that half of the
    // test and can only be flagged by an outright sign inversion, which a real corner does not
    // produce. Unprotected texels still get both checks, so ordinary interpolation artifacts
    // away from corners are still caught.
    //
    // The second condition is what spares corners even when the first triggers legitimately
    // (as it does for a protected texel's inversion check, or for any texel's span check).
    // Near a corner the median does move sharply, but no faster than the distance field
    // legitimately changes across a texel, so it stays within span and is left alone.
    static bool RangeTest(double at, double bt, double xt,
                          float am, float bm, float xm, double span, bool protectedFlag)
    {
        bool inversion = (am > 0.5f && bm > 0.5f && xm <= 0.5f) ||
                         (am < 0.5f && bm < 0.5f && xm >= 0.5f);

        // Outside the bracket its endpoints establish: the median is not between them. Only
        // counts against an unprotected texel -- see the note above.
        bool outsideBounds = !protectedFlag && Median(am, bm, xm) != xm;

        if (!inversion && !outsideBounds) return false;

        double axSpan = (xt - at) * span;
        double bxSpan = (bt - xt) * span;

        bool withinExpected =
            xm >= am - axSpan && xm <= am + axSpan &&
            xm >= bm - bxSpan && xm <= bm + bxSpan;

        return !withinExpected;
    }
}
