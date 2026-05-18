using UnityEngine;
using UnityEditor;
using VRCLightVolumes;

// Texture generation tool for AnimatedLightVolume.
// Produces a packed Texture3D with layout:
//   X = spatial width
//   Y = spatial height * numSnapshots  (snapshot 0 at Y=0, snapshot 1 at Y=H, etc.)
//   Z = spatial depth * numSlots       (numSlots = 3 for L1, 2 for MonoL1, 1 for MonoL0)
//
// SH packing per voxel:
//   Tex0: (L0.r,  L0.g,  L0.b,  L1r.z)
//   Tex1: (L1r.x, L1g.x, L1b.x, L1g.z)
//   Tex2: (L1r.y, L1g.y, L1b.y, L1b.z)
public static class ALVTextureWriter
{
    // Builds a packed Texture3D from an array of SnapshotSH and saves it at the given asset path.
    // Each SnapshotSH.tex0/1/2 must be a Color[] of length w*h*d in XYZ order (x fastest).
    // If an asset already exists at that path, its pixel data is updated in place so serialised
    // references (GUIDs) remain stable.
    public static void SavePackedTexture(SnapshotSH[] snapshots, int w, int h, int d, string assetPath,
        ALVSHMode shMode = ALVSHMode.L1, ALVBitDepth bitDepth = ALVBitDepth.Depth8)
    {
        int numSnapshots = snapshots.Length;
        int totalHeight  = ALVFormat.PackedHeight(h, numSnapshots);
        int totalDepth   = ALVFormat.PackedDepth(d, shMode);

        Color[] pixels = new Color[w * totalHeight * totalDepth];

        for (int s = 0; s < numSnapshots; s++)
        {
            int yOffset = s * h;
            switch (shMode)
            {
                case ALVSHMode.L1:
                    // Full: 3 slots — tex0/1/2 passed through as-is.
                    CopyBlock(pixels, snapshots[s].tex0, w, totalHeight, h, d, yOffset, 0);
                    CopyBlock(pixels, snapshots[s].tex1, w, totalHeight, h, d, yOffset, d);
                    CopyBlock(pixels, snapshots[s].tex2, w, totalHeight, h, d, yOffset, d * 2);
                    break;

                case ALVSHMode.MonoL1:
                {
                    // 2 slots: Tex0 = (L0.r, L0.g, L0.b, 0), Tex1 = (L1.x, L1.y, L1.z, 0).
                    Color[] t0 = new Color[w * h * d];
                    Color[] t1 = new Color[w * h * d];
                    DownsampleMonoL1(snapshots[s], w * h * d, t0, t1);
                    CopyBlock(pixels, t0, w, totalHeight, h, d, yOffset, 0);
                    CopyBlock(pixels, t1, w, totalHeight, h, d, yOffset, d);
                    break;
                }

                case ALVSHMode.MonoL0:
                {
                    // 1 slot: Tex0 = (L0, L1.x, L1.y, L1.z).
                    Color[] t0 = new Color[w * h * d];
                    DownsampleMonoL0(snapshots[s], w * h * d, t0);
                    CopyBlock(pixels, t0, w, totalHeight, h, d, yOffset, 0);
                    break;
                }
            }
        }

        // UNORM formats need signed SH values remapped from [-1,1] to [0,1] for storage.
        // The shader decodes back with value * 2 - 1.
        // RGB48 (MonoL1+Depth16) is also UNORM, so it needs the same remap as the Depth8 cases.
        bool isUnorm = ALVFormat.IsUnorm(shMode, bitDepth);
        if (isUnorm)
        {
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color(
                    pixels[i].r * 0.5f + 0.5f,
                    pixels[i].g * 0.5f + 0.5f,
                    pixels[i].b * 0.5f + 0.5f,
                    pixels[i].a * 0.5f + 0.5f);
        }

        // MonoL1 slots have zeroed alpha, so RGB formats fit and save one channel per slot.
        // All other modes need alpha (L1 stores Lx.z there; MonoL0 stores L1.z in alpha).
        TextureFormat texFormat = (shMode, bitDepth) switch
        {
            (ALVSHMode.MonoL1, ALVBitDepth.Depth8)  => TextureFormat.RGB24,
            (ALVSHMode.MonoL1, ALVBitDepth.Depth16) => TextureFormat.RGB48,
            (_,                ALVBitDepth.Depth8)  => TextureFormat.RGBA32,
            _                                       => TextureFormat.RGBAHalf,
        };

        // Reuse existing asset to preserve GUID.
        // Prevents serialised field linkage breaking whenever the texture gets updated.
        Texture3D existing = AssetDatabase.LoadAssetAtPath<Texture3D>(assetPath);
        if (existing != null && existing.width == w && existing.height == totalHeight
            && existing.depth == totalDepth && existing.format == texFormat)
        {
            existing.SetPixels(pixels);
            existing.Apply();
            EditorUtility.SetDirty(existing);
        }
        else
        {
            Texture3D tex = new Texture3D(w, totalHeight, totalDepth, texFormat, false);
            tex.wrapMode   = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.SetPixels(pixels);
            tex.Apply();

            if (existing != null)
                AssetDatabase.DeleteAsset(assetPath);

            AssetDatabase.CreateAsset(tex, assetPath);
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[GenerateALVTexture] Saved {assetPath} ({w}x{totalHeight}x{totalDepth}, {numSnapshots} snapshots, {shMode}, {bitDepth})");
    }

    // MonoL1: collapses the three per-channel L1 direction vectors into one shared direction,
    // weighted by each channel's L0 intensity so brighter channels pull the direction more.
    static void DownsampleMonoL1(SnapshotSH snapshot, int count, Color[] t0Out, Color[] t1Out)
    {
        for (int i = 0; i < count; i++)
        {
            float l0r = snapshot.tex0[i].r, l0g = snapshot.tex0[i].g, l0b = snapshot.tex0[i].b;

            // Reconstruct per-channel L1 vectors from the full packing layout:
            //   tex0.a = L1r.z, tex1 = (L1r.x, L1g.x, L1b.x, L1g.z),
            //   tex2 = (L1r.y, L1g.y, L1b.y, L1b.z)
            Vector3 L1r = new Vector3(snapshot.tex1[i].r, snapshot.tex2[i].r, snapshot.tex0[i].a);
            Vector3 L1g = new Vector3(snapshot.tex1[i].g, snapshot.tex2[i].g, snapshot.tex1[i].a);
            Vector3 L1b = new Vector3(snapshot.tex1[i].b, snapshot.tex2[i].b, snapshot.tex2[i].a);

            float denom = l0r + l0g + l0b;
            Vector3 L1mono = denom > 1e-6f
                ? (l0r * L1r + l0g * L1g + l0b * L1b) / denom
                : Vector3.zero;

            t0Out[i] = new Color(l0r, l0g, l0b, 0f);
            t1Out[i] = new Color(L1mono.x, L1mono.y, L1mono.z, 0f);
        }
    }

    // MonoL0: collapses to a single intensity value (max of L0.rgb) plus shared L1 direction.
    static void DownsampleMonoL0(SnapshotSH snapshot, int count, Color[] t0Out)
    {
        for (int i = 0; i < count; i++)
        {
            float l0r = snapshot.tex0[i].r, l0g = snapshot.tex0[i].g, l0b = snapshot.tex0[i].b;
            float l0  = Mathf.Max(l0r, Mathf.Max(l0g, l0b));

            Vector3 L1r = new Vector3(snapshot.tex1[i].r, snapshot.tex2[i].r, snapshot.tex0[i].a);
            Vector3 L1g = new Vector3(snapshot.tex1[i].g, snapshot.tex2[i].g, snapshot.tex1[i].a);
            Vector3 L1b = new Vector3(snapshot.tex1[i].b, snapshot.tex2[i].b, snapshot.tex2[i].a);

            float denom = l0r + l0g + l0b;
            Vector3 L1mono = denom > 1e-6f
                ? (l0r * L1r + l0g * L1g + l0b * L1b) / denom
                : Vector3.zero;

            t0Out[i] = new Color(l0, L1mono.x, L1mono.y, L1mono.z);
        }
    }

    // Forwards to ALVFormat.BytesPerTexel; kept here for call-site compatibility.
    public static int BytesPerTexel(ALVSHMode shMode, ALVBitDepth bitDepth) =>
        ALVFormat.BytesPerTexel(shMode, bitDepth);

    // Applies SH dering per voxel to suppress L1 ringing from area lights.
    // Mirrors LVUtils.DeringSingleSH: clamps each channel's L1 magnitude to L0 * 1.13.
    public static SnapshotSH DeringSnapshot(Color[] t0, Color[] t1, Color[] t2)
    {
        int count = t0.Length;
        Color[] r0 = new Color[count], r1 = new Color[count], r2 = new Color[count];
        for (int i = 0; i < count; i++)
        {
            // Packing layout:
            //   tex0: (L0.r, L0.g, L0.b, L1r.z)
            //   tex1: (L1r.x, L1g.x, L1b.x, L1g.z)
            //   tex2: (L1r.y, L1g.y, L1b.y, L1b.z)
            Vector3 L1r = LVUtils.DeringSingleSH(t0[i].r, new Vector3(t1[i].r, t2[i].r, t0[i].a));
            Vector3 L1g = LVUtils.DeringSingleSH(t0[i].g, new Vector3(t1[i].g, t2[i].g, t1[i].a));
            Vector3 L1b = LVUtils.DeringSingleSH(t0[i].b, new Vector3(t1[i].b, t2[i].b, t2[i].a));
            r0[i] = new Color(t0[i].r, t0[i].g, t0[i].b, L1r.z);
            r1[i] = new Color(L1r.x,   L1g.x,   L1b.x,   L1g.z);
            r2[i] = new Color(L1r.y,   L1g.y,   L1b.y,   L1b.z);
        }
        return new SnapshotSH { tex0 = r0, tex1 = r1, tex2 = r2 };
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

    // One baked snapshot: three SH textures as flat Color arrays, length w*h*d, XYZ order (x fastest).
    public struct SnapshotSH
    {
        public Color[] tex0, tex1, tex2;
    }
}
