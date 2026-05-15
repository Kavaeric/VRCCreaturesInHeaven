using System.IO;
using UnityEngine;
using UnityEditor;

// Sidecar metadata written alongside a packed ALV Texture3D asset.
// Stored as JSON at <textureName>.alv.json, adjacent to the texture asset.
// Read by AnimatedLightVolumeEditor when AnimatedTexture is assigned, so
// SpatialHeight can be populated without manual entry.
[System.Serializable]
public class ALVTextureInfo
{
    public int frameX;     // Spatial width  of one frame (texture X)
    public int frameY;     // Spatial height of one frame (texture Y per frame)
    public int frameZ;     // Spatial depth  of one frame (texture Z / 3)
    public int numFrames;  // Total number of baked frames

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
    public static ALVTextureInfo Load(string assetPath)
    {
        string path = SidecarPath(assetPath);
        if (!File.Exists(path)) return null;
        try { return JsonUtility.FromJson<ALVTextureInfo>(File.ReadAllText(path)); }
        catch { return null; }
    }
}
