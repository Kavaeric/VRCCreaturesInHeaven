using System.IO;
using UnityEngine;
using UnityEditor;

// Sidecar metadata written alongside a packed Moment Texture3D asset.
// Stored as JSON at <textureName>.alv.json, adjacent to the texture asset.
// Read by MomentEInsAnimatedLightVolume when AnimatedTexture is assigned, so the snapshot
// layout can be populated without manual entry.
[System.Serializable]
public class MomentTextureInfo
{
    // Bump when the sidecar layout changes in a way that needs migration on load.
    // 0/missing = pre-versioning (single-column Y stack, snapshotsPerColumn implicitly == numSnapshots).
    // 1 = explicit single-column field added (no behaviour change vs. 0).
    // 2 = column-wrapped flipbook: snapshotsPerColumn/numColumns describe the 2D grid.
    public const int CurrentSchemaVersion = 2;

    public int schemaVersion;            // See CurrentSchemaVersion. Filled in by Load() if absent.

    public int snapshotX;                 // Spatial width  of one snapshot (texture X per column)
    public int snapshotY;                 // Spatial height of one snapshot (texture Y per row in a column)
    public int snapshotZ;                 // Spatial depth  of one snapshot (texture Z / numSlots)
    public int numSnapshots;              // Total number of baked snapshots
    // 2D flipbook grid. snapshotsPerColumn rows tall, numColumns columns wide; snapshots fill a
    // column before wrapping to the next. Legacy sidecars (schemaVersion < 2) get these populated
    // on load with the single-column equivalent.
    public int snapshotsPerColumn;
    public int numColumns;
    public MomentALVSHMode   shMode;            // SH fidelity mode (L1 / MonoL1 / MonoL0)
    public MomentALVBitDepth bitDepth;          // Bit depth (Depth8 / Depth16)

    // Per-snapshot bake state. Length == numSnapshots when fully initialised.
    public SnapshotEntry[] snapshots;

    [System.Serializable]
    public struct SnapshotEntry
    {
        public bool  baked;          // True once this snapshot's slice has been written to the atlas
        public int   animFrame;      // Animation window frame index this snapshot was sampled from
        public float renderSeconds;  // Wall-clock seconds the Bakery render took; -1 if not recorded
    }

    // Returns true if the given params match this sidecar's texture layout.
    // When false, the existing atlas cannot be reused and a full re-bake is needed.
    // numColumns is recomputed from snapshotY and numSnapshots, so the caller doesn't have to
    // pass it — the layout is fully determined once snapshotY and numSnapshots are known.
    public bool MatchesParams(int x, int y, int z, int count, MomentALVSHMode mode, MomentALVBitDepth depth) =>
        snapshotX == x && snapshotY == y && snapshotZ == z &&
        numSnapshots == count && shMode == mode && bitDepth == depth;

    // Derives the sidecar path from a texture asset path.
    public static string SidecarPath(string assetPath) =>
        Path.ChangeExtension(assetPath, null) + ".alv.json";

    // Writes this info as JSON adjacent to the given texture asset path.
    public void Save(string assetPath)
    {
        string json = JsonUtility.ToJson(this, prettyPrint: true);
        File.WriteAllText(SidecarPath(assetPath), json);
        AssetDatabase.ImportAsset(SidecarPath(assetPath));
    }

    // Loads and returns the sidecar for a given texture asset path, or null if missing/invalid.
    // Performs in-memory migration of legacy sidecars: pre-schema-v2 files have no
    // snapshotsPerColumn/numColumns fields, so we fill them with the single-column equivalent.
    // The file on disk is unchanged until the next Save(), which is fine — old layouts pack
    // identically when numColumns == 1.
    public static MomentTextureInfo Load(string assetPath)
    {
        string path = SidecarPath(assetPath);
        if (!File.Exists(path)) return null;
        MomentTextureInfo info;
        try { info = JsonUtility.FromJson<MomentTextureInfo>(File.ReadAllText(path)); }
        catch { return null; }
        if (info == null) return null;
        Migrate(info);
        return info;
    }

    // Brings a freshly-deserialised sidecar up to CurrentSchemaVersion. Idempotent — calling this
    // on an already-current sidecar is a no-op. Mutates in place.
    static void Migrate(MomentTextureInfo info)
    {
        if (info.schemaVersion >= CurrentSchemaVersion) return;

        // v0/v1 → v2: column-wrap fields didn't exist. Everything was a single Y-stack column.
        if (info.snapshotsPerColumn <= 0) info.snapshotsPerColumn = Mathf.Max(1, info.numSnapshots);
        if (info.numColumns         <= 0) info.numColumns         = 1;

        info.schemaVersion = CurrentSchemaVersion;
    }

    // Writes snapshot layout metadata from this sidecar into a MomentAnimatedLightVolume component.
    public void ApplyTo(MomentAnimatedLightVolume alv)
    {
        alv.SnapshotX           = snapshotX;
        alv.SnapshotY           = snapshotY;
        alv.SnapshotsPerColumn  = snapshotsPerColumn > 0 ? snapshotsPerColumn : Mathf.Max(1, numSnapshots);
        alv.NumColumnsBaked     = numColumns         > 0 ? numColumns         : 1;
        alv.NumSnapshotsBaked   = numSnapshots;
        alv.SHMode              = shMode;
        alv.BitDepth            = bitDepth;
        EditorUtility.SetDirty(alv);
    }
}
