using System.IO;
using UnityEngine;
using UnityEditor;

// Builds a reference image of a packed atlas, for use as a texture while authoring meshes
// or as a quick debugging reference of the packed output.
public static class SDFAtlasReference
{
    // Filename suffix, replacing the atlas texture's own extension.
    const string Suffix = ".reference.png";

    // --- Reconstruction ---------------------------------------------------

    // Renders a packed atlas texture to a coverage image.
    //
    // Reads the atlas back rather than taking the packer's float buffer, so this sees the
    // same 8-bit values a material samples. Quantisation is part of what the reference is
    // for: a field too shallow to survive 8 bits shows its banding here.
    public static Texture2D Build(Texture2D atlas, SDFAtlasInfo info)
    {
        Color32[] texels = atlas.GetPixels32();
        var coverage = new float[texels.Length];

        for (int i = 0; i < texels.Length; i++)
        {
            Color32 texel = texels[i];

            float distance = info.IsMultiChannel
                ? Median(texel.r / 255f, texel.g / 255f, texel.b / 255f)
                : texel.r / 255f;

            coverage[i] = Coverage(distance, info);
        }

        return ToTexture(coverage, atlas.width, atlas.height, info);
    }

    // Converts a stored distance into an antialiased 0..1 coverage value.
    //
    // Spread converts texels into stored units: half the 0..1 range covers `spread` texels,
    // so one texel is 0.5 / spread.
    static float Coverage(float distance, SDFAtlasInfo info)
    {
        float texel = 0.5f / Mathf.Max(info.spread, 1e-5f);
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.5f - texel, 0.5f + texel, distance));
    }

    static float Median(float r, float g, float b) =>
        Mathf.Max(Mathf.Min(r, g), Mathf.Min(Mathf.Max(r, g), b));

    // --- Padding overlay ---------------------------------------------------

    // Colour and opacity of the wash marking each cell's padding border.
    static readonly Color PaddingTint = Color.red;
    const float PaddingOpacity = 0.2f;

    // Whether a texel falls in the padding border of whichever cell it belongs to.
    //
    // Padding is a fixed count of texels on all four sides of every cell, so this is the
    // position within the cell rather than within the atlas.
    static bool IsPadding(int x, int y, SDFAtlasInfo info)
    {
        if (info.padding <= 0) return false;

        int localX = x % info.cellWidth;
        int localY = y % info.cellHeight;

        return localX < info.padding || localX >= info.cellWidth - info.padding ||
               localY < info.padding || localY >= info.cellHeight - info.padding;
    }

    // --- Texture output ---------------------------------------------------

    // Build the reference image: white artwork on transparent, with the padding borders
    // drawn as a red tinted border.
    static Texture2D ToTexture(float[] coverage, int width, int height, SDFAtlasInfo info)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false, linear: false);

        var pixels = new Color32[coverage.Length];
        for (int i = 0; i < coverage.Length; i++)
        {
            var pixel = new Color(1f, 1f, 1f, coverage[i]);

            if (IsPadding(i % width, i / width, info))
                pixel = Over(PaddingTint, PaddingOpacity, pixel);

            pixels[i] = new Color32(ToByte(pixel.r), ToByte(pixel.g), ToByte(pixel.b), ToByte(pixel.a));
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    // Composites a translucent colour over another, as an image editor's layer would.
    static Color Over(Color source, float sourceAlpha, Color dest)
    {
        float alpha = sourceAlpha + dest.a * (1f - sourceAlpha);
        if (alpha <= 0f) return new Color(source.r, source.g, source.b, 0f);

        float sourceWeight = sourceAlpha / alpha;
        float destWeight = dest.a * (1f - sourceAlpha) / alpha;

        return new Color(
            source.r * sourceWeight + dest.r * destWeight,
            source.g * sourceWeight + dest.g * destWeight,
            source.b * sourceWeight + dest.b * destWeight,
            alpha);
    }

    static byte ToByte(float value) =>
        (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);

    // --- Asset writing -----------------------------------------------------

    // Derives the reference image's path from an atlas texture asset path.
    public static string ReferencePath(string atlasAssetPath) =>
        Path.ChangeExtension(atlasAssetPath, null) + Suffix;

    // Writes the reference image beside its atlas and returns the asset path.
    //
    // Imported with no compression and no mips: this is an authoring reference read by an
    // external tool, so it is never sampled by a shader and never wants block compression
    // mangling the edges that make it legible.
    public static string Write(Texture2D reference, string atlasAssetPath)
    {
        string path = ReferencePath(atlasAssetPath);

        File.WriteAllBytes(path, reference.EncodeToPNG());
        AssetDatabase.ImportAsset(path);

        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false;
            importer.maxTextureSize = Mathf.Max(reference.width, reference.height);
            importer.SaveAndReimport();
        }

        return path;
    }
}
