using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

// Packs encoded SDF graphics into a uniform-grid atlas texture.
//
// One source texture per cell; cells are addressed by index and the grid never reflows, so
// a graphic's cell assignment should be stable for the life of the atlas, at least until
// the user repacks the whole thing and reorders all the graphics.
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
        settings.cellWidth = info.cellWidth;
        settings.cellHeight = info.cellHeight;
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

    // Encodes one source into a full cellWidth x cellHeight block, with the artwork inset by
    // the manifest's padding.
    //
    // The inset is applied before the distance transform, by compositing the source into a
    // larger transparent canvas. That way the transform measures real distances from the
    // border texels to artwork that genuinely sits inset, so the padding contains a true
    // continuation of the field rather than a synthesised or clamped fill. Post-filling the
    // border would create a distance discontinuity at the padding boundary, which pinches
    // edges near the cell edge.
    static float[] EncodeCell(Texture2D source, SDFAtlasInfo info, SDFAtlasEncoder.Settings settings)
    {
        float[] coverage = SDFAtlasEncoder.ReadCoverage(source, settings.channel);

        // Scale factor from cell to artwork area, per axis. The source is composited into a
        // canvas proportionally larger than itself, so that after downsampling to the cell's
        // dimensions the artwork lands in the inner ArtworkWidth x ArtworkHeight rectangle.
        //
        // The two axes are computed independently: padding is a fixed texel count, so on a
        // non-square cell it is a different *fraction* of each axis, and a single shared
        // inset would misplace the artwork on the longer one.
        float insetX = (float)info.ArtworkWidth / info.cellWidth;
        float insetY = (float)info.ArtworkHeight / info.cellHeight;

        int canvasWidth = Mathf.Max(1, Mathf.RoundToInt(source.width / insetX));
        int canvasHeight = Mathf.Max(1, Mathf.RoundToInt(source.height / insetY));

        float[] canvas = CompositeCentred(coverage, source.width, source.height, canvasWidth, canvasHeight);

        // Encode at full cell size. The transform runs across the whole canvas including the
        // border region, so border texels get correct distances to the inset artwork.
        return SDFAtlasEncoder.EncodeCoverage(canvas, canvasWidth, canvasHeight, settings,
                                              info.cellWidth, info.cellHeight);
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
    // Note that the padding is part of the cell's encoded data, not a gap between cells.
    // So a 64x64 cell in an atlas with padding set to 2px on all sides will output a graphic
    // at 60x60.
    //
    // Cell Y and the texture buffer both run bottom-up (see SDFAtlasInfo's addressing notes),
    // so no row flip is needed here.
    static void BlitCell(float[] atlas, int atlasWidth, int atlasHeight,
                         float[] cell, SDFAtlasInfo info, int cellX, int cellY)
    {
        int originX = cellX * info.cellWidth;
        int originY = cellY * info.cellHeight;

        for (int y = 0; y < info.cellHeight; y++)
        {
            int destY = originY + y;
            if (destY < 0 || destY >= atlasHeight) continue;

            int destRow = destY * atlasWidth;
            int srcRow = y * info.cellWidth;

            for (int x = 0; x < info.cellWidth; x++)
            {
                int destX = originX + x;
                if (destX < 0 || destX >= atlasWidth) continue;
                atlas[destRow + destX] = cell[srcRow + x];
            }
        }
    }

    // --- Texture output ---------------------------------------------------

    // Converts the packed float buffer into a single-channel texture.
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
    // A reference image is written alongside them, for use as a backdrop when authoring UVs.
    // See SDFAtlasReference.
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

        Texture2D reference = SDFAtlasReference.Build(atlas, info);
        SDFAtlasReference.Write(reference, assetPath);
        Object.DestroyImmediate(reference);

        return assetPath;
    }

    // Configures the atlas texture's importer for distance-field data.
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

        // Mips are generated normally.
        //
        // preserveCoverage rescales mip levels to hold a coverage ratio, which would distort
        // the distance values, so disable it.
        importer.mipMapsPreserveCoverage = false;

        // Need to do this because we can't just do importer.singleChannelComponent = [Red]
        // for whatever reason.
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
