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

    // Reports progress while packing. `entryIndex`/`entryCount` place the current SVG within
    // the whole batch; `cellProgress` (0..1) is how far that one SVG's own generation has got,
    // so a batch of one slow, complex graphic still moves visibly rather than sitting at one
    // fraction until it's entirely done. Return false to cancel -- Pack unwinds via
    // MSDFAtlasField.GenerationCancelledException and returns null.
    public delegate bool ProgressCallback(int entryIndex, int entryCount, string name, float cellProgress);

    // Builds the atlas texture from a set of entries.
    //
    // Returns an RGB24 texture sized to the manifest's grid, and fills in the manifest's
    // cell table as a side effect. Cells with no entry are left fully outside, so an
    // unaddressed cell renders as nothing rather than as a block of colour.
    //
    // Returns null if `onProgress` cancels partway through. The manifest may already reflect
    // some entries packed before the cancel; the caller discards the whole attempt rather than
    // trying to salvage a partial atlas, since a partial bake is not a state anyone wants to
    // ship.
    public static Texture2D Pack(IList<Entry> entries, SDFAtlasInfo info, Settings settings,
                                 ProgressCallback onProgress = null)
    {
        int width = info.TextureWidth;
        int height = info.TextureHeight;

        // Three floats per texel, interleaved, matching the generator's layout.
        var atlas = new float[width * height * 3];

        // Start every channel fully outside. Stored 0 is the deepest-outside value, so the
        // median of three zeroes is also fully outside.
        for (int i = 0; i < atlas.Length; i++) atlas[i] = 0f;

        try
        {
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

                int entryIndex = i;
                float[] cell = EncodeCell(entry.svgPath, info, settings, cellProgress =>
                {
                    if (onProgress != null && !onProgress(entryIndex, entries.Count, entry.name, cellProgress))
                        throw new MSDFAtlasField.GenerationCancelledException();
                }, out float anisotropy);
                if (cell == null) continue;

                WarnOnThinSpread(entry.name, info, anisotropy);

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
        }
        catch (MSDFAtlasField.GenerationCancelledException)
        {
            return null;
        }

        return ToTexture(atlas, width, height);
    }

    // Encodes one SVG into a full cellWidth x cellHeight x 3 block.
    static float[] EncodeCell(string svgPath, SDFAtlasInfo info, Settings settings,
                              System.Action<float> onProgress, out float anisotropy)
    {
        anisotropy = 1f;

        SDFAtlasShape shape = SDFAtlasSvgLoader.Load(svgPath, out Vector2 documentSize);
        if (shape == null)
        {
            Debug.LogWarning($"[SDFAtlas] Could not load '{svgPath}'; cell left empty.");
            return null;
        }

        // Preserve any whitespace deliberately included by framing the document.
        // The padding is the margin, and the distance field continues naturally into it
        // as it's measured from the curves themselves.
        //
        // Under PreserveAspect, a graphic whose aspect does not match its cell is
        // letterboxed rather than stretched: texels are wasted on margin, but the field
        // stays isotropic and one stored unit means the same real distance on both axes.
        //
        // Under Stretch, each axis is scaled to fill the cell, so none of the cell's
        // resolution is spent on margin. The field is then anisotropic, which the reported
        // scale lets the caller warn about -- see the note in FitDocumentToBox.
        Vector2 framingScale = SDFAtlasShapeRasteriser.FitDocumentToBox(
            shape, documentSize, info.cellWidth, info.cellHeight, info.padding, FramingMode(info));

        anisotropy = SDFAtlasShapeRasteriser.AnisotropyRatio(framingScale);

        MSDFAtlasEdgeColouring.Apply(shape, settings.angleThreshold, settings.seed);

        float[] raw = MSDFAtlasField.Generate(shape, info.cellWidth, info.cellHeight, onProgress);
        float[] encoded = MSDFAtlasField.Encode(raw, info.spread);

        if (settings.errorCorrection)
            MSDFAtlasField.CorrectErrors(shape, encoded, info.cellWidth, info.cellHeight, info.spread);

        return encoded;
    }

    // Manifest framing translated into the rasteriser's own enum.
    //
    // The two enums are deliberately separate types (see SDFAtlasInfo.AtlasFraming), so the
    // mapping lives here rather than either of them depending on the other.
    static SDFAtlasShapeRasteriser.FramingMode FramingMode(SDFAtlasInfo info) =>
        info.IsStretched
            ? SDFAtlasShapeRasteriser.FramingMode.Stretch
            : SDFAtlasShapeRasteriser.FramingMode.PreserveAspect;

    // Copies an encoded cell into its grid position.
    //
    // Cell Y and the texture buffer both run bottom-up (see SDFAtlasInfo's addressing
    // notes), so no row flip is needed.
    static void BlitCell(float[] atlas, int atlasWidth, int atlasHeight,
                         float[] cell, SDFAtlasInfo info, int cellX, int cellY)
    {
        int originX = cellX * info.cellWidth;
        int originY = cellY * info.cellHeight;

        for (int y = 0; y < info.cellHeight; y++)
        {
            int destY = originY + y;
            if (destY < 0 || destY >= atlasHeight) continue;

            for (int x = 0; x < info.cellWidth; x++)
            {
                int destX = originX + x;
                if (destX < 0 || destX >= atlasWidth) continue;

                int destIndex = (destY * atlasWidth + destX) * 3;
                int srcIndex = (y * info.cellWidth + x) * 3;

                atlas[destIndex + 0] = cell[srcIndex + 0];
                atlas[destIndex + 1] = cell[srcIndex + 1];
                atlas[destIndex + 2] = cell[srcIndex + 2];
            }
        }
    }

    // Effective spread, in texels, below which the shader has too little gradient to
    // antialias against and 8-bit quantisation starts to show as stepping along the edge.
    const float MinUsableSpread = 1.5f;

    // Warns when stretching has left one axis with too little effective spread.
    //
    // Stretch compresses the field along the axis that had to shrink more, and the stored
    // spread is divided by exactly that ratio on that axis. A graphic stretched 4:1 with a
    // stored spread of 2 has an effective spread of 0.5 on its narrow axis, which is not
    // enough for the shader's smoothstep to resolve. Stretch and a small spread pull against
    // each other, and this is where that shows up, so it is worth naming the graphic rather
    // than leaving it to be spotted by eye later.
    static void WarnOnThinSpread(string name, SDFAtlasInfo info, float anisotropy)
    {
        if (anisotropy <= 1.001f) return;

        float effectiveSpread = info.spread / anisotropy;
        if (effectiveSpread >= MinUsableSpread) return;

        Debug.LogWarning(
            $"[SDFAtlas] '{name}' is stretched {anisotropy:0.##}:1 to fill its cell, leaving an " +
            $"effective spread of {effectiveSpread:0.##} texels on its narrow axis " +
            $"(stored spread {info.spread}). Below about {MinUsableSpread} the shader has too " +
            "little gradient to antialias against and edges may look stepped. Raise the spread, " +
            "use a cell aspect closer to the artwork, or frame this atlas with preserved aspect.");
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
    //
    // A reference image is written alongside them, for use as a backdrop when authoring UVs.
    // See SDFAtlasReference.
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
