using System.IO;
using UnityEngine;
using UnityEditor;

// Sidecar metadata written alongside a packed SDF atlas texture.
// Stored as JSON at <atlasName>.sdfatlas.json, adjacent to the texture asset.
//
// Read by the shader setup (to derive cell addressing constants), by the builder window
// (to restore the cell assignment list between sessions), and later by the placement tool
// (to populate its graphic picker).
//
// The atlas layout is a uniform grid, which is what makes UDIM addressing pure arithmetic:
// a quad's integer UV is its cell coordinate. No lookup table needed! Woo!
[System.Serializable]
public class SDFAtlasInfo
{
    // Bump when the layout changes in a way that needs migration on load.
    // 1 = initial: uniform grid, atlas-wide padding, single-channel SDF.
    public const int CurrentSchemaVersion = 1;

    public int schemaVersion;      // See CurrentSchemaVersion. Filled in by Load() if absent.

    // --- Grid layout ----------------------------------------------------

    public int cellSize;           // Cell edge length in texels, including padding (e.g. 64)
    public int gridWidth;          // Cells across
    public int gridHeight;         // Cells down

    // Texels of border inside each cell that carry distance data continuing past the
    // artwork's edge. The artwork itself occupies the inner (cellSize - 2 * padding) square.
    public int padding;

    // Distance range, in cell texels, mapped to the stored 0..1. Recorded so the shader can
    // reconstruct true distances (for outlines, glows, or thickness adjustment) rather than
    // only thresholding at 0.5.
    public float spread;

    // --- Cell contents ---------------------------------------------------

    // One entry per grid cell, row-major, length == gridWidth * gridHeight.
    // Empty cells are present but flagged unoccupied, so cell indices stay stable as
    // graphics are added and removed. Index stability matters: the cell index is baked into
    // mesh UVs at authoring time, so renumbering silently breaks every quad already placed.
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
    public int TextureWidth => cellSize * gridWidth;
    public int TextureHeight => cellSize * gridHeight;

    public int CellCount => gridWidth * gridHeight;

    // Edge length of the artwork area inside each cell, excluding padding.
    public int ArtworkSize => cellSize - 2 * padding;

    // Deepest mip level whose texels still average only within a cell, given the padding.
    //
    // A level-N texel averages a 2^N block, so as long as the padding is at least that wide,
    // a tap near the cell edge is still reading this cell's own data. Past that point
    // neighbouring cells start contaminating each other and no amount of shader clamping
    // helps, because the averaging already happened at texture-build time.
    //
    // Reported rather than enforced: mips are generated normally and bleed is treated as a
    // padding question, the same way TextMeshPro handles its glyph atlases. If a distant sign
    // shows contamination, raise the padding and rebuild.
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
    public static SDFAtlasInfo Create(int cellSize, int gridWidth, int gridHeight, int padding, float spread)
    {
        var info = new SDFAtlasInfo
        {
            schemaVersion = CurrentSchemaVersion,
            cellSize = cellSize,
            gridWidth = gridWidth,
            gridHeight = gridHeight,
            padding = padding,
            spread = spread,
            cells = new CellEntry[gridWidth * gridHeight],
        };
        return info;
    }

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
        texelX = cellX * cellSize + padding;
        texelY = cellY * cellSize + padding;
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
        Migrate(info);
        return info;
    }

    // Brings a freshly-deserialised manifest up to CurrentSchemaVersion. Idempotent.
    // Mutates in place. No migrations exist yet; the hook is here so the first schema
    // change does not require restructuring the load path.
    static void Migrate(SDFAtlasInfo info)
    {
        if (info.schemaVersion >= CurrentSchemaVersion) return;
        info.schemaVersion = CurrentSchemaVersion;
    }
}
