using System.Collections.Generic;
using UnityEngine;

// Scanline rasteriser for SDFAtlasShape.
//
// This exists to validate SVG parsing, not to produce final art. If a parsed shape
// rasterises to the same silhouette as the artwork it came from, then the contours, their
// winding, and the fill rule were all read correctly, and that can be confirmed before
// any distance-field code is involved.
//
// Deliberately independent of the distance generator: a validation tool that shared code
// with the thing it validates would agree with it even when both were wrong.
public static class SDFAtlasShapeRasteriser
{
    // Samples per axis inside each pixel. 4 gives 16 coverage levels, which is enough to
    // judge an outline by eye without making a large render slow.
    const int DefaultSubsamples = 4;

    // Renders a shape to a coverage buffer (0..1 per pixel, row-major, bottom-up).
    public static float[] Rasterise(SDFAtlasShape shape, int width, int height,
                                    int subsamples = DefaultSubsamples)
    {
        var coverage = new float[width * height];
        if (shape == null || shape.EdgeCount == 0) return coverage;

        // Flatten every edge to line segments once, up front. Scanline crossing tests only
        // need straight edges, and flattening per scanline would repeat the work hundreds
        // of times over.
        List<Segment> segments = Flatten(shape);
        if (segments.Count == 0) return coverage;

        float step = 1f / subsamples;
        float sampleWeight = 1f / (subsamples * subsamples);

        // Crossing buffer, reused across scanlines to avoid per-line allocation.
        var crossings = new List<Crossing>(64);

        for (int y = 0; y < height; y++)
        {
            for (int sy = 0; sy < subsamples; sy++)
            {
                float sampleY = y + (sy + 0.5f) * step;

                crossings.Clear();
                CollectCrossings(segments, sampleY, crossings);
                if (crossings.Count == 0) continue;

                crossings.Sort((a, b) => a.x.CompareTo(b.x));

                for (int x = 0; x < width; x++)
                {
                    int row = y * width + x;
                    for (int sx = 0; sx < subsamples; sx++)
                    {
                        float sampleX = x + (sx + 0.5f) * step;
                        if (IsInside(crossings, sampleX, shape.fillRule))
                            coverage[row] += sampleWeight;
                    }
                }
            }
        }

        for (int i = 0; i < coverage.Length; i++)
            coverage[i] = Mathf.Clamp01(coverage[i]);

        return coverage;
    }

    // --- Inside test -------------------------------------------------------

    // Decides whether a point is inside, given the sorted crossings of its scanline.
    //
    // Nonzero sums the winding directions of every edge crossed to the left; even-odd just
    // counts them. The distinction is exactly what makes a hole a hole: two nested contours
    // wound oppositely cancel under nonzero, while under even-odd nesting alone is enough.
    static bool IsInside(List<Crossing> crossings, float x, SDFAtlasShape.FillRule rule)
    {
        if (rule == SDFAtlasShape.FillRule.EvenOdd)
        {
            int count = 0;
            for (int i = 0; i < crossings.Count; i++)
            {
                if (crossings[i].x > x) break;
                count++;
            }
            return (count & 1) != 0;
        }

        int winding = 0;
        for (int i = 0; i < crossings.Count; i++)
        {
            if (crossings[i].x > x) break;
            winding += crossings[i].direction;
        }
        return winding != 0;
    }

    // --- Scanline crossings -------------------------------------------------

    struct Crossing
    {
        public float x;
        public int direction;   // +1 for an upward edge, -1 for downward
    }

    struct Segment
    {
        public Vector2 a;
        public Vector2 b;
    }

    // Finds where a horizontal line at sampleY crosses each segment.
    //
    // The half-open rule (include the lower endpoint, exclude the upper) is what keeps a
    // vertex shared by two segments from being counted twice, which would otherwise punch
    // spurious holes along any horizontal line passing exactly through a vertex.
    static void CollectCrossings(List<Segment> segments, float sampleY, List<Crossing> crossings)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            Vector2 a = segments[i].a;
            Vector2 b = segments[i].b;

            int direction;
            float y0, y1, x0, x1;

            if (a.y < b.y)
            {
                direction = 1;
                y0 = a.y; y1 = b.y; x0 = a.x; x1 = b.x;
            }
            else if (a.y > b.y)
            {
                direction = -1;
                y0 = b.y; y1 = a.y; x0 = b.x; x1 = a.x;
            }
            else
            {
                continue;   // horizontal segments never cross a horizontal scanline
            }

            if (sampleY < y0 || sampleY >= y1) continue;

            float t = (sampleY - y0) / (y1 - y0);
            crossings.Add(new Crossing { x = Mathf.LerpUnclamped(x0, x1, t), direction = direction });
        }
    }

    // --- Flattening ---------------------------------------------------------

    // Converts every curve into line segments fine enough that the error is invisible.
    //
    // Subdivision count scales with the control polygon's length, so a long sweeping curve
    // gets more segments than a short one and both end up with similar chord error.
    static List<Segment> Flatten(SDFAtlasShape shape)
    {
        var segments = new List<Segment>(shape.EdgeCount * 8);

        foreach (SDFAtlasShape.Contour contour in shape.contours)
        {
            foreach (SDFAtlasShape.Edge edge in contour.edges)
            {
                if (edge.degree == 1)
                {
                    segments.Add(new Segment { a = edge.p0, b = edge.p1 });
                    continue;
                }

                float polygonLength =
                    (edge.p1 - edge.p0).magnitude +
                    (edge.p2 - edge.p1).magnitude +
                    (edge.degree >= 3 ? (edge.p3 - edge.p2).magnitude : 0f);

                // Roughly one segment per third of a pixel of control polygon, clamped so a
                // huge curve cannot explode the segment count.
                int steps = Mathf.Clamp(Mathf.CeilToInt(polygonLength * 3f), 4, 256);

                Vector2 previous = edge.Point(0f);
                for (int i = 1; i <= steps; i++)
                {
                    Vector2 point = edge.Point((float)i / steps);
                    segments.Add(new Segment { a = previous, b = point });
                    previous = point;
                }
            }
        }
        return segments;
    }

    // --- Framing ------------------------------------------------------------

    // How a document is fitted into its cell.
    public enum FramingMode
    {
        // Scale both axes by the same factor, so the artwork keeps its authored proportions
        // and the slack on the axis that did not drive the scale becomes empty margin.
        // The stored field stays isotropic: one texel of distance means the same real
        // distance on both axes.
        PreserveAspect,

        // Scale each axis independently so the artwork fills the whole cell. Non-square
        // artwork stops wasting texels on margin, at the cost of an anisotropic field --
        // see the note in FitDocumentToBox on what that costs.
        Stretch,
    }

    // Scales and translates a shape so its document fills a pixel box, leaving a margin.
    //
    // documentSize comes from the SVG's viewBox (see SDFAtlasSvgLoader). Falls back to the
    // shape's own bounds only when the document has no usable size.
    //
    // Returns the scale applied to each axis. Under PreserveAspect both components are
    // equal; under Stretch they differ, and the caller needs both to reason about what the
    // stored spread is worth per axis (see AnisotropyRatio).
    //
    // On Stretch and anisotropy: the distance solver runs after this transform, on the
    // already-stretched geometry, so it measures true distances in the stretched space. The
    // field is correct there, and the quad's own aspect un-squashes it at render time. What
    // it is not is isotropic: one stored unit is worth more real distance along the axis
    // that was compressed less. The consequences are that the shader's antialiasing band is
    // slightly wider along one axis, and that _EdgeBias dilates unevenly. Both are
    // proportional to the stretch ratio and negligible until that ratio gets large.
    public static Vector2 FitDocumentToBox(SDFAtlasShape shape, Vector2 documentSize,
                                           int width, int height, float marginPixels,
                                           FramingMode mode = FramingMode.PreserveAspect)
    {
        Vector2 min, max;

        if (documentSize.x > 0f && documentSize.y > 0f)
        {
            // The loader has already flipped Y about the document height, so the document
            // occupies (0,0)..(documentSize) in shape space regardless of the SVG's origin.
            min = Vector2.zero;
            max = documentSize;
        }
        else if (!shape.Bounds(out min, out max))
        {
            return Vector2.one;
        }

        Vector2 size = max - min;
        if (size.x <= 0f || size.y <= 0f) return Vector2.one;

        float usableWidth = Mathf.Max(width - 2f * marginPixels, 1f);
        float usableHeight = Mathf.Max(height - 2f * marginPixels, 1f);

        Vector2 scale;
        if (mode == FramingMode.Stretch)
        {
            // Each axis fills its own extent. No axis "drives" the scale, so the only slack
            // left is the margin itself, which the centring below distributes evenly.
            scale = new Vector2(usableWidth / size.x, usableHeight / size.y);
        }
        else
        {
            float uniform = Mathf.Min(usableWidth / size.x, usableHeight / size.y);
            scale = new Vector2(uniform, uniform);
        }

        // Centre whatever slack remains. Under Stretch that is exactly the margin on both
        // axes; under PreserveAspect it is the margin plus the letterboxing on one axis.
        Vector2 scaledSize = Vector2.Scale(size, scale);
        var offset = new Vector2(
            (width - scaledSize.x) * 0.5f - min.x * scale.x,
            (height - scaledSize.y) * 0.5f - min.y * scale.y);

        shape.Transform(scale, offset);
        return scale;
    }

    // How far from isotropic a framing scale is, as a ratio of at least 1.
    //
    // 1 means square texels and a field where one stored unit means the same real distance
    // in every direction. Larger means the field was compressed harder along one axis, which
    // is what divides the effective spread on that axis -- see the builder's spread warning.
    public static float AnisotropyRatio(Vector2 scale)
    {
        float x = Mathf.Abs(scale.x);
        float y = Mathf.Abs(scale.y);
        if (x <= 0f || y <= 0f) return 1f;
        return Mathf.Max(x / y, y / x);
    }
}
