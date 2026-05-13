using UnityEngine;
using UnityEditor;

public static class GenerateTestLightTexture
{
    // Spatial volume resolution.
    const int W = 8, H = 8, D = 8;

    // Texture layout:
    //   Y axis: time frames, each H rows tall. Total height = H * numFrames.
    //   Z axis: SH sub-textures, each D slices deep. Total depth = D * 3.
    //
    //   Z slice  0 ..  D-1  →  Tex0  (L0.r,  L0.g,  L0.b,  L1r.z)
    //   Z slice  D .. 2D-1  →  Tex1  (L1r.x, L1g.x, L1b.x, L1g.z)
    //   Z slice 2D .. 3D-1  →  Tex2  (L1r.y, L1g.y, L1b.y, L1b.z)

    [MenuItem("Tools/Lighting/Generate Test Light Texture")]
    static void Generate()
    {
        // Frame 0: overhead red.   L0=(1,0,0), L1r=(0,1,0)
        // Frame 1: overhead blue.  L0=(0,0,1), L1b=(0,1,0)
        //
        // Packing per frame:
        //   Tex0: (L0.r, L0.g, L0.b, L1r.z)
        //   Tex1: (L1r.x, L1g.x, L1b.x, L1g.z)
        //   Tex2: (L1r.y, L1g.y, L1b.y, L1b.z)

        var frames = new FrameSH[]
        {
            new FrameSH {
                tex0 = new Color(0, 0, 0, 0), // L0=red, L1r.z=0
                tex1 = new Color(0, 0, 0, 0),
                tex2 = new Color(0, 0, 0, 0), // L1r.y=1 (overhead)
            },
            new FrameSH {
                tex0 = new Color(0, 0, 1, 0), // L0=blue, L1r.z=0
                tex1 = new Color(0, 0, 0, 0),
                tex2 = new Color(0, 0, 1, 0), // L1b.y=1 (overhead)
            },
        };

        int numFrames = frames.Length;
        int totalHeight = H * numFrames;
        int totalDepth  = D * 3;

        Texture3D packed = new Texture3D(W, totalHeight, totalDepth, TextureFormat.RGBAHalf, false);
        packed.wrapMode = TextureWrapMode.Clamp;
        packed.filterMode = FilterMode.Bilinear;

        Color[] pixels = new Color[W * totalHeight * totalDepth];

        for (int f = 0; f < numFrames; f++)
        {
            int yOffset = f * H;
            WriteBlock(pixels, W, totalHeight, H, D, yOffset, 0,     frames[f].tex0);
            WriteBlock(pixels, W, totalHeight, H, D, yOffset, D,     frames[f].tex1);
            WriteBlock(pixels, W, totalHeight, H, D, yOffset, D * 2, frames[f].tex2);
        }

        packed.SetPixels(pixels);
        packed.Apply();

        string path = "Assets/Scenes/Lighting test scene add/TestLight_Packed.asset";
        AssetDatabase.CreateAsset(packed, path);
        AssetDatabase.SaveAssets();

        Debug.Log($"[GenerateTestLightTexture] Saved {path} ({W}x{totalHeight}x{totalDepth}, {numFrames} frames)");
    }

    // Fills a W x blockH x blockD block at (yOffset, zOffset) with a constant colour.
    // pixels is indexed as: x + y * W + z * W * totalHeight
    static void WriteBlock(Color[] pixels, int w, int totalHeight, int blockH, int blockD, int yOffset, int zOffset, Color value)
    {
        for (int z = 0; z < blockD; z++)
            for (int y = 0; y < blockH; y++)
                for (int x = 0; x < w; x++)
                    pixels[x + (y + yOffset) * w + (z + zOffset) * w * totalHeight] = value;
    }

    struct FrameSH
    {
        public Color tex0, tex1, tex2;
    }
}
