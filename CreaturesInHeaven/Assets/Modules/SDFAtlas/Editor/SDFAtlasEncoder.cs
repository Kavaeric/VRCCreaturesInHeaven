using System.IO;
using UnityEngine;
using UnityEditor;

// Turns a source texture into a cell-sized signed distance field.
//
// The pipeline is: read coverage -> threshold to a binary mask -> exact signed distance
// transform at source resolution -> downsample to cell resolution -> remap to 0..1.
//
// Doing the distance transform at full source resolution and only then downsampling is
// deliberate and is where the quality comes from. The alternative (downsample first, then
// transform) throws away the sub-pixel edge position before it has been measured, which
// is precisely the information an SDF exists to preserve. A 4096 source encoded to a 64
// cell is effectively 64x supersampling of the edge location.
public static class SDFAtlasEncoder
{
    // Which channel of the source carries shape coverage. Alpha is the default.
    public enum CoverageChannel
    {
        Alpha,
        Luminance,
        InvertedLuminance,
    }

    // How the source's coverage values are turned into a distance field.
    public enum EdgeMode
    {
        // Threshold to a binary mask, then measure distances to it. The source's
        // antialiased edge is rounded to the nearest pixel centre before anything is
        // measured, so the field describes a staircase approximation of the real edge.
        // Kept as a baseline for comparison; SubTexel supersedes it for real work.
        Binary,

        // Use the antialiased coverage to place the edge at its true sub-pixel position.
        // Recovers edge precision that Binary discards, which otherwise has to be bought
        // back with source resolution.
        SubTexel,
    }

    // Everything needed to encode one graphic. Grouped so the settings can be passed
    // around and later serialised into the atlas manifest without a long argument list.
    public struct Settings
    {
        public int cellWidth;                 // Output width, in texels (e.g. 64)
        public int cellHeight;                // Output height, in texels
        public float spreadPixels;            // Distance range mapped to 0..1, in *cell* texels
        public float coverageThreshold;       // Coverage at/above this is inside. 0.5 for antialiased art
        public CoverageChannel channel;
        public EdgeMode edgeMode;

        // Sensible starting point, matching the module's agreed 64x64 cell target.
        // A 4-texel spread at cell resolution gives the shader enough gradient to
        // antialias against without pushing 8-bit quantisation into visible banding.
        public static Settings Default => new Settings
        {
            cellWidth = 64,
            cellHeight = 64,
            spreadPixels = 4f,
            coverageThreshold = 0.5f,
            channel = CoverageChannel.Alpha,
            edgeMode = EdgeMode.SubTexel,
        };
    }

    // --- Encoding -------------------------------------------------------

    // Encodes a source texture to a cell-sized SDF, returned as 0..1 values (row-major,
    // cellWidth * cellHeight entries). Ready to be written into an atlas cell.
    public static float[] Encode(Texture2D source, Settings settings)
    {
        float[] coverage = ReadCoverage(source, settings.channel);
        return EncodeCoverage(coverage, source.width, source.height, settings);
    }

    // As Encode, but takes an already-extracted coverage buffer and lets the caller choose
    // the output dimensions. Split out so the contact sheet can render intermediate stages
    // without re-reading the texture, and preview non-square sources undistorted.
    //
    // dstWidth/dstHeight are usually the cell's own dimensions, but the validation harness
    // passes an aspect-preserving size so encoder quality can be judged without the squash
    // confusing the picture.
    public static float[] EncodeCoverage(float[] coverage, int width, int height, Settings settings,
                                         int dstWidth, int dstHeight)
    {
        // The distance transform runs in *source* space, where pixels are square and the
        // result is a true isotropic Euclidean distance. Squashing afterwards (if the
        // destination aspect differs) distorts the field along with the shape, which is
        // exactly what we want -- the quad's own aspect un-squashes both at render time.
        float[] distance;
        if (settings.edgeMode == EdgeMode.SubTexel)
        {
            distance = SDFAtlasDistanceField.ComputeAntialiased(
                coverage, width, height, settings.coverageThreshold);
        }
        else
        {
            bool[] mask = SDFAtlasDistanceField.Threshold(coverage, settings.coverageThreshold);
            distance = SDFAtlasDistanceField.Compute(mask, width, height);
        }

        // Downsample the *distance field*, not the mask. Distance is a smooth, slowly
        // varying function, so box-averaging it is well-behaved and preserves the
        // sub-pixel edge position encoded in the neighbouring values.
        float[] cellDistance = Downsample(distance, width, height, dstWidth, dstHeight);

        // Distances are still in source pixels; convert to destination texels so that
        // spreadPixels means the same thing regardless of source resolution.
        //
        // When the aspect changes, the two axes scale differently and no single factor is
        // correct. Use the geometric mean, which keeps the spread visually even rather than
        // biasing the antialiasing band toward one axis.
        float scaleX = (float)dstWidth / width;
        float scaleY = (float)dstHeight / height;
        float sourceToCellScale = Mathf.Sqrt(scaleX * scaleY);

        for (int i = 0; i < cellDistance.Length; i++)
            cellDistance[i] *= sourceToCellScale;

        return SDFAtlasDistanceField.Encode(cellDistance, settings.spreadPixels);
    }

    // Encodes into the settings' atlas cell. Sources whose aspect differs from the cell's are
    // squashed to fit; the quad's aspect restores them at render time.
    public static float[] EncodeCoverage(float[] coverage, int width, int height, Settings settings) =>
        EncodeCoverage(coverage, width, height, settings, settings.cellWidth, settings.cellHeight);

    // --- Source reading -------------------------------------------------

    // Extracts a 0..1 coverage value per pixel from the source texture.
    //
    // Requires the texture to be readable (Read/Write Enabled in the importer) and,
    // ideally, uncompressed -- block compression mangles exactly the edge detail this
    // whole system is built to preserve.
    public static float[] ReadCoverage(Texture2D source, CoverageChannel channel)
    {
        Color[] pixels = source.GetPixels();
        float[] coverage = new float[pixels.Length];

        for (int i = 0; i < pixels.Length; i++)
        {
            Color p = pixels[i];
            switch (channel)
            {
                case CoverageChannel.Alpha:
                    coverage[i] = p.a;
                    break;

                // Rec. 709 luma. Only meaningful for sources with no usable alpha.
                case CoverageChannel.Luminance:
                    coverage[i] = 0.2126f * p.r + 0.7152f * p.g + 0.0722f * p.b;
                    break;

                // For dark-artwork-on-light-background sources, where high luminance is
                // the *background* rather than the shape.
                case CoverageChannel.InvertedLuminance:
                    coverage[i] = 1f - (0.2126f * p.r + 0.7152f * p.g + 0.0722f * p.b);
                    break;
            }
        }
        return coverage;
    }

    // Reports whether a texture's alpha channel actually varies.
    //
    // Used by the contact sheet to warn when Alpha is selected on a source that is
    // uniformly opaque.
    public static bool HasVaryingAlpha(Texture2D source)
    {
        Color[] pixels = source.GetPixels();
        if (pixels.Length == 0) return false;

        float first = pixels[0].a;
        for (int i = 1; i < pixels.Length; i++)
            if (!Mathf.Approximately(pixels[i].a, first)) return true;

        return false;
    }

    // --- Resampling -----------------------------------------------------

    // Box-filter downsample of a float field.
    //
    // Each destination texel averages the full rectangle of source texels that maps to it,
    // so no source pixel is skipped regardless of the ratio. For non-integer ratios the
    // rectangle edges land between source texels; we accept the slight unevenness rather
    // than weighting partial coverage, as the error is far below the quantisation floor of
    // the 8-bit output.
    public static float[] Downsample(float[] source, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        float[] dst = new float[dstWidth * dstHeight];
        float xRatio = (float)srcWidth / dstWidth;
        float yRatio = (float)srcHeight / dstHeight;

        for (int y = 0; y < dstHeight; y++)
        {
            int y0 = Mathf.FloorToInt(y * yRatio);
            int y1 = Mathf.Min(Mathf.CeilToInt((y + 1) * yRatio), srcHeight);
            if (y1 <= y0) y1 = Mathf.Min(y0 + 1, srcHeight);

            for (int x = 0; x < dstWidth; x++)
            {
                int x0 = Mathf.FloorToInt(x * xRatio);
                int x1 = Mathf.Min(Mathf.CeilToInt((x + 1) * xRatio), srcWidth);
                if (x1 <= x0) x1 = Mathf.Min(x0 + 1, srcWidth);

                float sum = 0f;
                int count = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * srcWidth;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        sum += source[row + sx];
                        count++;
                    }
                }
                dst[y * dstWidth + x] = count > 0 ? sum / count : 0f;
            }
        }
        return dst;
    }

    // Nearest-neighbour upscale of a float field.
    //
    // Used by the contact sheet to show a small encoded cell at inspectable size without
    // the viewer's own filtering smoothing over the very artifacts we are looking for.
    public static float[] UpsampleNearest(float[] source, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        float[] dst = new float[dstWidth * dstHeight];
        for (int y = 0; y < dstHeight; y++)
        {
            int sy = Mathf.Min(y * srcHeight / dstHeight, srcHeight - 1);
            for (int x = 0; x < dstWidth; x++)
            {
                int sx = Mathf.Min(x * srcWidth / dstWidth, srcWidth - 1);
                dst[y * dstWidth + x] = source[sy * srcWidth + sx];
            }
        }
        return dst;
    }

    // Bilinear sample of a float field at normalised coordinates.
    //
    // This deliberately mirrors what the GPU will do when the shader samples the atlas:
    // the reconstruction previews are only honest if they filter the field the same way
    // the real thing does. Coordinates are clamped at the edges, matching clamp wrap mode.
    public static float SampleBilinear(float[] field, int width, int height, float u, float v)
    {
        // Convert normalised coords to texel space, offsetting by half a texel because
        // texel centres sit at 0.5 steps rather than on integer boundaries.
        float x = u * width - 0.5f;
        float y = v * height - 0.5f;

        int x0 = Mathf.FloorToInt(x);
        int y0 = Mathf.FloorToInt(y);
        float fx = x - x0;
        float fy = y - y0;

        int x1 = Mathf.Clamp(x0 + 1, 0, width - 1);
        int y1 = Mathf.Clamp(y0 + 1, 0, height - 1);
        x0 = Mathf.Clamp(x0, 0, width - 1);
        y0 = Mathf.Clamp(y0, 0, height - 1);

        float v00 = field[y0 * width + x0];
        float v10 = field[y0 * width + x1];
        float v01 = field[y1 * width + x0];
        float v11 = field[y1 * width + x1];

        return Mathf.Lerp(Mathf.Lerp(v00, v10, fx), Mathf.Lerp(v01, v11, fx), fy);
    }
}
