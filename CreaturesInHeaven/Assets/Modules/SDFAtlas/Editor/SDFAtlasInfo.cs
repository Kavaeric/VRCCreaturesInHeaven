using System.IO;
using UnityEngine;
using UnityEditor;

// Sidecar metadata written alongside a packed SDF atlas texture.
// Stored as JSON at <atlasName>.sdfatlas.json, adjacent to the texture asset.
//
// Read by the builder window (to restore the cell assignment list between sessions) and by
// the material inspector (to report each graphic's UV rectangle for mesh authoring, and to
// check the material's spread against the one the atlas was packed at).
//
// The atlas layout is a uniform grid, so a cell's position and its artwork rectangle are
// pure arithmetic on the cell index. No lookup table needed! Woo!
[System.Serializable]
public class SDFAtlasInfo
{
    // Bump when the layout changes in a way that needs migration on load.
    // 1 = initial: uniform grid, atlas-wide padding, and an encoding field distinguishing
    //     single-channel SDF from multi-channel MSDF.
    // 2 = cells may be non-square: the scalar cellSize became cellWidth/cellHeight, and
    //     framing gained a mode so artwork can be stretched to fill its cell rather than
    //     always letterboxed into it.
    //     No migration from 1; those atlases are rebuilt rather than converted.
    public const int CurrentSchemaVersion = 2;

    public int schemaVersion;      // See CurrentSchemaVersion. Filled in by Load() if absent.

    // --- Encoding ---------------------------------------------------------

    // How distance is stored in the atlas texture.
    //
    // This is not a cosmetic label: the two need different shaders, and pairing an atlas
    // with the wrong one degrades quality silently rather than failing. Recorded here so
    // the material inspector can check the pairing and say so.
    public enum AtlasEncoding
    {
        // R8, one distance per texel. Corners round off below roughly 128px cells.
        SingleChannel = 0,

        // RGB24, three distance fields whose median is the true distance. Corners stay
        // sharp at small cell sizes, at three times the memory of single-channel.
        MultiChannel = 1,
    }

    public AtlasEncoding encoding = AtlasEncoding.SingleChannel;

    public bool IsMultiChannel => encoding == AtlasEncoding.MultiChannel;

    // Bytes per texel implied by the encoding, for the size estimates the builder reports.
    public int BytesPerTexel => encoding == AtlasEncoding.MultiChannel ? 3 : 1;

    // --- Framing -----------------------------------------------------------

    // How each graphic was fitted into its cell. Recorded because it is not recoverable
    // from the texture, and because it changes what the stored field means: under Stretch
    // the field is anisotropic, so one stored unit is worth more real distance along one
    // axis than the other.
    //
    // Mirrors SDFAtlasShapeRasteriser.FramingMode, kept as its own enum so the manifest
    // does not depend on the rasteriser's type for its serialised form.
    public enum AtlasFraming
    {
        // Uniform scale; non-square artwork is letterboxed and the field stays isotropic.
        PreserveAspect = 0,

        // Per-axis scale; artwork fills the cell and the field is anisotropic.
        Stretch = 1,
    }

    public AtlasFraming framing = AtlasFraming.PreserveAspect;

    public bool IsStretched => framing == AtlasFraming.Stretch;

    // --- Grid layout ----------------------------------------------------

    // Cell dimensions in texels, including padding. Usually equal, but a non-square cell
    // suits artwork that is itself strongly non-square: a 2:1 graphic in a 2:1 cell spends
    // its texels where the graphic actually has extent, rather than on empty margin above
    // and below. Artwork is framed preserving aspect either way, so the stored field stays
    // isotropic and one stored unit means the same real distance on both axes.
    public int cellWidth;
    public int cellHeight;

    public int gridWidth;          // Cells across
    public int gridHeight;         // Cells down

    // Texels of border inside each cell that carry distance data continuing past the
    // artwork's edge. Uniform on all four sides, so the artwork occupies the inner
    // (cellWidth - 2 * padding) x (cellHeight - 2 * padding) rectangle. Uniform rather than
    // per-axis because padding is a mip-safety margin -- see SafeMipLevel -- and safety is
    // measured in texels, not in a fraction of the cell.
    public int padding;

    // Distance range, in cell texels, mapped to the stored 0..1. Recorded so the shader can
    // reconstruct true distances (for outlines, glows, or thickness adjustment) rather than
    // only thresholding at 0.5.
    public float spread;

    // --- Cell contents ---------------------------------------------------

    // One entry per grid cell, row-major, length == gridWidth * gridHeight.
    // Empty cells are present but flagged unoccupied, so cell indices stay stable as graphics
    // are added and removed. Index stability matters because a cell's index fixes where its
    // graphic sits in the texture: renumbering moves artwork out from under the UVs of every
    // quad already authored against it.
    public CellEntry[] cells;

    [System.Serializable]
    public struct CellEntry
    {
        public bool occupied;
        public string sourceGuid;   // AssetDatabase GUID of the source texture, for re-packing
        public string name;         // Human-readable label, shown in the placement tool's picker
    }

    // --- Derived values ---------------------------------------------------

    // Atlas texture dimensions implied by the grid.
    public int TextureWidth => cellWidth * gridWidth;
    public int TextureHeight => cellHeight * gridHeight;

    public int CellCount => gridWidth * gridHeight;

    // Dimensions of the artwork area inside each cell, excluding padding.
    public int ArtworkWidth => cellWidth - 2 * padding;
    public int ArtworkHeight => cellHeight - 2 * padding;

    // Whether cells are square, which is the common case and the one the encoder can take
    // shortcuts for.
    public bool IsSquareCell => cellWidth == cellHeight;

    // Deepest mip level whose texels still average only within a cell, given the padding.
    //
    // A level-N texel averages a 2^N block, so as long as the padding is at least that wide,
    // a tap near the cell edge is still reading this cell's own data. Past that point
    // neighbouring cells start contaminating each other and no amount of shader clamping
    // helps, because the averaging already happened at texture-build time.
    public int SafeMipLevel
    {
        get
        {
            int level = 0;
            while ((1 << (level + 1)) <= Mathf.Max(padding, 1)) level++;
            return level;
        }
    }

    // --- Construction ------------------------------------------------------

    // Creates an empty manifest for a grid of the given shape.
    public static SDFAtlasInfo Create(int cellWidth, int cellHeight,
                                      int gridWidth, int gridHeight, int padding,
                                      float spread,
                                      AtlasEncoding encoding = AtlasEncoding.SingleChannel,
                                      AtlasFraming framing = AtlasFraming.PreserveAspect)
    {
        var info = new SDFAtlasInfo
        {
            schemaVersion = CurrentSchemaVersion,
            cellWidth = cellWidth,
            cellHeight = cellHeight,
            gridWidth = gridWidth,
            gridHeight = gridHeight,
            padding = padding,
            spread = spread,
            encoding = encoding,
            framing = framing,
            cells = new CellEntry[gridWidth * gridHeight],
        };
        return info;
    }

    // Convenience overload for the square case, which is still the common one.
    public static SDFAtlasInfo Create(int cellSize, int gridWidth, int gridHeight, int padding,
                                      float spread,
                                      AtlasEncoding encoding = AtlasEncoding.SingleChannel,
                                      AtlasFraming framing = AtlasFraming.PreserveAspect) =>
        Create(cellSize, cellSize, gridWidth, gridHeight, padding, spread, encoding, framing);

    // --- Cell addressing ---------------------------------------------------
    // Converts a cell index to its grid coordinate. Row-major from the bottom-left:
    // index 0 is (0,0), increasing left to right then bottom to top.
    public void IndexToCoord(int index, out int cellX, out int cellY)
    {
        cellX = index % gridWidth;
        cellY = index / gridWidth;
    }

    public int CoordToIndex(int cellX, int cellY) => cellY * gridWidth + cellX;

    // Returns the texel origin of a cell's artwork area (i.e. inside the padding), in
    // atlas texture space, with the texture's bottom-left as (0, 0).
    public void CellArtworkOrigin(int cellX, int cellY, out int texelX, out int texelY)
    {
        texelX = cellX * cellWidth + padding;
        texelY = cellY * cellHeight + padding;
    }

    // --- Persistence -------------------------------------------------------

    // Derives the manifest path from an atlas texture asset path.
    public static string ManifestPath(string atlasAssetPath) =>
        Path.ChangeExtension(atlasAssetPath, null) + ".sdfatlas.json";

    // Writes this manifest as JSON adjacent to the given atlas texture path.
    public void Save(string atlasAssetPath)
    {
        string path = ManifestPath(atlasAssetPath);
        File.WriteAllText(path, JsonUtility.ToJson(this, prettyPrint: true));
        AssetDatabase.ImportAsset(path);
    }

    // Loads the manifest for a given atlas texture path, or null if missing/invalid.
    public static SDFAtlasInfo Load(string atlasAssetPath)
    {
        string path = ManifestPath(atlasAssetPath);
        if (!File.Exists(path)) return null;

        SDFAtlasInfo info;
        try { info = JsonUtility.FromJson<SDFAtlasInfo>(File.ReadAllText(path)); }
        catch { return null; }

        if (info == null) return null;

        // Schema 1 stored a single scalar cellSize, which JsonUtility drops on the floor
        // here, leaving cellWidth/cellHeight at zero and every derived dimension at zero
        // with it. Reject rather than migrate: those atlases are rebuilt from source, and a
        // manifest that loads into a zero-sized grid is far worse than one that refuses.
        if (info.schemaVersion < CurrentSchemaVersion || info.cellWidth <= 0 || info.cellHeight <= 0)
        {
            Debug.LogWarning(
                $"[SDFAtlas] '{path}' uses schema version {info.schemaVersion}, but " +
                $"{CurrentSchemaVersion} is required. Rebuild the atlas from its sources.");
            return null;
        }

        return info;
    }
}
