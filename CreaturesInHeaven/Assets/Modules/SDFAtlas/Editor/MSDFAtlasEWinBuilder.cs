using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

// Builds a multi-channel (MSDF) atlas from an explicit list of SVG sources.
//
// The single-channel counterpart is SDFAtlasEWinBuilder. Kept separate for the same reason
// the shaders are: the source type differs (SVG rather than raster), the settings differ
// (corner angle and seed rather than coverage channel and threshold), and the resulting
// atlases are not interchangeable.
//
// Cell assignment is explicit and stable by design. Each graphic sits at a fixed cell index
// baked into mesh UVs at authoring time, so list order is meaningful data rather than
// presentation: reordering changes what every already-placed quad displays.
//
// Opens via Tools > SDF Atlas > MSDF atlas builder...
public class MSDFAtlasEWinBuilder : EditorWindow
{
    // --- Atlas configuration --------------------------------------------

    int _cellWidth = 64;
    int _cellHeight = 64;
    int _gridWidth = 16;
    int _gridHeight = 16;
    int _padding = 4;
    float _spread = 4f;
    SDFAtlasInfo.AtlasFraming _framing = SDFAtlasInfo.AtlasFraming.PreserveAspect;

    double _angleThreshold = MSDFAtlasEdgeColouring.DefaultAngleThreshold;
    ulong _seed = 0;
    bool _errorCorrection = true;

    string _atlasName = "SignageAtlasMSDF";
    string _outputFolder = "Assets/Modules/SDFAtlas/Generated";

    // --- Source list ------------------------------------------------------

    // Ordered list of sources; position is the cell index, so gaps are null entries rather
    // than a compacted list.
    readonly List<DefaultAsset> _sources = new List<DefaultAsset>();

    Vector2 _scroll;
    string _status;
    MessageType _statusType = MessageType.None;
    string _lastAtlasPath;

    // --- Window lifecycle ---------------------------------------------------

    [MenuItem("Tools/SDF Atlas/MSDF atlas builder...")]
    static void Open() => GetWindow<MSDFAtlasEWinBuilder>("MSDF atlas builder");

    void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        DrawAtlasSettings();
        EditorGUILayout.Space();
        DrawSourceList();
        EditorGUILayout.Space();
        DrawBuildControls();

        EditorGUILayout.EndScrollView();
    }

    // --- Settings UI ---------------------------------------------------------

    void DrawAtlasSettings()
    {
        EditorGUILayout.LabelField("Atlas layout", EditorStyles.boldLabel);

        SDFAtlasBuilderGUI.CellSizeFields(ref _cellWidth, ref _cellHeight);

        _gridWidth = Mathf.Max(1, EditorGUILayout.IntField("Grid width (cells)", _gridWidth));
        _gridHeight = Mathf.Max(1, EditorGUILayout.IntField("Grid height (cells)", _gridHeight));

        // Clamped against the shorter axis: padding is uniform on all four sides, so the
        // smaller dimension is what limits how much of it fits.
        _padding = Mathf.Clamp(EditorGUILayout.IntField(
            new GUIContent("Padding (texels)",
                "Border inside each cell carrying distance data that continues past the " +
                "artwork. Also the margin the shape is framed into."),
            _padding), 0, Mathf.Min(_cellWidth, _cellHeight) / 2 - 1);

        _spread = EditorGUILayout.Slider(
            new GUIContent("Spread (texels)",
                "Distance range mapped to the stored 0..1. Larger leaves more gradient for " +
                "the shader to antialias against, at the cost of edge precision."),
            _spread, 1f, 16f);

        _framing = (SDFAtlasInfo.AtlasFraming)EditorGUILayout.EnumPopup(
            new GUIContent("Framing",
                "How artwork is fitted into its cell. Preserve aspect letterboxes non-square " +
                "artwork, keeping the field isotropic. Stretch fills the whole cell, spending " +
                "no texels on margin, at the cost of an anisotropic field."),
            _framing);

        DrawFramingNote();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Encoding", EditorStyles.boldLabel);

        _angleThreshold = EditorGUILayout.Slider(
            new GUIContent("Corner angle (rad)",
                "Joins sharper than this count as corners and get a channel switch. The " +
                "default 3.0 (~172 degrees) is close to straight on purpose: missing a real " +
                "corner rounds it off, while treating a smooth join as a corner costs nothing."),
            (float)_angleThreshold, 0.1f, 3.14f);

        _seed = (ulong)Mathf.Max(0, EditorGUILayout.IntField(
            new GUIContent("Seed",
                "Varies which channels get assigned first. Different seeds are equally valid; " +
                "change it if a particular graphic shows colour fringing."),
            (int)_seed));

        _errorCorrection = EditorGUILayout.Toggle(
            new GUIContent("Error correction",
                "Removes interpolation artifacts -- isolated bright or dark pixels along " +
                "edges. Leaves corners alone."),
            _errorCorrection);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        _atlasName = EditorGUILayout.TextField("Atlas name", _atlasName);
        _outputFolder = EditorGUILayout.TextField("Output folder", _outputFolder);

        int capacity = _gridWidth * _gridHeight;
        int artworkWidth = _cellWidth - 2 * _padding;
        int artworkHeight = _cellHeight - 2 * _padding;

        int safeMip = SDFAtlasBuilderGUI.SafeMipLevel(_padding);

        // Three bytes per texel rather than one. Worth stating plainly next to the number,
        // since the whole point of MSDF is trading memory for corner sharpness and the
        // trade is only sensible if the cell size comes down to match.
        long texels = (long)_cellWidth * _gridWidth * _cellHeight * _gridHeight;
        float megabytes = texels * 3f / 1024f / 1024f;

        EditorGUILayout.HelpBox(
            $"Atlas: {_cellWidth * _gridWidth} x {_cellHeight * _gridHeight} texels, " +
            $"{capacity} cells, {artworkWidth}x{artworkHeight} artwork area per cell.\n" +
            $"RGB24 uncompressed: ~{megabytes:0.##} MB (3x single-channel at the same size).\n" +
            $"Padding {_padding} keeps mip levels 0-{safeMip} free of cross-cell bleed.",
            MessageType.None);
    }

    // Effective spread below which the shader has too little gradient to antialias against.
    // Matches MSDFAtlasPacker.MinUsableSpread, which does the same check per graphic at
    // build time with the real stretch ratio in hand.
    const float MinUsableSpread = 1.5f;

    // Explains what the chosen framing costs, and how much stretch the current spread can
    // absorb before edges start to look stepped.
    //
    // The exact ratio is per-graphic and not known until each SVG is loaded, so this reports
    // the budget rather than a verdict; the packer warns by name for any graphic that
    // actually exceeds it.
    void DrawFramingNote()
    {
        if (_framing == SDFAtlasInfo.AtlasFraming.PreserveAspect)
        {
            EditorGUILayout.HelpBox(
                "Artwork keeps its authored proportions and is centred in the cell, so a " +
                "graphic whose aspect differs from the cell's is letterboxed and spends some " +
                "of the cell's resolution on empty margin.",
                MessageType.None);
            return;
        }

        // Stretch divides the stored spread by the stretch ratio on the compressed axis, so
        // the tolerable ratio is however many times the spread exceeds the usable minimum.
        float maxRatio = _spread / MinUsableSpread;

        EditorGUILayout.HelpBox(
            "Artwork is scaled per-axis to fill the cell, so none of the cell's resolution is " +
            "spent on margin. The stored field is then anisotropic: one stored unit is worth " +
            "more real distance along one axis than the other, which slightly widens the " +
            "antialiasing band on that axis and makes Edge bias dilate unevenly.\n\n" +
            $"At spread {_spread:0.##}, stretch up to about {maxRatio:0.##}:1 keeps at least " +
            $"{MinUsableSpread} texels of effective spread on the narrow axis. Anything " +
            "stretched harder than that is warned about by name at build time.",
            maxRatio >= 2f ? MessageType.None : MessageType.Warning);
    }

    // --- Source list UI -------------------------------------------------------

    void DrawSourceList()
    {
        EditorGUILayout.LabelField("Graphics (SVG)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "List position is the cell index, which decides where in the atlas a graphic " +
            "lands. Reordering or removing entries moves the graphics already-placed quads " +
            "have their UVs over.\n\n" +
            "Coordinates are cells: (0,0) is the atlas's bottom-left and +Y runs up, matching " +
            "UV space.\n\n" +
            "Sources must be flattened outlines. Strokes need outlining before export.",
            MessageType.Warning);

        int capacity = _gridWidth * _gridHeight;

        for (int i = 0; i < _sources.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();

            int cellX = i % _gridWidth;
            int cellY = i / _gridWidth;
            EditorGUILayout.LabelField($"{i}  ({cellX},{cellY})", GUILayout.Width(80));

            _sources[i] = (DefaultAsset)EditorGUILayout.ObjectField(
                _sources[i], typeof(DefaultAsset), false);

            using (new EditorGUI.DisabledScope(i == 0))
            {
                if (GUILayout.Button("↑", GUILayout.Width(24)))
                    (_sources[i - 1], _sources[i]) = (_sources[i], _sources[i - 1]);
            }
            using (new EditorGUI.DisabledScope(i == _sources.Count - 1))
            {
                if (GUILayout.Button("↓", GUILayout.Width(24)))
                    (_sources[i + 1], _sources[i]) = (_sources[i], _sources[i + 1]);
            }
            if (GUILayout.Button("✕", GUILayout.Width(24)))
            {
                _sources.RemoveAt(i);
                EditorGUILayout.EndHorizontal();
                break;
            }

            EditorGUILayout.EndHorizontal();

            // Flag non-SVG assets immediately rather than at build time. DefaultAsset
            // accepts any unrecognised file type, so a stray drop is easy.
            if (_sources[i] != null)
            {
                string path = AssetDatabase.GetAssetPath(_sources[i]);
                if (!path.EndsWith(".svg", System.StringComparison.OrdinalIgnoreCase))
                {
                    EditorGUILayout.HelpBox(
                        $"'{Path.GetFileName(path)}' is not an SVG.", MessageType.Error);
                }
            }
        }

        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(_sources.Count >= capacity))
        {
            if (GUILayout.Button("Add slot")) _sources.Add(null);
        }
        if (GUILayout.Button("Clear all") &&
            EditorUtility.DisplayDialog("Clear source list",
                "Remove all graphics from the list? This does not delete any assets.", "Clear", "Cancel"))
        {
            _sources.Clear();
        }
        EditorGUILayout.EndHorizontal();

        if (_sources.Count > capacity)
        {
            EditorGUILayout.HelpBox(
                $"{_sources.Count} graphics listed but only {capacity} cells available. " +
                "Increase the grid size or remove entries.", MessageType.Error);
        }
    }

    // --- Build ----------------------------------------------------------------

    void DrawBuildControls()
    {
        int capacity = _gridWidth * _gridHeight;
        bool canBuild = _sources.Count > 0 && _sources.Count <= capacity &&
                        !string.IsNullOrWhiteSpace(_atlasName);

        using (new EditorGUI.DisabledScope(!canBuild))
        {
            if (GUILayout.Button("Build MSDF atlas", GUILayout.Height(30)))
                Build();
        }

        if (!string.IsNullOrEmpty(_status))
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(_status, _statusType);
        }

        if (!string.IsNullOrEmpty(_lastAtlasPath) && GUILayout.Button("Select atlas in Project"))
        {
            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(_lastAtlasPath);
            if (asset != null) Selection.activeObject = asset;
        }
    }

    void Build()
    {
        var info = SDFAtlasInfo.Create(_cellWidth, _cellHeight, _gridWidth, _gridHeight,
                                       _padding, _spread,
                                       SDFAtlasInfo.AtlasEncoding.MultiChannel,
                                       _framing);

        var settings = MSDFAtlasPacker.Settings.Default;
        settings.angleThreshold = _angleThreshold;
        settings.seed = _seed;
        settings.errorCorrection = _errorCorrection;

        var entries = new List<MSDFAtlasPacker.Entry>();

        for (int i = 0; i < _sources.Count; i++)
        {
            if (_sources[i] == null) continue;   // Deliberate gap; cell stays empty.

            string path = AssetDatabase.GetAssetPath(_sources[i]);
            if (!path.EndsWith(".svg", System.StringComparison.OrdinalIgnoreCase))
            {
                _status = $"'{Path.GetFileName(path)}' is not an SVG file.";
                _statusType = MessageType.Error;
                return;
            }

            entries.Add(new MSDFAtlasPacker.Entry
            {
                svgPath = path,
                cellIndex = i,
                name = Path.GetFileNameWithoutExtension(path),
            });
        }

        if (entries.Count == 0)
        {
            _status = "No usable graphics in the list.";
            _statusType = MessageType.Warning;
            return;
        }

        try
        {
            Texture2D atlas = MSDFAtlasPacker.Pack(entries, info, settings, (entryIndex, entryCount, name, cellProgress) =>
            {
                // Reserve the last 20% of the bar for writing the atlas out, same as before;
                // encoding fills the other 80%, split evenly across entries and further split
                // within an entry by its own generation progress, so one slow, complex
                // graphic still visibly advances instead of parking the bar at its start.
                float overall = (entryIndex + cellProgress) / entryCount * 0.8f;
                return !EditorUtility.DisplayCancelableProgressBar(
                    "MSDF atlas", $"Encoding '{name}' ({entryIndex + 1}/{entryCount})...", overall);
            });

            if (atlas == null)
            {
                _status = "Build cancelled.";
                _statusType = MessageType.Warning;
                return;
            }

            EditorUtility.DisplayProgressBar("MSDF atlas", "Writing atlas...", 0.8f);
            string path = $"{_outputFolder}/{_atlasName}.png";
            _lastAtlasPath = MSDFAtlasPacker.WriteAtlas(atlas, info, path);
            DestroyImmediate(atlas);

            _status = $"Built {entries.Count} graphic(s) into {path}\n" +
                      $"Manifest: {Path.GetFileName(SDFAtlasInfo.ManifestPath(path))}\n" +
                      $"Reference: {Path.GetFileName(SDFAtlasReference.ReferencePath(path))}\n\n" +
                      $"Use the 'SDFAtlas/MSDF Additive' shader with this atlas.";
            _statusType = MessageType.Info;
        }
        catch (UnityException e)
        {
            _status = $"Build failed.\n\n{e.Message}";
            _statusType = MessageType.Error;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }
}
