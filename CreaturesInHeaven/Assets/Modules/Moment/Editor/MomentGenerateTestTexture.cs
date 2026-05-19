using UnityEngine;
using UnityEditor;
using System;

// Generates a set of packed Moment textures covering all supported format combinations.
// Useful for measuring real AssetBundle compression ratios against the 0.315 estimate.
public static class MomentGenerateTestTexture
{
    const int W = 4, H = 4, D = 4;
    const int NumSnapshots = 64;

    [MenuItem("Tools/Lighting/Generate Moment format test textures")]
    static void GenerateAll()
    {
        string assetDir = MomentAssetPaths.SceneAssetDir() + "/FormatTest";
        MomentAssetPaths.CreateDirectory(assetDir);

        var rng = new System.Random(9);

        // Generate NumSnapshots of Gaussian-blob SH data, shared across all format variants.
        var snapshots = GaussianSnapshots(W, H, D, NumSnapshots, rng);

        var report = new System.Text.StringBuilder();
        report.AppendLine($"Moment format test — {W}x{H}x{D}, {NumSnapshots} snapshots (Gaussian blobs)");
        report.AppendLine($"{DateTime.Now}");
        report.AppendLine();
        report.AppendLine(new string('-', 75));
        report.AppendLine();

        var generated = new System.Collections.Generic.List<(string name, string path, double vram)>();

        foreach (MomentALVSHMode shMode in System.Enum.GetValues(typeof(MomentALVSHMode)))
        {
            foreach (MomentALVBitDepth bitDepth in System.Enum.GetValues(typeof(MomentALVBitDepth)))
            {
                string name = $"Moment_Test_{shMode}_{(bitDepth == MomentALVBitDepth.Depth8 ? "8bpc" : "16bpc")}";
                string path = $"{assetDir}/{name}.asset";
                MomentTextureWriter.SavePackedTexture(snapshots, W, H, D, path, shMode, bitDepth);

                double vram = MomentALVFormat.VramMB(W, H, D, NumSnapshots, shMode, bitDepth);
                generated.Add((name, path, vram));
            }
        }

        AssetDatabase.Refresh();

        // Assign each asset to its own named bundle so we get one file per format.
        string bundleName = "alv-format-test";
        foreach (var (_, path, _) in generated)
        {
            var importer = AssetImporter.GetAtPath(path);
            if (importer != null) importer.SetAssetBundleNameAndVariant(bundleName, "");
        }

        // Build bundles into a temp directory, then read the sizes back.
        string bundleOutDir = System.IO.Path.Combine(
            Application.dataPath, "..", "Temp", "MomentFormatTestBundles");
        System.IO.Directory.CreateDirectory(bundleOutDir);

        // Each asset gets its own bundle by building them individually so sizes are per-format.
        // BuildAssetBundles with a single shared name would merge them all into one file.
        var bundleSizes = new System.Collections.Generic.Dictionary<string, double>();
        foreach (var (name, path, _) in generated)
        {
            string perName = $"alv-test-{name.ToLower()}";
            var imp = AssetImporter.GetAtPath(path);
            if (imp != null) imp.SetAssetBundleNameAndVariant(perName, "");
            var builds = new[]
            {
                new AssetBundleBuild
                {
                    assetBundleName = perName,
                    assetNames      = new[] { path },
                }
            };
            BuildPipeline.BuildAssetBundles(bundleOutDir, builds,
                BuildAssetBundleOptions.None, EditorUserBuildSettings.activeBuildTarget);

            string bundleFile = System.IO.Path.Combine(bundleOutDir, perName);
            bundleSizes[name] = System.IO.File.Exists(bundleFile)
                ? new System.IO.FileInfo(bundleFile).Length / (1024.0 * 1024.0)
                : -1;

            // Clear the bundle name assignment.
            var impClean = AssetImporter.GetAtPath(path);
            if (impClean != null) impClean.SetAssetBundleNameAndVariant("", "");
        }

        AssetDatabase.RemoveUnusedAssetBundleNames();

        foreach (var (name, path, vram) in generated)
        {
            Texture3D tex = AssetDatabase.LoadAssetAtPath<Texture3D>(path);
            string dims   = tex != null ? $"{tex.width} * {tex.height} * {tex.depth}" : "not found";
            string format = tex != null ? tex.format.ToString() : "not found";

            double bundleMb = bundleSizes.TryGetValue(name, out double b) ? b : -1;
            string bundleStr = bundleMb >= 0 ? $"{bundleMb:0.000}" : "not found";
            string ratioStr  = bundleMb >= 0 && vram > 0 ? $"{bundleMb / vram:0.000}" : "—";

            report.AppendLine($"{name}");
            report.AppendLine($"  Output dimensions:           {dims}");
            report.AppendLine($"  Output texture format:       {format}");
            report.AppendLine($"  Expected VRAM size (MB):     {vram:0.000}");
            report.AppendLine($"  Expected bundle size (MB):   {vram * 0.315:0.000}");
            report.AppendLine($"  Actual bundle size (MB):     {bundleStr}");
            report.AppendLine($"  Actual compression ratio:    {ratioStr}");
            report.AppendLine();
        }

        string reportPath = $"{assetDir}/FormatTestReport-{DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss")}.txt";
        string absReportPath = System.IO.Path.Combine(
            Application.dataPath, "..", reportPath).Replace('\\', '/');
        System.IO.File.WriteAllText(absReportPath, report.ToString());
        AssetDatabase.ImportAsset(reportPath);
        Debug.Log($"[Moment] Format test report written to {reportPath}");
    }

    // Generates numSnapshots of SH data mixing Gaussian point lights with static dark zones.
    // Lights pulse sinusoidally for temporal variation; dark zones are fixed in space,
    // producing stretches of near-zero values that mimic shadowed or unlit regions.
    static MomentTextureWriter.SnapshotSH[] GaussianSnapshots(int w, int h, int d, int numSnapshots, System.Random rng)
    {
        const int   numLights        = 8;
        const int   numBlockers      = 3;
        // 0 = no darkening at blocker centre, 1 = fully black. Overlapping blockers multiply.
        const float blockerStrength  = 0.9f;
        // Blocker radius range as a fraction of the longest volume dimension.
        const float blockerRadiusMin = 0.10f;
        const float blockerRadiusMax = 0.20f;
        float maxDim = Mathf.Max(w, Mathf.Max(h, d));

        var lightPos    = new Vector3[numLights];
        var lightColor  = new Vector3[numLights];
        var lightRadius = new float[numLights];
        var lightPhase  = new float[numLights];

        for (int i = 0; i < numLights; i++)
        {
            lightPos[i]    = new Vector3(Rand01(rng) * w, Rand01(rng) * h, Rand01(rng) * d);
            lightColor[i]  = new Vector3(Rand01(rng), Rand01(rng), Rand01(rng));
            lightRadius[i] = Mathf.Max(1.5f, maxDim * (0.15f + 0.35f * Rand01(rng)));
            lightPhase[i]  = Rand01(rng) * 2 * Mathf.PI;
        }

        // Blockers are wider than lights and static. They carve out spatially coherent
        // dark regions without varying per-snapshot, which is realistic for shadowed areas.
        var blockerPos    = new Vector3[numBlockers];
        var blockerRadius = new float[numBlockers];

        for (int i = 0; i < numBlockers; i++)
        {
            blockerPos[i]    = new Vector3(Rand01(rng) * w, Rand01(rng) * h, Rand01(rng) * d);
            blockerRadius[i] = Mathf.Max(2f, maxDim * Mathf.Lerp(blockerRadiusMin, blockerRadiusMax, Rand01(rng)));
        }

        var snapshots = new MomentTextureWriter.SnapshotSH[numSnapshots];
        for (int snap = 0; snap < numSnapshots; snap++)
        {
            float t     = numSnapshots > 1 ? (float)snap / numSnapshots : 0f;
            int   count = w * h * d;
            Color[] tex0 = new Color[count];
            Color[] tex1 = new Color[count];
            Color[] tex2 = new Color[count];

            for (int z = 0; z < d; z++)
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int     idx = z * h * w + y * w + x;
                Vector3 vp  = new(x, y, z);

                float   L0r = 0, L0g = 0, L0b = 0;
                Vector3 L1r = Vector3.zero, L1g = Vector3.zero, L1b = Vector3.zero;

                for (int i = 0; i < numLights; i++)
                {
                    float   inten   = 0.5f + 0.5f * Mathf.Sin(2 * Mathf.PI * t + lightPhase[i]);
                    float   radius  = lightRadius[i];
                    Vector3 delta   = vp - lightPos[i];
                    float   contrib = Mathf.Exp(-delta.sqrMagnitude / (2 * radius * radius)) * inten;
                    // Direction from voxel toward light (convention for incoming SH).
                    Vector3 dir = delta.sqrMagnitude > 1e-6f ? (-delta).normalized : Vector3.zero;

                    L0r += lightColor[i].x * contrib;
                    L0g += lightColor[i].y * contrib;
                    L0b += lightColor[i].z * contrib;
                    L1r += dir * (lightColor[i].x * contrib);
                    L1g += dir * (lightColor[i].y * contrib);
                    L1b += dir * (lightColor[i].z * contrib);
                }

                // Normalise so the brightest channel stays in [0,1]; scale L1 by the same
                // factor so |L1| ≤ L0 is preserved (triangle inequality guarantees it holds
                // before normalisation, and uniform scaling keeps the ratio intact).
                float maxL0 = Mathf.Max(L0r, Mathf.Max(L0g, L0b));
                if (maxL0 > 1f)
                {
                    float s = 1f / maxL0;
                    L0r *= s; L0g *= s; L0b *= s;
                    L1r *= s; L1g *= s; L1b *= s;
                }

                // Shadow mask: each blocker multiplies the mask by (1 - strength * gaussian).
                // Multiplicative form keeps the result in [0,1] without clamping, so overlapping
                // blockers darken further rather than overcounting and snapping to zero.
                // Applied after normalisation so the two systems don't interfere.
                float shadow = 1f;
                for (int i = 0; i < numBlockers; i++)
                {
                    float r = blockerRadius[i];
                    Vector3 delta = vp - blockerPos[i];
                    shadow *= 1f - blockerStrength * Mathf.Exp(-delta.sqrMagnitude / (2 * r * r));
                }

                L0r *= shadow; L0g *= shadow; L0b *= shadow;
                L1r *= shadow; L1g *= shadow; L1b *= shadow;

                tex0[idx] = new Color(L0r,   L0g,   L0b,   L1r.z);
                tex1[idx] = new Color(L1r.x, L1g.x, L1b.x, L1g.z);
                tex2[idx] = new Color(L1r.y, L1g.y, L1b.y, L1b.z);
            }

            snapshots[snap] = new MomentTextureWriter.SnapshotSH { tex0 = tex0, tex1 = tex1, tex2 = tex2 };
        }

        return snapshots;
    }

    static float Rand01(System.Random rng) => (float)rng.NextDouble();
}
