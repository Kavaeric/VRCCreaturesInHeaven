using UnityEngine;

// Signed distance field generation from a binary coverage mask.
//
// This is the mathematical core of the SDFAtlas encoder, kept free of any Unity asset
// or editor dependencies so it can be called from the encoder, the atlas packer, or a
// test harness without dragging the rest of the module along.
//
// The technique is Green 2007, with one substitution: rather than the brute-force
// nearest-neighbour search the paper describes, distances come from an exact Euclidean
// distance transform (Felzenszwalb & Huttenlocher 2012). Both produce the same answer;
// the EDT does it in O(n) per axis instead of O(n * searchArea), which matters because
// we supersample heavily (a 4096x4096 source is 16.7M pixels).
public static class SDFAtlasDistanceField
{
    // Sentinel for "no seed pixel in this column/row" during the distance transform.
    // Large enough to dominate any real squared distance, small enough not to overflow
    // when added to another value of the same magnitude.
    const float Infinity = 1e20f;

    // --- Public API -----------------------------------------------------

    // Computes a signed distance field from a binary mask.
    //
    // mask: true = inside the shape, false = outside. Row-major, width * height entries.
    // Returns signed distance in source pixels, positive inside the shape, negative
    // outside, zero on the boundary. The caller is responsible for remapping this to a
    // texture-storable range (see Encode).
    //
    // The signed field is the difference of two unsigned transforms: distance-to-outside
    // measured from inside, minus distance-to-inside measured from outside. Computing both
    // and subtracting avoids the half-pixel bias you get from a single transform, and gives
    // a field that is continuous across the boundary rather than stepping from 0 to 1.
    public static float[] Compute(bool[] mask, int width, int height)
    {
        // Distance from each pixel to the nearest pixel NOT in the shape.
        // Zero everywhere outside; grows as you move deeper into the interior.
        float[] inside = EuclideanDistanceTransform(mask, width, height, seedValue: false);

        // Distance from each pixel to the nearest pixel IN the shape.
        // Zero everywhere inside; grows as you move further out into the background.
        float[] outside = EuclideanDistanceTransform(mask, width, height, seedValue: true);

        float[] signed = new float[width * height];
        for (int i = 0; i < signed.Length; i++)
        {
            // Exactly one of these is non-zero for any given pixel, so the subtraction
            // selects the relevant one and applies the sign in a single step.
            signed[i] = inside[i] - outside[i];
        }
        return signed;
    }

    // Thresholds a coverage channel into a binary mask.
    //
    // coverage: 0..1 per pixel (alpha, for this module's sources. See the encoder).
    // threshold: coverage at or above this counts as inside. 0.5 is the natural choice
    // for antialiased sources, since that is where the antialiased edge sits.
    public static bool[] Threshold(float[] coverage, float threshold)
    {
        bool[] mask = new bool[coverage.Length];
        for (int i = 0; i < coverage.Length; i++)
            mask[i] = coverage[i] >= threshold;
        return mask;
    }

    // Remaps a signed distance field (in source pixels) to 0..1 for storage in a texture.
    //
    // spreadPixels is the distance, in source pixels, that maps to the top and bottom of
    // the stored range. Distances beyond +/-spread clamp. The shape's edge (distance 0)
    // always lands at exactly 0.5, which is what the shader thresholds against.
    //
    // Spread is the key quality knob: too small and the field clips before the shader has
    // enough gradient to antialias against; too large and the 8-bit quantisation steps
    // become visible as banding along the edge.
    public static float[] Encode(float[] signedDistance, float spreadPixels)
    {
        float[] encoded = new float[signedDistance.Length];
        float invRange = 0.5f / Mathf.Max(spreadPixels, 1e-6f);
        for (int i = 0; i < signedDistance.Length; i++)
            encoded[i] = Mathf.Clamp01(0.5f + signedDistance[i] * invRange);
        return encoded;
    }

    // --- Distance transform ---------------------------------------------

    // Exact Euclidean distance transform over a binary image.
    //
    // Returns, for every pixel, the Euclidean distance (in pixels) to the nearest pixel
    // whose mask value equals seedValue. Pixels that are themselves seeds get 0.
    //
    // Works by running a 1D squared-distance transform along each row, then along each
    // column of that intermediate result. The 2D squared Euclidean distance is separable
    // ((dx^2 + dy^2) decomposes cleanly), which is what makes the two-pass approach exact
    // rather than an approximation like the chamfer/8SSEDT methods.
    static float[] EuclideanDistanceTransform(bool[] mask, int width, int height, bool seedValue)
    {
        float[] grid = new float[width * height];

        // Seed pixels start at 0, everything else at infinity. The transform then floods
        // the finite values outward.
        for (int i = 0; i < grid.Length; i++)
            grid[i] = mask[i] == seedValue ? 0f : Infinity;

        // Scratch buffers sized for the longest axis, reused across all rows and columns
        // to keep this from allocating per-line on large sources.
        int longest = Mathf.Max(width, height);
        float[] f = new float[longest];      // input function for the current line
        float[] d = new float[longest];      // output distances for the current line
        int[] v = new int[longest];          // indices of parabolas in the lower envelope
        float[] z = new float[longest + 1];  // boundaries between those parabolas

        // Pass 1: transform each row independently.
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width;
            for (int x = 0; x < width; x++) f[x] = grid[rowStart + x];
            Transform1D(f, d, v, z, width);
            for (int x = 0; x < width; x++) grid[rowStart + x] = d[x];
        }

        // Pass 2: transform each column of the row-transformed result.
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++) f[y] = grid[y * width + x];
            Transform1D(f, d, v, z, height);
            for (int y = 0; y < height; y++) grid[y * width + x] = d[y];
        }

        // The transform works in squared distance to stay in integer-friendly arithmetic
        // and avoid a square root per comparison. Take the root once, at the end.
        for (int i = 0; i < grid.Length; i++)
            grid[i] = Mathf.Sqrt(grid[i]);

        return grid;
    }

    // 1D squared distance transform of a sampled function, after Felzenszwalb & Huttenlocher.
    //
    // Computes d[q] = min over p of ( (q - p)^2 + f[p] ).
    //
    // Geometrically, each sample p defines an upward parabola rooted at (p, f[p]); the
    // result is the lower envelope of all of them. The algorithm walks left to right
    // maintaining that envelope as a stack of parabolas (v) and the x-positions where
    // consecutive parabolas intersect (z), then walks it again sampling the winner at each
    // q. Both walks are linear, so the whole thing is O(n).
    //
    // Buffers are passed in rather than allocated so callers can reuse them across lines.
    static void Transform1D(float[] f, float[] d, int[] v, float[] z, int n)
    {
        int k = 0;          // index of the rightmost parabola currently in the envelope
        v[0] = 0;
        z[0] = -Infinity;   // envelope extends infinitely left of the first parabola
        z[1] = +Infinity;   // ...and infinitely right, until another parabola is added

        // Build the lower envelope.
        for (int q = 1; q < n; q++)
        {
            // Intersection of the parabola at q with the one currently on top of the stack.
            // Derived by setting the two parabola equations equal and solving for x.
            float s = Intersection(f, v[k], q);

            // If that intersection lies left of where the top parabola took over, the top
            // parabola is entirely hidden beneath the new one. Pop it and retry against
            // whatever is underneath. Each parabola is pushed and popped at most once,
            // which is what keeps the loop linear despite being nested.
            while (k > 0 && s <= z[k])
            {
                k--;
                s = Intersection(f, v[k], q);
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = +Infinity;
        }

        // Walk the envelope left to right, reading off the winning parabola at each sample.
        k = 0;
        for (int q = 0; q < n; q++)
        {
            // Advance past any parabolas whose region ends before q.
            while (z[k + 1] < q) k++;

            int p = v[k];
            float dist = q - p;
            d[q] = dist * dist + f[p];
        }
    }

    // x-coordinate where the parabolas rooted at p and q intersect.
    //
    // Both are unit-curvature upward parabolas, so ((q^2 + f[q]) - (p^2 + f[p])) / (2q - 2p).
    // When f[p] and f[q] are both Infinity the numerator is a difference of equal infinities;
    // the guard keeps that from producing NaN and corrupting the envelope.
    static float Intersection(float[] f, int p, int q)
    {
        float denominator = 2f * q - 2f * p;
        if (denominator == 0f) return Infinity;

        float fp = f[p];
        float fq = f[q];

        // Both parabolas are at infinity: neither can ever win, so push the intersection
        // out of range rather than computing Infinity - Infinity.
        if (fp >= Infinity && fq >= Infinity) return Infinity;

        return ((q * (float)q + fq) - (p * (float)p + fp)) / denominator;
    }
}
