using UnityEngine;

// Distance field generation from a vector shape.
//
// The difference from the raster path is what "nearest" means. The distance transform
// measures to the nearest lit texel, so its precision is bounded by the source resolution.
// This measures to the curve itself, so a 64-texel field carries edge positions accurate to
// far finer than a texel.
public static class SDFAtlasVectorField
{
    // --- Single-channel generation ----------------------------------------

    // Generates a signed distance field over a pixel grid.
    //
    // The shape must already be framed into the target pixel space (see
    // SDFAtlasShapeRasteriser.FitDocumentToBox). Returns raw signed distances in pixels, positive
    // inside, matching the sign convention of the raster path's Compute().
    public static float[] Generate(SDFAtlasShape shape, int width, int height)
    {
        var field = new float[width * height];
        if (shape == null || shape.EdgeCount == 0) return field;

        // Winding sign per contour, computed once. Under the nonzero rule a clockwise
        // contour inside a counter-clockwise one is a hole, and that is decided by area
        // sign rather than by anything the distance solver sees.
        int contourCount = shape.contours.Count;
        var windings = new int[contourCount];
        for (int i = 0; i < contourCount; i++)
            windings[i] = shape.contours[i].SignedArea() >= 0f ? 1 : -1;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Sample at texel centres. Getting this half-pixel offset wrong shifts the
                // whole field by half a texel, which reads as the artwork sitting slightly
                // off-centre in its cell.
                var point = new Vector2(x + 0.5f, y + 0.5f);
                field[y * width + x] = SignedDistanceAt(shape, windings, point);
            }
        }
        return field;
    }

    // Signed distance from a point to the whole shape.
    //
    // Takes the closest edge across all contours, using the pseudo-distance extension so
    // that the field stays smooth past endpoints rather than creasing at every join.
    public static float SignedDistanceAt(SDFAtlasShape shape, int[] windings, Vector2 point)
    {
        var closest = SDFAtlasEdgeDistance.SignedDistance.Infinite;
        double closestParam = 0.0;
        SDFAtlasShape.Edge closestEdge = default;
        bool found = false;

        for (int c = 0; c < shape.contours.Count; c++)
        {
            SDFAtlasShape.Contour contour = shape.contours[c];

            for (int e = 0; e < contour.edges.Count; e++)
            {
                SDFAtlasShape.Edge edge = contour.edges[e];
                var distance = SDFAtlasEdgeDistance.Distance(edge, point, out double param);

                if (!found || distance.IsCloserThan(closest))
                {
                    closest = distance;
                    closestParam = param;
                    closestEdge = edge;
                    found = true;
                }
            }
        }

        if (!found) return 0f;

        // Extend past the endpoint if that is where the closest point fell. Without this,
        // the field creases at every joint between edges, which shows up as faint seams
        // radiating from the outline.
        SDFAtlasEdgeDistance.ToPseudoDistance(ref closest, closestEdge, point, closestParam);

        return (float)closest.distance;
    }

    // --- Winding-corrected variant -----------------------------------------

    // Generates a field whose sign comes from a winding test rather than from the closest
    // edge's orientation.
    //
    // The closest-edge sign is correct for a simple shape, but not for one where contours
    // overlap or nest. e.g. a letter counter, for instance, is inside its own contour but
    // outside the glyph. Resolving the sign by winding matches how the shape actually fills,
    // and matches what the rasteriser shows.
    //
    // Magnitude still comes from the distance solver; only the sign is replaced.
    public static float[] GenerateFilled(SDFAtlasShape shape, int width, int height)
    {
        float[] field = Generate(shape, width, height);
        if (shape == null || shape.EdgeCount == 0) return field;

        // Reuse the rasteriser's fill test, at one sample per texel centre. It already
        // implements both fill rules correctly and was validated in stage 1, so deriving the
        // sign from it keeps the two in agreement by construction.
        float[] coverage = SDFAtlasShapeRasteriser.Rasterise(shape, width, height, subsamples: 1);

        for (int i = 0; i < field.Length; i++)
        {
            bool inside = coverage[i] >= 0.5f;
            float magnitude = Mathf.Abs(field[i]);
            field[i] = inside ? magnitude : -magnitude;
        }
        return field;
    }

    // --- Encoding ----------------------------------------------------------

    // Remaps a signed distance field to 0..1 for storage, matching the raster path's
    // encoding exactly so the two are directly comparable.
    public static float[] Encode(float[] signedDistance, float spreadPixels)
    {
        var encoded = new float[signedDistance.Length];
        float invRange = 0.5f / Mathf.Max(spreadPixels, 1e-6f);

        for (int i = 0; i < signedDistance.Length; i++)
            encoded[i] = Mathf.Clamp01(0.5f + signedDistance[i] * invRange);

        return encoded;
    }
}
