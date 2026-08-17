using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

// Packs multi-channel distance fields into a uniform-grid atlas texture. The single-channel
// counterpart is SDFAtlasPacker.
//
// Cell indices are stable for the same reason as in the single-channel packer: the index is
// baked into mesh UVs at authoring time, so a packer that renumbered cells on rebuild would
// silently break every quad already placed.
public static class MSDFAtlasPacker
{
    // One graphic queued for packing, and the cell it belongs in.
    public struct Entry
    {
        public string svgPath;      // Asset path of the source SVG
        public int cellIndex;
        public string name;
    }

    // Settings that control how curves become distance values.
    public struct Settings
    {
        public double angleThreshold;   // Radians; joins sharper than this are corners
        public ulong seed;              // Varies channel assignment
        public bool errorCorrection;

        public static Settings Default => new Settings
        {
            angleThreshold = MSDFAtlasEdgeColouring.DefaultAngleThreshold,
            seed = 0,
            errorCorrection = true,
        };
    }

    // --- Packing --------------------------------------------------------

    // Builds the atlas texture from a set of entries.
    //
    // Returns an RGB24 texture sized to the manifest's grid, and fills in the manifest's
    // cell table as a side effect. Cells with no entry are left fully outside, so an
    // unaddressed cell renders as nothing rather than as a block of colour.
    public static Texture2D Pack(IList<Entry> entries, SDFAtlasInfo info, Settings settings)
    {
        int width = info.TextureWidth;
        int height = info.TextureHeight;

        // Three floats per texel, interleaved, matching the generator's layout.
        var atlas = new float[width * height * 3];

        // Start every channel fully outside. Stored 0 is the deepest-outside value, so the
        // median of three zeroes is also fully outside -- an empty cell reads as empty.
        for (int i = 0; i < atlas.Length; i++) atlas[i] = 0f;

        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            if (string.IsNullOrEmpty(entry.svgPath)) continue;

            if (entry.cellIndex < 0 || entry.cellIndex >= info.CellCount)
            {
                Debug.LogWarning(
                    $"[SDFAtlas] '{entry.name}' has out-of-range cell index {entry.cellIndex}; skipped.");
                continue;
            }

            float[] cell = EncodeCell(entry.svgPath, info, settings);
            if (cell == null) continue;

            info.IndexToCoord(entry.cellIndex, out int cellX, out int cellY);
            BlitCell(atlas, width, height, cell, info, cellX, cellY);

            info.cells[entry.cellIndex] = new SDFAtlasInfo.CellEntry
            {
                occupied = true,
                sourceGuid = AssetDatabase.AssetPathToGUID(entry.svgPath),
                name = string.IsNullOrEmpty(entry.name)
                    ? Path.GetFileNameWithoutExtension(entry.svgPath)
                    : entry.name,
            };
        }

        return ToTexture(atlas, width, height);
    }

    // Encodes one SVG into a full cellSize x cellSize x 3 block.
    //
    // Padding is handled by framing the shape into the artwork area with the padding as
    // margin, rather than by compositing into an oversized canvas as the raster path does.
    // Working from curves means the margin can simply be part of the framing transform --
    // there is no source grid to preserve, so no resampling is involved.
    static float[] EncodeCell(string svgPath, SDFAtlasInfo info, Settings settings)
    {
        SDFAtlasShape shape = SDFAtlasSvgLoader.Load(svgPath, out Vector2 documentSize);
        if (shape == null)
        {
            Debug.LogWarning($"[SDFAtlas] Could not load '{svgPath}'; cell left empty.");
            return null;
        }

        // Frame the *document*, not the artwork's bounding box. Cropping to the artwork
        // would strip any clear space the artboard deliberately included and rescale every
        // graphic to fill its cell independently, so icons authored to a shared cap height
        // would come out at different sizes. Framing the document also matches the raster
        // pipeline, which composites its source at full pixel dimensions.
        //
        // The padding is the margin, and the distance field continues naturally into it
        // because it is measured from curves rather than filled in afterwards.
        SDFAtlasShapeRasteriser.FitDocumentToBox(
            shape, documentSize, info.cellSize, info.cellSize, info.padding);

        MSDFAtlasEdgeColouring.Apply(shape, settings.angleThreshold, settings.seed);

        float[] raw = MSDFAtlasField.Generate(shape, info.cellSize, info.cellSize);
        float[] encoded = MSDFAtlasField.Encode(raw, info.spread);

        if (settings.errorCorrection)
            MSDFAtlasField.CorrectErrors(shape, encoded, info.cellSize, info.cellSize, info.spread);

        return encoded;
    }

    // Copies an encoded cell into its grid position.
    //
    // Cell Y and the texture buffer both run bottom-up (see SDFAtlasInfo's addressing
    // notes), so no row flip is needed.
    static void BlitCell(float[] atlas, int atlasWidth, int atlasHeight,
                         float[] cell, SDFAtlasInfo info, int cellX, int cellY)
    {
        int originX = cellX * info.cellSize;
        int originY = cellY * info.cellSize;

        for (int y = 0; y < info.cellSize; y++)
        {
            int destY = originY + y;
            if (destY < 0 || destY >= atlasHeight) continue;

            for (int x = 0; x < info.cellSize; x++)
            {
                int destX = originX + x;
                if (destX < 0 || destX >= atlasWidth) continue;

                int destIndex = (destY * atlasWidth + destX) * 3;
                int srcIndex = (y * info.cellSize + x) * 3;

                atlas[destIndex + 0] = cell[srcIndex + 0];
                atlas[destIndex + 1] = cell[srcIndex + 1];
                atlas[destIndex + 2] = cell[srcIndex + 2];
            }
        }
    }

    // --- Texture output ---------------------------------------------------

    // Converts the packed float buffer into an RGB24 texture.
    //
    // Linear, not sRGB: these are distances, and gamma-encoding them would warp the field
    // and shift every edge. This matters more for MSDF than for single-channel, because the
    // median of three gamma-warped values is not the gamma-warped median -- the corner
    // reconstruction would be wrong, not merely offset.
    static Texture2D ToTexture(float[] atlas, int width, int height)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGB24, mipChain: false, linear: true);

        var pixels = new Color32[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            int index = i * 3;
            pixels[i] = new Color32(
                ToByte(atlas[index + 0]),
                ToByte(atlas[index + 1]),
                ToByte(atlas[index + 2]),
                255);
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    static byte ToByte(float value) =>
        (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);

    // --- Asset writing -----------------------------------------------------

    // Writes the atlas texture and its manifest, then applies import settings.
    public static string WriteAtlas(Texture2D atlas, SDFAtlasInfo info, string assetPath)
    {
        string dir = Path.GetDirectoryName(assetPath).Replace('\\', '/');
        if (!AssetDatabase.IsValidFolder(dir))
            CreateFolders(dir);

        File.WriteAllBytes(assetPath, atlas.EncodeToPNG());
        AssetDatabase.ImportAsset(assetPath);

        ApplyImportSettings(assetPath, info);
        info.Save(assetPath);

        return assetPath;
    }

    // Configures the importer for multi-channel distance data.
    //
    // Compression is the setting that matters most here and is the easiest to get wrong.
    // Block compression works by approximating a block's colours along a line in RGB space,
    // which is exactly wrong for MSDF: the three channels are independent distance fields,
    // not correlated colour, so DXT smears them into each other and destroys the channel
    // disagreement that encodes corners. Uncompressed, always.
    static void ApplyImportSettings(string assetPath, SDFAtlasInfo info)
    {
        var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer == null) return;

        importer.textureType = TextureImporterType.Default;
        importer.sRGBTexture = false;
        importer.alphaSource = TextureImporterAlphaSource.None;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.filterMode = FilterMode.Bilinear;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.mipmapEnabled = true;
        importer.maxTextureSize = Mathf.Max(info.TextureWidth, info.TextureHeight);

        // Same reasoning as the single-channel atlas: mips average across cell boundaries at
        // deep levels, which is a padding question rather than a reason to hand-build the
        // chain. SDFAtlasInfo.SafeMipLevel reports which levels stay clean.
        importer.mipMapsPreserveCoverage = false;

        importer.SaveAndReimport();
    }

    static void CreateFolders(string path)
    {
        string[] parts = path.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = $"{current}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }
}
