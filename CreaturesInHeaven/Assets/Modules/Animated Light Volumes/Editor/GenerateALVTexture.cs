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
    const int W = 18, H = 14, D = 18;

    [MenuItem("Tools/Lighting/Generate Test ALV Texture")]
    static void GenerateTest()
    {
        int voxels = W * H * D;
        var frames = new FrameSH[]
        {
            new FrameSH {
                tex0 = FillArray(voxels, new Color(1, 0, 0, 0)),
                tex1 = FillArray(voxels, new Color(0, 0, 0, 0)),
                tex2 = FillArray(voxels, new Color(1, 0, 0, 0)),
            },
            new FrameSH {
                tex0 = FillArray(voxels, new Color(0, 0, 1, 0)),
                tex1 = FillArray(voxels, new Color(0, 0, 1, 0)),
                tex2 = FillArray(voxels, new Color(0, 0, 0, 0)),
            },
            new FrameSH {
                tex0 = FillArray(voxels, Color.black),
                tex1 = FillArray(voxels, Color.black),
                tex2 = FillArray(voxels, Color.black),
            },
            new FrameSH {
                tex0 = FillArray(voxels, new Color(1.0f, 1.0f, 1.0f, 0f)),
                tex1 = FillArray(voxels, Color.black),
                tex2 = FillArray(voxels, new Color(0.5f, 0.2f, 0f, 0f)),
            },
        };

        string scenePath = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
        string sceneName = System.IO.Path.GetFileNameWithoutExtension(scenePath);
        string sceneDir  = System.IO.Path.GetDirectoryName(scenePath);
        string assetDir  = $"{sceneDir}/{sceneName}/AnimatedLV";
        ALVEditor.CreateDirectory(assetDir);

        string path = $"{assetDir}/TestLight_Packed.asset";
        SavePackedTexture(frames, W, H, D, path);
    }

    static Color[] FillArray(int count, Color value)
    {
        Color[] arr = new Color[count];
        for (int i = 0; i < count; i++) arr[i] = value;
        return arr;
    }

    // Builds a packed Texture3D from an array of FrameSH and saves it at the given asset path.
    // Each FrameSH.tex0/1/2 must be a Color[] of length w*h*d in XYZ order (x fastest).
    // If an asset already exists at that path, its pixel data is updated in place so serialised
    // references (GUIDs) remain stable.
    public static void SavePackedTexture(FrameSH[] frames, int w, int h, int d, string assetPath)
    {
        int numFrames   = frames.Length;
        int totalHeight = h * numFrames;
        int totalDepth  = d * 3;

        Color[] pixels = new Color[w * totalHeight * totalDepth];

        for (int f = 0; f < numFrames; f++)
        {
            int yOffset = f * h;
            CopyBlock(pixels, frames[f].tex0, w, totalHeight, h, d, yOffset, 0);
            CopyBlock(pixels, frames[f].tex1, w, totalHeight, h, d, yOffset, d);
            CopyBlock(pixels, frames[f].tex2, w, totalHeight, h, d, yOffset, d * 2);
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
        Debug.Log($"[GenerateALVTexture] Saved {assetPath} ({w}x{totalHeight}x{totalDepth}, {numFrames} frames)");
    }

    // Copies a w*h*d source block (XYZ order, x fastest) into the packed pixel array
    // at the given yOffset and zOffset within the atlas layout.
    static void CopyBlock(Color[] dst, Color[] src, int w, int totalHeight, int blockH, int blockD, int yOffset, int zOffset)
    {
        for (int z = 0; z < blockD; z++)
            for (int y = 0; y < blockH; y++)
                for (int x = 0; x < w; x++)
                    dst[x + (y + yOffset) * w + (z + zOffset) * w * totalHeight] = src[x + y * w + z * w * blockH];
    }

    // One baked frame: three SH textures as flat Color arrays, length w*h*d, XYZ order (x fastest).
    public struct FrameSH
    {
        public Color[] tex0, tex1, tex2;
    }
}
