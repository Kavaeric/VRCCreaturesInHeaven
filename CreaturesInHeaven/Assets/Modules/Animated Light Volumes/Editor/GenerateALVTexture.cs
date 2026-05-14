using UnityEngine;
using UnityEditor;

// Texture generation tool for AnimatedLightVolume.
// Produces a packed Texture3D with layout:
//   X = spatial width
//   Y = spatial height * time frames  (frame 0 at Y=0, frame 1 at Y=H, etc.)
//   Z = spatial depth * 3 SH slots   (Tex0 at Z=0, Tex1 at Z=D, Tex2 at Z=2D)
//
// SH packing per voxel:
//   Tex0: (L0.r,  L0.g,  L0.b,  L1r.z)
//   Tex1: (L1r.x, L1g.x, L1b.x, L1g.z)
//   Tex2: (L1r.y, L1g.y, L1b.y, L1b.z)
public static class GenerateALVTexture
{
    // Spatial volume resolution for the test texture.
    const int W = 18, H = 18, D = 18;

    [MenuItem("Tools/Lighting/Generate Test ALV Texture")]
    static void GenerateTest()
    {
        // Frame 0: Overhead red
        // Frame 1: Sideways blue
        // Frame 2: Nothing
        // Frame 3: Warm overhead
        var frames = new FrameSH[]
        {
            new FrameSH {
                tex0 = new Color(0, 0, 0, 0),
                tex1 = new Color(0, 0, 0, 0),
                tex2 = new Color(0, 0, 0, 0),
            },
        };

        string scenePath = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
        string sceneName = System.IO.Path.GetFileNameWithoutExtension(scenePath);
        string sceneDir  = System.IO.Path.GetDirectoryName(scenePath);
        string assetDir  = $"{sceneDir}/{sceneName}/AnimatedLV";
        AnimatedLightVolumeEditor.CreateDirectory(assetDir);

        string path = $"{assetDir}/TestLight_Packed.asset";
        SavePackedTexture(frames, W, H, D, path);
    }

    // Builds a packed Texture3D from an array of FrameSH and saves it at the
    // given asset path. If an asset already exists at that path, its pixel data
    // is updated in place so serialised references (GUIDs) remain stable.
    public static void SavePackedTexture(FrameSH[] frames, int w, int h, int d, string assetPath)
    {
        int numFrames   = frames.Length;
        int totalHeight = h * numFrames;
        int totalDepth  = d * 3;

        Color[] pixels = new Color[w * totalHeight * totalDepth];

        for (int f = 0; f < numFrames; f++)
        {
            int yOffset = f * h;
            WriteBlock(pixels, w, totalHeight, h, d, yOffset, 0,     frames[f].tex0);
            WriteBlock(pixels, w, totalHeight, h, d, yOffset, d,     frames[f].tex1);
            WriteBlock(pixels, w, totalHeight, h, d, yOffset, d * 2, frames[f].tex2);
        }

        // Reuse existing asset to preserve GUID.
        // Prevents serialised field linkage breaking whenever the texture gets updated.
        Texture3D existing = AssetDatabase.LoadAssetAtPath<Texture3D>(assetPath);
        if (existing != null && existing.width == w && existing.height == totalHeight && existing.depth == totalDepth)
        {
            existing.SetPixels(pixels);
            existing.Apply();
            EditorUtility.SetDirty(existing);
        }
        else
        {
            Texture3D tex = new Texture3D(w, totalHeight, totalDepth, TextureFormat.RGBAHalf, false);
            tex.wrapMode   = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.SetPixels(pixels);
            tex.Apply();

            // Overwrite at path if dimensions changed, otherwise create fresh.
            if (existing != null)
                AssetDatabase.DeleteAsset(assetPath);

            AssetDatabase.CreateAsset(tex, assetPath);
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[GenerateAVLTexture] Saved {assetPath} ({w}x{totalHeight}x{totalDepth}, {numFrames} frames)");
    }

    // Fills a w x blockH x blockD block at (yOffset, zOffset) with a constant colour.
    static void WriteBlock(Color[] pixels, int w, int totalHeight, int blockH, int blockD, int yOffset, int zOffset, Color value)
    {
        for (int z = 0; z < blockD; z++)
            for (int y = 0; y < blockH; y++)
                for (int x = 0; x < w; x++)
                    pixels[x + (y + yOffset) * w + (z + zOffset) * w * totalHeight] = value;
    }

    public struct FrameSH
    {
        public Color tex0, tex1, tex2;
    }
}
