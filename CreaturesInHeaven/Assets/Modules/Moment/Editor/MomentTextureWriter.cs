using UnityEngine;
using UnityEditor;
using VRCLightVolumes;

// Texture generation tool for the Moment module.
// Produces a packed Texture3D with layout:
//   X = spatial width
//   Y = spatial height * numSnapshots  (snapshot 0 at Y=0, snapshot 1 at Y=H, etc.)
//   Z = spatial depth * numSlots       (numSlots = 3 for L1, 2 for MonoL1, 1 for MonoL0)
//
// SH packing per voxel:
//   Tex0: (L0.r,  L0.g,  L0.b,  L1r.z)
//   Tex1: (L1r.x, L1g.x, L1b.x, L1g.z)
//   Tex2: (L1r.y, L1g.y, L1b.y, L1b.z)
public static class MomentTextureWriter
{
    // Builds a packed Texture3D from an array of SnapshotSH and saves it at the given asset path.
    // Each SnapshotSH.tex0/1/2 must be a Color[] of length w*h*d in XYZ order (x fastest).
    // If an asset already exists at that path, its pixel data is updated in place so serialised
    // references (GUIDs) remain stable.
    public static void SavePackedTexture(SnapshotSH[] snapshots, int w, int h, int d, string assetPath,
        MomentALVSHMode shMode = MomentALVSHMode.L1, MomentALVBitDepth bitDepth = MomentALVBitDepth.Depth8)
    {
        int numSnapshots = snapshots.Length;
        int totalHeight  = MomentALVFormat.PackedHeight(h, numSnapshots);
        int totalDepth   = MomentALVFormat.PackedDepth(d, shMode);

        Color[] pixels = new Color[w * totalHeight * totalDepth];

        for (int s = 0; s < numSnapshots; s++)
        {
            int yOffset = s * h;
            switch (shMode)
            {
                case MomentALVSHMode.L1:
                    // Full: 3 slots: tex0/1/2 passed through as-is.
                    CopyBlock(pixels, snapshots[s].tex0, w, totalHeight, h, d, yOffset, 0);
                    CopyBlock(pixels, snapshots[s].tex1, w, totalHeight, h, d, yOffset, d);
                    CopyBlock(pixels, snapshots[s].tex2, w, totalHeight, h, d, yOffset, d * 2);
                    break;

                case MomentALVSHMode.MonoL1:
                {
                    // 2 slots: Tex0 = (L0.r, L0.g, L0.b, 0), Tex1 = (L1.x, L1.y, L1.z, 0).
                    Color[] t0 = new Color[w * h * d];
                    Color[] t1 = new Color[w * h * d];
                    DownsampleMonoL1(snapshots[s], w * h * d, t0, t1);
                    CopyBlock(pixels, t0, w, totalHeight, h, d, yOffset, 0);
                    CopyBlock(pixels, t1, w, totalHeight, h, d, yOffset, d);
                    break;
                }

                case MomentALVSHMode.MonoL0:
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
        // TODO (8bpc): L0 channels are always non-negative so the uniform remap wastes half their
        // precision on the negative half they never use. Investigate storing L0 raw and only
        // remapping L1 channels, with matching per-channel decode in the shader.
        bool isUnorm = MomentALVFormat.IsUnorm(shMode, bitDepth);
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
            (MomentALVSHMode.MonoL1, MomentALVBitDepth.Depth8)  => TextureFormat.RGB24,
            (MomentALVSHMode.MonoL1, MomentALVBitDepth.Depth16) => TextureFormat.RGB48,
            (_,                MomentALVBitDepth.Depth8)  => TextureFormat.RGBA32,
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
        Debug.Log($"[Moment] Saved {assetPath} ({w}x{totalHeight}x{totalDepth}, {numSnapshots} snapshots, {shMode}, {bitDepth})");
    }

    // Creates or replaces a blank (zeroed) Texture3D atlas at assetPath with the correct packed dimensions.
    // Called at bake start so the asset exists immediately and partial results are playable at runtime.
    public static Texture3D InitialiseTexture(int w, int h, int d, int numSnapshots,
        MomentALVSHMode shMode, MomentALVBitDepth bitDepth, string assetPath)
    {
        int totalHeight = MomentALVFormat.PackedHeight(h, numSnapshots);
        int totalDepth  = MomentALVFormat.PackedDepth(d, shMode);

        TextureFormat texFormat = (shMode, bitDepth) switch
        {
            (MomentALVSHMode.MonoL1, MomentALVBitDepth.Depth8)  => TextureFormat.RGB24,
            (MomentALVSHMode.MonoL1, MomentALVBitDepth.Depth16) => TextureFormat.RGB48,
            (_,                MomentALVBitDepth.Depth8)  => TextureFormat.RGBA32,
            _                                       => TextureFormat.RGBAHalf,
        };

        Texture3D existing = AssetDatabase.LoadAssetAtPath<Texture3D>(assetPath);
        if (existing != null)
            AssetDatabase.DeleteAsset(assetPath);

        Texture3D tex = new Texture3D(w, totalHeight, totalDepth, texFormat, false);
        tex.wrapMode   = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        // UNORM formats store SH values remapped to [0,1]; zero storage decodes as -1 in the shader.
        // Fill with 0.5 so unbaked slices decode to 0 (no light contribution) rather than -1.
        if (MomentALVFormat.IsUnorm(shMode, bitDepth))
        {
            Color blank = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            Color[] pixels = new Color[w * totalHeight * totalDepth];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = blank;
            tex.SetPixels(pixels);
        }

        tex.Apply();
        AssetDatabase.CreateAsset(tex, assetPath);
        AssetDatabase.SaveAssets();
        return tex;
    }

    // Writes a single snapshot's SH data into the correct Y-slice of an existing packed atlas asset.
    // snapshotIndex is 0-based; the atlas must already exist with the correct packed dimensions.
    public static void WriteSnapshotSlice(string assetPath, SnapshotSH snapshot, int snapshotIndex,
        int w, int h, int d, MomentALVSHMode shMode, MomentALVBitDepth bitDepth)
    {
        Texture3D tex = AssetDatabase.LoadAssetAtPath<Texture3D>(assetPath);
        if (tex == null)
        {
            Debug.LogError($"[Moment] WriteSnapshotSlice: asset not found at {assetPath}");
            return;
        }

        int totalHeight = tex.height;
        int yOffset     = snapshotIndex * h;
        bool isUnorm    = MomentALVFormat.IsUnorm(shMode, bitDepth);

        Color[] pixels = tex.GetPixels();

        void PasteBlock(Color[] src, int zOffset)
        {
            for (int z = 0; z < d; z++)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        Color c = src[x + y * w + z * w * h];
                        if (isUnorm) c = new Color(c.r * 0.5f + 0.5f, c.g * 0.5f + 0.5f, c.b * 0.5f + 0.5f, c.a * 0.5f + 0.5f);
                        pixels[x + (y + yOffset) * w + (z + zOffset) * w * totalHeight] = c;
                    }
        }

        switch (shMode)
        {
            case MomentALVSHMode.L1:
                PasteBlock(snapshot.tex0, 0);
                PasteBlock(snapshot.tex1, d);
                PasteBlock(snapshot.tex2, d * 2);
                break;

            case MomentALVSHMode.MonoL1:
            {
                Color[] t0 = new Color[w * h * d];
                Color[] t1 = new Color[w * h * d];
                DownsampleMonoL1(snapshot, w * h * d, t0, t1);
                PasteBlock(t0, 0);
                PasteBlock(t1, d);
                break;
            }

            case MomentALVSHMode.MonoL0:
            {
                Color[] t0 = new Color[w * h * d];
                DownsampleMonoL0(snapshot, w * h * d, t0);
                PasteBlock(t0, 0);
                break;
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();
        EditorUtility.SetDirty(tex);
        AssetDatabase.SaveAssets();
    }

    // MonoL1: collapses the three per-channel L1 direction vectors into one shared direction,
    // weighted by each channel's L0 intensity so brighter channels pull the direction more.
    static void DownsampleMonoL1(SnapshotSH snapshot, int count, Color[] t0Out, Color[] t1Out)
    {
        for (int i = 0; i < count; i++)
        {
            float l0r = snapshot.tex0[i].r, l0g = snapshot.tex0[i].g, l0b = snapshot.tex0[i].b;
            var (L1r, L1g, L1b) = UnpackL1Channels(snapshot.tex0[i], snapshot.tex1[i], snapshot.tex2[i]);
            Vector3 L1mono = WeightedMonoL1(l0r, l0g, l0b, L1r, L1g, L1b);
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
            var (L1r, L1g, L1b) = UnpackL1Channels(snapshot.tex0[i], snapshot.tex1[i], snapshot.tex2[i]);
            Vector3 L1mono = WeightedMonoL1(l0r, l0g, l0b, L1r, L1g, L1b);
            t0Out[i] = new Color(l0, L1mono.x, L1mono.y, L1mono.z);
        }
    }

    // Blends three per-channel L1 vectors into one shared direction, weighted by each
    // channel's L0 intensity. Returns zero when all channels are dark.
    static Vector3 WeightedMonoL1(float l0r, float l0g, float l0b, Vector3 L1r, Vector3 L1g, Vector3 L1b)
    {
        float denom = l0r + l0g + l0b;
        return denom > 1e-6f ? (l0r * L1r + l0g * L1g + l0b * L1b) / denom : Vector3.zero;
    }

    // Forwards to MomentALVFormat.BytesPerTexel; kept here for call-site compatibility.
    public static int BytesPerTexel(MomentALVSHMode shMode, MomentALVBitDepth bitDepth) =>
        MomentALVFormat.BytesPerTexel(shMode, bitDepth);

    // Applies SH dering per voxel to suppress L1 ringing from area lights.
    // Mirrors LVUtils.DeringSingleSH: clamps each channel's L1 magnitude to L0 * 1.13.
    public static SnapshotSH DeringSnapshot(Color[] t0, Color[] t1, Color[] t2)
    {
        int count = t0.Length;
        Color[] r0 = new Color[count], r1 = new Color[count], r2 = new Color[count];
        for (int i = 0; i < count; i++)
        {
            var (rawL1r, rawL1g, rawL1b) = UnpackL1Channels(t0[i], t1[i], t2[i]);
            Vector3 L1r = LVUtils.DeringSingleSH(t0[i].r, rawL1r);
            Vector3 L1g = LVUtils.DeringSingleSH(t0[i].g, rawL1g);
            Vector3 L1b = LVUtils.DeringSingleSH(t0[i].b, rawL1b);
            r0[i] = new Color(t0[i].r, t0[i].g, t0[i].b, L1r.z);
            r1[i] = new Color(L1r.x,   L1g.x,   L1b.x,   L1g.z);
            r2[i] = new Color(L1r.y,   L1g.y,   L1b.y,   L1b.z);
        }
        return new SnapshotSH { tex0 = r0, tex1 = r1, tex2 = r2 };
    }

    // Reconstructs the three per-channel L1 direction vectors from the full SH packing layout:
    //   tex0.a = L1r.z, tex1 = (L1r.x, L1g.x, L1b.x, L1g.z), tex2 = (L1r.y, L1g.y, L1b.y, L1b.z)
    static (Vector3 L1r, Vector3 L1g, Vector3 L1b) UnpackL1Channels(Color t0, Color t1, Color t2) =>
    (
        new Vector3(t1.r, t2.r, t0.a),
        new Vector3(t1.g, t2.g, t1.a),
        new Vector3(t1.b, t2.b, t2.a)
    );

    // Zeroes a single snapshot's Y-slice in an existing packed atlas asset.
    // For UNORM formats, fills with 0.5 so the slice decodes to zero in the shader (matching InitialiseTexture).
    public static void ClearSnapshotSlice(string assetPath, int snapshotIndex,
        int w, int h, int d, MomentALVSHMode shMode, MomentALVBitDepth bitDepth)
    {
        Texture3D tex = AssetDatabase.LoadAssetAtPath<Texture3D>(assetPath);
        if (tex == null)
        {
            Debug.LogError($"[Moment] ClearSnapshotSlice: asset not found at {assetPath}");
            return;
        }

        int totalHeight = tex.height;
        int yOffset     = snapshotIndex * h;
        int numSlots    = MomentALVFormat.NumSlots(shMode);
        bool isUnorm    = MomentALVFormat.IsUnorm(shMode, bitDepth);
        Color blank     = isUnorm ? new Color(0.5f, 0.5f, 0.5f, 0.5f) : Color.clear;

        Color[] pixels = tex.GetPixels();

        for (int slot = 0; slot < numSlots; slot++)
        {
            int zOffset = slot * d;
            for (int z = 0; z < d; z++)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        pixels[x + (y + yOffset) * w + (z + zOffset) * w * totalHeight] = blank;
        }

        tex.SetPixels(pixels);
        tex.Apply();
        EditorUtility.SetDirty(tex);
        AssetDatabase.SaveAssets();
    }

    // Reads one voxel from the packed pixel array and returns it in the full L1 atlas layout:
    //   sh0 = (L0.r, L0.g, L0.b, L1r.z)
    //   sh1 = (L1r.x, L1g.x, L1b.x, L1g.z)
    //   sh2 = (L1r.y, L1g.y, L1b.y, L1b.z)
    // This is the same layout the preview shader and the atlas itself use, so callers don't
    // need to know which SH mode was baked. They always get full L1 data back.
    //
    // pixels:       flat array from Texture3D.GetPixels(), XYZ order (x fastest)
    // texSize:      full packed texture dimensions (width, height, depth)
    // snapshotSize: spatial dimensions of one snapshot (x, y, z/depth per slot)
    // snapshotOrigin: Y offset in pixels of the target snapshot (= snapshotIndex * snapshotSize.y)
    // voxelX/Y/Z:   spatial voxel coordinates within the snapshot
    // shMode:       the mode the texture was baked in
    // bitDepth:     the bit depth the texture was baked in (used for UNORM decode)
    public static void DecodeVoxel(
        Color[] pixels, Vector3Int texSize, Vector3Int snapshotSize, int snapshotOrigin,
        int voxelX, int voxelY, int voxelZ,
        MomentALVSHMode shMode, MomentALVBitDepth bitDepth,
        out Vector4 sh0, out Vector4 sh1, out Vector4 sh2)
    {
        // Read the raw slots that exist for this mode.
        Color s0 = ReadTexel(pixels, texSize, voxelX, voxelY + snapshotOrigin, voxelZ);
        Color s1 = MomentALVFormat.NumSlots(shMode) >= 2
            ? ReadTexel(pixels, texSize, voxelX, voxelY + snapshotOrigin, voxelZ + snapshotSize.z)
            : new Color(0, 0, 0, 0);
        Color s2 = MomentALVFormat.NumSlots(shMode) >= 3
            ? ReadTexel(pixels, texSize, voxelX, voxelY + snapshotOrigin, voxelZ + snapshotSize.z * 2)
            : new Color(0, 0, 0, 0);

        // UNORM decode: values were remapped [−1,1]→[0,1] at bake time; invert that here.
        if (MomentALVFormat.IsUnorm(shMode, bitDepth))
        {
            s0 = new Color(s0.r * 2 - 1, s0.g * 2 - 1, s0.b * 2 - 1, s0.a * 2 - 1);
            s1 = new Color(s1.r * 2 - 1, s1.g * 2 - 1, s1.b * 2 - 1, s1.a * 2 - 1);
            s2 = new Color(s2.r * 2 - 1, s2.g * 2 - 1, s2.b * 2 - 1, s2.a * 2 - 1);
        }

        // Expand into the full L1 atlas layout the preview shader expects.
        switch (shMode)
        {
            case MomentALVSHMode.L1:
                // Slots are already in atlas layout; pass through directly.
                sh0 = new Vector4(s0.r, s0.g, s0.b, s0.a);
                sh1 = new Vector4(s1.r, s1.g, s1.b, s1.a);
                sh2 = new Vector4(s2.r, s2.g, s2.b, s2.a);
                break;

            case MomentALVSHMode.MonoL1:
            {
                // s0 = (L0.r, L0.g, L0.b, 0),  s1 = (L1.x, L1.y, L1.z, 0)
                float l1x = s1.r, l1y = s1.g, l1z = s1.b;
                sh0 = new Vector4(s0.r, s0.g, s0.b, l1z);
                sh1 = new Vector4(l1x,  l1x,  l1x,  l1z);
                sh2 = new Vector4(l1y,  l1y,  l1y,  l1z);
                break;
            }

            default: // MonoL0
            {
                // s0 = (L0, L1.x, L1.y, L1.z)
                float l0 = s0.r, l1x = s0.g, l1y = s0.b, l1z = s0.a;
                sh0 = new Vector4(l0,  l0,  l0,  l1z);
                sh1 = new Vector4(l1x, l1x, l1x, l1z);
                sh2 = new Vector4(l1y, l1y, l1y, l1z);
                break;
            }
        }
    }

    // Reads one texel from a flat Texture3D pixel array (XYZ order, x fastest).
    static Color ReadTexel(Color[] pixels, Vector3Int texSize, int x, int y, int z) =>
        pixels[x + y * texSize.x + z * texSize.x * texSize.y];

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
