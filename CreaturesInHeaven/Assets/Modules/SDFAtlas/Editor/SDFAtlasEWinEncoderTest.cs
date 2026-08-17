using System.IO;
using UnityEngine;
using UnityEditor;

// Step 1 validation harness for the SDFAtlas encoder.
//
// Encodes a single source texture and writes a contact sheet PNG showing every stage of
// the pipeline side by side, so encoder quality can be judged by eye before anything is
// built on top of it. This window is a development tool, not part of the shipping
// authoring workflow -- the real atlas builder comes later.
//
// The question this exists to answer: does single-channel SDF hold up on this artwork at
// this cell size, or do corners round off badly enough to need multi-channel?
//
// Opens via Tools > SDF Atlas > Encoder test...
public class SDFAtlasEWinEncoderTest : EditorWindow
{
    // --- Window state ---------------------------------------------------

    Texture2D _source;
    SDFAtlasEncoder.Settings _settings = SDFAtlasEncoder.Settings.Default;

    // Magnifications at which the reconstruction is previewed. Chosen to bracket real
    // viewing conditions: 1x is the cell as stored, 8x approximates a sign filling a good
    // part of the screen, 16x is closer than a player is likely to get. Corner rounding
    // becomes obvious as this number climbs.
    static readonly int[] PreviewMagnifications = { 1, 4, 8, 16 };

    // Size of each panel in the contact sheet, in pixels. Every stage is rendered at this
    // size regardless of its native resolution, so panels line up in a grid.
    const int PanelSize = 256;
    const int PanelPadding = 8;

    string _lastOutputPath;
    string _status;
    MessageType _statusType = MessageType.None;

    // --- Window lifecycle -----------------------------------------------

    [MenuItem("Tools/SDF Atlas/Encoder test...")]
    static void Open() => GetWindow<SDFAtlasEWinEncoderTest>("SDF encoder test");

    void OnGUI()
    {
        EditorGUILayout.LabelField("Step 1: encoder validation", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Encodes one source texture and writes a contact sheet showing the coverage mask, " +
            "the distance field, and thresholded reconstructions at several magnifications.\n\n" +
            "Source must be readable, uncompressed, and at full resolution " +
            "(Read/Write Enabled on, Compression None, Max Size >= native).",
            MessageType.Info);

        EditorGUILayout.Space();

        _source = (Texture2D)EditorGUILayout.ObjectField("Source texture", _source, typeof(Texture2D), false);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Encoding", EditorStyles.boldLabel);

        _settings.channel = (SDFAtlasEncoder.CoverageChannel)EditorGUILayout.EnumPopup(
            new GUIContent("Coverage channel",
                "Which channel carries the shape. Alpha for this project's signage art -- " +
                "Photoshop writes white into the RGB of transparent pixels, so RGB is unreliable."),
            _settings.channel);

        _settings.cellSize = EditorGUILayout.IntPopup(
            new GUIContent("Cell size", "Encoded resolution, in texels."),
            _settings.cellSize,
            new[] { new GUIContent("32"), new GUIContent("64"), new GUIContent("128"), new GUIContent("256") },
            new[] { 32, 64, 128, 256 });

        _settings.spreadPixels = EditorGUILayout.Slider(
            new GUIContent("Spread (cell texels)",
                "Distance range mapped to 0..1. Too small clips the gradient the shader " +
                "antialiases with; too large makes 8-bit quantisation band the edge."),
            _settings.spreadPixels, 1f, 16f);

        _settings.coverageThreshold = EditorGUILayout.Slider(
            new GUIContent("Coverage threshold", "Coverage at or above this counts as inside."),
            _settings.coverageThreshold, 0.01f, 0.99f);

        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(_source == null))
        {
            if (GUILayout.Button("Encode and write contact sheet", GUILayout.Height(30)))
                Run();
        }

        if (!string.IsNullOrEmpty(_status))
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(_status, _statusType);
        }

        if (!string.IsNullOrEmpty(_lastOutputPath) && GUILayout.Button("Select output in Project"))
        {
            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(_lastOutputPath);
            if (asset != null) Selection.activeObject = asset;
        }
    }

    // --- Encode + report -------------------------------------------------

    void Run()
    {
        if (!ValidateSource()) return;

        try
        {
            EditorUtility.DisplayProgressBar("SDF encoder test", "Reading coverage...", 0.1f);
            float[] coverage = SDFAtlasEncoder.ReadCoverage(_source, _settings.channel);

            // Warn rather than silently proceed: an all-opaque alpha channel produces a
            // uniformly-inside mask and a featureless field, which looks like a bug in the
            // transform when it is really a wrong-channel problem.
            if (_settings.channel == SDFAtlasEncoder.CoverageChannel.Alpha &&
                !SDFAtlasEncoder.HasVaryingAlpha(_source))
            {
                Debug.LogWarning(
                    $"[SDFAtlas] '{_source.name}' has a uniform alpha channel, so the Alpha " +
                    "coverage mode will produce an empty field. If this source carries its " +
                    "artwork in RGB, switch the coverage channel accordingly.");
            }

            EditorUtility.DisplayProgressBar("SDF encoder test", "Computing distance field...", 0.3f);
            Texture2D sheet = BuildContactSheet(coverage);

            EditorUtility.DisplayProgressBar("SDF encoder test", "Writing contact sheet...", 0.9f);
            _lastOutputPath = WriteSheet(sheet);
            DestroyImmediate(sheet);

            _status = $"Wrote contact sheet to:\n{_lastOutputPath}\n\n" +
                      $"Source {_source.width}x{_source.height} -> {_settings.cellSize}x{_settings.cellSize} cell, " +
                      $"spread {_settings.spreadPixels} texels.";
            _statusType = MessageType.Info;
        }
        catch (UnityException e)
        {
            // Almost always the unreadable-texture case, which has a specific fix.
            _status = $"Failed to read '{_source.name}'.\n\n{e.Message}\n\n" +
                      "Enable Read/Write in the texture's import settings.";
            _statusType = MessageType.Error;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    // Checks import settings that would silently degrade the encode, and reports them
    // rather than letting them produce quietly-wrong output.
    bool ValidateSource()
    {
        string path = AssetDatabase.GetAssetPath(_source);
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) return true;

        if (!importer.isReadable)
        {
            _status = $"'{_source.name}' is not readable.\n\n" +
                      "Enable Read/Write Enabled in its import settings and re-import.";
            _statusType = MessageType.Error;
            return false;
        }

        if (importer.textureCompression != TextureImporterCompression.Uncompressed)
        {
            Debug.LogWarning(
                $"[SDFAtlas] '{_source.name}' is block-compressed. DXT/BC quantises exactly the " +
                "edge detail the distance transform measures, so the encode will be worse than " +
                "it needs to be. Set Compression to None.");
        }

        return true;
    }

    // --- Contact sheet ---------------------------------------------------

    // Lays out the pipeline stages as a horizontal strip of square panels:
    //
    //   [ coverage mask ] [ distance field ] [ recon 1x ] [ recon 4x ] [ recon 8x ] [ recon 16x ]
    //
    // The mask and field panels are the encoder's internal state, useful for spotting
    // transform bugs. The reconstruction panels are the actual question: they threshold the
    // *downsampled cell* with bilinear filtering, exactly as the shader will, so what you
    // see here is what the shader will produce.
    Texture2D BuildContactSheet(float[] coverage)
    {
        var s = _settings;
        int srcW = _source.width;
        int srcH = _source.height;

        // Panels preserve the source aspect so quality can be judged without distortion.
        // Note this differs from the real atlas cell, which is square by design -- a
        // non-square graphic genuinely does get squashed into its cell and un-squashed by
        // the quad at render time. Squashing the *preview* would only obscure the thing
        // we are here to look at.
        FitAspect(srcW, srcH, out int panelW, out int panelH);

        // Full-resolution intermediates, for the diagnostic panels.
        bool[] mask = SDFAtlasDistanceField.Threshold(coverage, s.coverageThreshold);
        float[] signedDistance = SDFAtlasDistanceField.Compute(mask, srcW, srcH);

        // The encoded cell, at panel aspect rather than squashed square, so the
        // reconstructions show shape quality rather than aspect distortion.
        FitAspect(srcW, srcH, out int cellW, out int cellH);
        cellW = Mathf.Max(1, Mathf.RoundToInt(s.cellSize * (float)cellW / PanelSize));
        cellH = Mathf.Max(1, Mathf.RoundToInt(s.cellSize * (float)cellH / PanelSize));
        float[] cell = SDFAtlasEncoder.EncodeCoverage(coverage, srcW, srcH, s, cellW, cellH);

        int panelCount = 2 + PreviewMagnifications.Length;
        int sheetWidth = panelCount * PanelSize + (panelCount + 1) * PanelPadding;
        int sheetHeight = PanelSize + 2 * PanelPadding;

        var sheet = new Texture2D(sheetWidth, sheetHeight, TextureFormat.RGBA32, false, true);
        Color[] sheetPixels = new Color[sheetWidth * sheetHeight];

        // Mid-grey background so both black and white panel content stays distinguishable
        // against it.
        for (int i = 0; i < sheetPixels.Length; i++) sheetPixels[i] = new Color(0.15f, 0.15f, 0.15f, 1f);

        int panelIndex = 0;

        // Panel 1: the binary coverage mask, downsampled for display only.
        // This is what the transform actually saw. If the shape looks wrong here, the
        // problem is channel selection or threshold, not the distance field.
        {
            float[] maskField = new float[mask.Length];
            for (int i = 0; i < mask.Length; i++) maskField[i] = mask[i] ? 1f : 0f;
            float[] display = SDFAtlasEncoder.Downsample(maskField, srcW, srcH, panelW, panelH);
            BlitPanel(sheetPixels, sheetWidth, sheetHeight, panelIndex++, display, panelW, panelH, Grayscale);
        }

        // Panel 2: the raw signed distance field, remapped for display.
        // Mid-grey is the shape boundary; brighter is inside, darker is outside. A correct
        // field shows smooth gradients radiating from the outline, with no discontinuities
        // away from the edge.
        {
            // Spread is expressed in cell texels, so scale it back up to source pixels to
            // display the full-resolution field with equivalent contrast.
            float sourceSpread = s.spreadPixels * Mathf.Max(srcW, srcH) / Mathf.Max(cellW, cellH);
            float[] encodedFull = SDFAtlasDistanceField.Encode(signedDistance, sourceSpread);
            float[] display = SDFAtlasEncoder.Downsample(encodedFull, srcW, srcH, panelW, panelH);
            BlitPanel(sheetPixels, sheetWidth, sheetHeight, panelIndex++, display, panelW, panelH, Grayscale);
        }

        // Panels 3+: reconstructions of the encoded cell at increasing magnification.
        //
        // Each samples the cell bilinearly (as the GPU would) over a progressively smaller
        // region of it, then applies the same smoothstep the shader uses. Rounding at the
        // chevron tip and jaw spike, if it happens, shows up here as the magnification rises.
        foreach (int magnification in PreviewMagnifications)
        {
            float[] recon = Reconstruct(cell, cellW, cellH, panelW, panelH, magnification);
            BlitPanel(sheetPixels, sheetWidth, sheetHeight, panelIndex++, recon, panelW, panelH, Grayscale);
        }

        sheet.SetPixels(sheetPixels);
        sheet.Apply();

        // Panel order is logged rather than drawn into the image: rendering text without a
        // font atlas means hand-rasterising glyphs, which is a lot of code for a dev tool.
        Debug.Log(
            $"[SDFAtlas] '{_source.name}' {srcW}x{srcH} -> {cellW}x{cellH} encoded cell.\n" +
            $"Contact sheet panels, left to right: coverage mask | distance field | " +
            string.Join(" | ", System.Array.ConvertAll(PreviewMagnifications, m => $"reconstruction {m}x")));

        return sheet;
    }

    // Renders the encoded cell as the shader would, at a given magnification.
    //
    // magnification 1 shows the whole cell; magnification 8 shows the centre eighth blown
    // up to fill the panel. Sampling is bilinear and thresholding uses the same
    // fwidth-style adaptive smoothstep as the shader, so edge softness matches what the
    // GPU produces rather than being arbitrarily chosen here.
    static float[] Reconstruct(float[] cell, int cellWidth, int cellHeight,
                               int panelWidth, int panelHeight, int magnification)
    {
        float[] output = new float[panelWidth * panelHeight];

        // Region of the cell covered by this panel, centred.
        float span = 1f / magnification;
        float origin = 0.5f - span * 0.5f;

        // How much of the field one output pixel spans, used to size the antialiasing
        // window. This is the CPU equivalent of fwidth(d) in the shader.
        float texelStep = span / panelWidth * cellWidth;

        // Stored values are a linear remap of distance, so the encode maps `spread` texels
        // to half the 0..1 range. The antialiasing window must be expressed in those same
        // stored units to stay one output pixel wide.
        float softness = Mathf.Max(texelStep * 0.5f, 1e-4f);

        for (int y = 0; y < panelHeight; y++)
        {
            float v = origin + (y + 0.5f) / panelHeight * span;
            for (int x = 0; x < panelWidth; x++)
            {
                float u = origin + (x + 0.5f) / panelWidth * span;
                float d = SDFAtlasEncoder.SampleBilinear(cell, cellWidth, cellHeight, u, v);

                // Stored value 0.5 is the shape edge; smoothstep across one output pixel.
                output[y * panelWidth + x] = Mathf.SmoothStep(0f, 1f,
                    Mathf.Clamp01((d - (0.5f - softness)) / Mathf.Max(2f * softness, 1e-6f)));
            }
        }
        return output;
    }

    // --- Sheet composition ------------------------------------------------

    delegate Color Colouriser(float value);

    static Color Grayscale(float v) => new Color(v, v, v, 1f);

    // Copies one panel of float values into the sheet at the given slot index.
    //
    // Panels are laid out left to right in fixed-width slots, centred within their slot so
    // that panels narrower than PanelSize (aspect-preserved previews of non-square sources)
    // still line up with their neighbours.
    //
    // No vertical flip happens here: every field in this tool traces back to GetPixels(),
    // which already returns bottom-up data in the same orientation Texture2D expects.
    static void BlitPanel(Color[] sheet, int sheetWidth, int sheetHeight, int panelIndex,
                          float[] panel, int panelWidth, int panelHeight, Colouriser colourise)
    {
        int slotOriginX = PanelPadding + panelIndex * (PanelSize + PanelPadding);

        // Centre the panel within its slot, both axes.
        int originX = slotOriginX + (PanelSize - panelWidth) / 2;
        int originY = (sheetHeight - panelHeight) / 2;

        for (int y = 0; y < panelHeight; y++)
        {
            int destRow = (originY + y) * sheetWidth;
            for (int x = 0; x < panelWidth; x++)
                sheet[destRow + originX + x] = colourise(panel[y * panelWidth + x]);
        }
    }

    // Returns panel dimensions that fit within PanelSize while preserving a source aspect.
    // Keeps the validation previews undistorted, so shape quality can be judged honestly.
    static void FitAspect(int srcWidth, int srcHeight, out int panelWidth, out int panelHeight)
    {
        if (srcWidth >= srcHeight)
        {
            panelWidth = PanelSize;
            panelHeight = Mathf.Max(1, Mathf.RoundToInt(PanelSize * (float)srcHeight / srcWidth));
        }
        else
        {
            panelHeight = PanelSize;
            panelWidth = Mathf.Max(1, Mathf.RoundToInt(PanelSize * (float)srcWidth / srcHeight));
        }
    }

    // Writes the sheet next to the source texture, named after the source and its settings
    // so successive runs at different cell sizes or spreads sit side by side for comparison
    // rather than overwriting each other.
    string WriteSheet(Texture2D sheet)
    {
        string sourcePath = AssetDatabase.GetAssetPath(_source);
        string dir = Path.GetDirectoryName(sourcePath).Replace('\\', '/');
        string outputDir = $"{dir}/ContactSheets";

        if (!AssetDatabase.IsValidFolder(outputDir))
            AssetDatabase.CreateFolder(dir, "ContactSheets");

        string name = $"{_source.name}_cell{_settings.cellSize}_spread{_settings.spreadPixels:0.#}.png";
        string path = $"{outputDir}/{name}";

        File.WriteAllBytes(path, sheet.EncodeToPNG());
        AssetDatabase.ImportAsset(path);

        return path;
    }
}
