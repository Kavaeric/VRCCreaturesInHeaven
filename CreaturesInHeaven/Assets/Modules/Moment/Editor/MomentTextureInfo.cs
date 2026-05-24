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
    public int snapshotX;                 // Spatial width  of one snapshot (texture X)
    public int snapshotY;                 // Spatial height of one snapshot (texture Y per snapshot)
    public int snapshotZ;                 // Spatial depth  of one snapshot (texture Z / numSlots)
    public int numSnapshots;              // Total number of baked snapshots
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
    public static MomentTextureInfo Load(string assetPath)
    {
        string path = SidecarPath(assetPath);
        if (!File.Exists(path)) return null;
        try { return JsonUtility.FromJson<MomentTextureInfo>(File.ReadAllText(path)); }
        catch { return null; }
    }

    // Writes snapshot layout metadata from this sidecar into a MomentAnimatedLightVolume component.
    public void ApplyTo(MomentAnimatedLightVolume alv)
    {
        alv.SnapshotY = snapshotY;
        alv.SHMode    = shMode;
        alv.BitDepth  = bitDepth;
        EditorUtility.SetDirty(alv);
    }
}
