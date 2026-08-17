using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

// Packs encoded SDF graphics into a uniform-grid atlas texture.
//
// One source texture per cell; cells are addressed by index and the grid never reflows, so
// a graphic's cell assignment is stable for the life of the atlas. That stability is load-
// bearing: the cell index is baked into mesh UVs at authoring time, so a packer that
// renumbered cells on rebuild would silently break every quad already placed in the scene.
public static class SDFAtlasPacker
{
    // One graphic queued for packing, and the cell it belongs in.
    public struct Entry
    {
        public Texture2D source;
        public int cellIndex;
        public string name;
    }

    // --- Packing --------------------------------------------------------

    // Builds the atlas texture from a set of entries.
    //
    // Returns a single-channel (R8) texture sized to the manifest's grid, and fills in the
    // manifest's cell table as a side effect. Cells with no entry are left at the
    // fully-outside value so nothing appears there if a quad addresses them by mistake.
    public static Texture2D Pack(IList<Entry> entries, SDFAtlasInfo info,
                                 SDFAtlasEncoder.Settings encodeSettings)
    {
        int width = info.TextureWidth;
        int height = info.TextureHeight;

        // Start fully "outside". Stored 0 is the deepest-outside value, so an unaddressed or
        // empty cell renders as nothing rather than as a solid block.
        float[] atlas = new float[width * height];

        // Encoder settings are driven by the manifest so the atlas and its metadata cannot
        // disagree. The artwork occupies the inner area; the padding border is filled by the
        // distance transform continuing outward past it.
        var settings = encodeSettings;
        settings.cellSize = info.cellSize;
        settings.spreadPixels = info.spread;

        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            if (entry.source == null) continue;
            if (entry.cellIndex < 0 || entry.cellIndex >= info.CellCount)
            {
                Debug.LogWarning($"[SDFAtlas] '{entry.name}' has out-of-range cell index {entry.cellIndex}; skipped.");
                continue;
            }

            float[] cell = EncodeCell(entry.source, info, settings);

            info.IndexToCoord(entry.cellIndex, out int cellX, out int cellY);
            BlitCell(atlas, width, height, cell, info, cellX, cellY);

            info.cells[entry.cellIndex] = new SDFAtlasInfo.CellEntry
            {
                occupied = true,
                sourceGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(entry.source)),
                name = string.IsNullOrEmpty(entry.name) ? entry.source.name : entry.name,
            };
        }

        return ToTexture(atlas, width, height);
    }

    // Encodes one source into a full cellSize x cellSize block, with the artwork inset by
    // the manifest's padding.
    //
    // The inset is applied *before* the distance transform, by compositing the source into a
    // larger transparent canvas. That way the transform measures real distances from the
    // border texels to artwork that genuinely sits inset, so the padding contains a true
    // continuation of the field rather than a synthesised or clamped fill. Post-filling the
    // border would create a distance discontinuity at the padding boundary, which pinches
    // edges near the cell edge.
    static float[] EncodeCell(Texture2D source, SDFAtlasInfo info, SDFAtlasEncoder.Settings settings)
    {
        float[] coverage = SDFAtlasEncoder.ReadCoverage(source, settings.channel);

        // Scale factor from cell to artwork area. The source is composited into a canvas
        // proportionally larger than itself, so that after downsampling to cellSize the
        // artwork lands in the inner ArtworkSize square.
        float inset = (float)info.ArtworkSize / info.cellSize;

        int canvasWidth = Mathf.Max(1, Mathf.RoundToInt(source.width / inset));
        int canvasHeight = Mathf.Max(1, Mathf.RoundToInt(source.height / inset));

        float[] canvas = CompositeCentred(coverage, source.width, source.height, canvasWidth, canvasHeight);

        // Encode at full cell size. The transform runs across the whole canvas including the
        // border region, so border texels get correct distances to the inset artwork.
        return SDFAtlasEncoder.EncodeCoverage(canvas, canvasWidth, canvasHeight, settings,
                                              info.cellSize, info.cellSize);
    }

    // Composites a coverage buffer into the centre of a larger, empty (fully-outside) canvas.
    // Used to inset artwork inside its cell's padding.
    static float[] CompositeCentred(float[] source, int srcWidth, int srcHeight,
                                    int dstWidth, int dstHeight)
    {
        float[] dst = new float[dstWidth * dstHeight];

        int offsetX = (dstWidth - srcWidth) / 2;
        int offsetY = (dstHeight - srcHeight) / 2;

        for (int y = 0; y < srcHeight; y++)
        {
            int dstY = offsetY + y;
            if (dstY < 0 || dstY >= dstHeight) continue;

            int srcRow = y * srcWidth;
            int dstRow = dstY * dstWidth;

            for (int x = 0; x < srcWidth; x++)
            {
                int dstX = offsetX + x;
                if (dstX < 0 || dstX >= dstWidth) continue;
                dst[dstRow + dstX] = source[srcRow + x];
            }
        }
        return dst;
    }

    // Copies an encoded cell into its grid position in the atlas buffer.
    //
    // The cell block is written whole, padding included -- the padding is part of the cell's
    // encoded data, not a gap between cells.
    //
    // Cell Y and the texture buffer both run bottom-up (see SDFAtlasInfo's addressing notes),
    // so no row flip is needed here.
    static void BlitCell(float[] atlas, int atlasWidth, int atlasHeight,
                         float[] cell, SDFAtlasInfo info, int cellX, int cellY)
    {
        int originX = cellX * info.cellSize;
        int originY = cellY * info.cellSize;

        for (int y = 0; y < info.cellSize; y++)
        {
            int destY = originY + y;
            if (destY < 0 || destY >= atlasHeight) continue;

            int destRow = destY * atlasWidth;
            int srcRow = y * info.cellSize;

            for (int x = 0; x < info.cellSize; x++)
            {
                int destX = originX + x;
                if (destX < 0 || destX >= atlasWidth) continue;
                atlas[destRow + destX] = cell[srcRow + x];
            }
        }
    }

    // --- Texture output ---------------------------------------------------

    // Converts the packed float buffer into a single-channel texture.
    //
    // R8 is the natural format: one distance value per texel, no colour involved. Linear
    // (not sRGB) because these are distances, not perceptual colour -- gamma-encoding them
    // would warp the field and shift every edge.
    static Texture2D ToTexture(float[] atlas, int width, int height)
    {
        var texture = new Texture2D(width, height, TextureFormat.R8, mipChain: false, linear: true);

        var pixels = new Color32[atlas.Length];
        for (int i = 0; i < atlas.Length; i++)
        {
            byte v = (byte)Mathf.Clamp(Mathf.RoundToInt(atlas[i] * 255f), 0, 255);
            pixels[i] = new Color32(v, 0, 0, 255);
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    // --- Asset writing -----------------------------------------------------

    // Writes the atlas texture and its manifest to disk, then applies import settings
    // appropriate for distance data.
    //
    // Returns the asset path of the written texture.
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

    // Configures the atlas texture's importer for distance-field data.
    //
    // The settings here are not cosmetic: sRGB would gamma-warp the distances, compression
    // would quantise exactly the edge precision the technique depends on, and point filtering
    // would defeat the sub-texel edge reconstruction that makes an SDF sharper than its
    // resolution.
    static void ApplyImportSettings(string assetPath, SDFAtlasInfo info)
    {
        var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer == null) return;

        importer.textureType = TextureImporterType.SingleChannel;
        importer.sRGBTexture = false;
        importer.alphaSource = TextureImporterAlphaSource.None;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.filterMode = FilterMode.Bilinear;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.mipmapEnabled = true;
        importer.maxTextureSize = Mathf.Max(info.TextureWidth, info.TextureHeight);

        // Mips are generated normally. Cells are independent images sharing one texture, so
        // deep mip levels do average across cell boundaries -- but that is a padding question
        // rather than a reason to hand-build the chain, and it is how TextMeshPro's glyph
        // atlases behave too. SDFAtlasInfo.SafeMipLevel reports which levels stay clean at a
        // given padding; if a distant sign shows contamination, raise the padding and rebuild.
        //
        // preserveCoverage stays off because it is an alpha-testing feature: it rescales mip
        // levels to hold a coverage ratio, which would distort the distance values.
        importer.mipMapsPreserveCoverage = false;

        // Which component a SingleChannel texture reads from lives on TextureImporterSettings,
        // not on the importer itself, so it needs a read-modify-write rather than a direct set.
        //
        // Red rather than Alpha: the packer writes distances into the red channel with alpha
        // forced opaque, so Red states what is actually true instead of leaving the importer
        // to infer a channel from the source PNG. It also means the atlas previews visibly in
        // the inspector, which matters when eyeballing a packed atlas for misplaced cells.
        var settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        settings.singleChannelComponent = TextureImporterSingleChannelComponent.Red;
        importer.SetTextureSettings(settings);

        importer.SaveAndReimport();
    }

    // Creates each missing segment of a project-relative folder path.
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
