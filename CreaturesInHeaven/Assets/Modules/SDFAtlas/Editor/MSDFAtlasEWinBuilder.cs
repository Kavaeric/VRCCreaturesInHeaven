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

    int _cellSize = 64;
    int _gridWidth = 16;
    int _gridHeight = 16;
    int _padding = 4;
    float _spread = 4f;

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

        _cellSize = EditorGUILayout.IntPopup(
            new GUIContent("Cell size", "Cell edge in texels, including padding."),
            _cellSize,
            new[] { new GUIContent("32"), new GUIContent("64"), new GUIContent("128"), new GUIContent("256") },
            new[] { 32, 64, 128, 256 });

        _gridWidth = Mathf.Max(1, EditorGUILayout.IntField("Grid width (cells)", _gridWidth));
        _gridHeight = Mathf.Max(1, EditorGUILayout.IntField("Grid height (cells)", _gridHeight));

        _padding = Mathf.Clamp(EditorGUILayout.IntField(
            new GUIContent("Padding (texels)",
                "Border inside each cell carrying distance data that continues past the " +
                "artwork. Also the margin the shape is framed into."),
            _padding), 0, _cellSize / 2 - 1);

        _spread = EditorGUILayout.Slider(
            new GUIContent("Spread (texels)",
                "Distance range mapped to the stored 0..1. Larger leaves more gradient for " +
                "the shader to antialias against, at the cost of edge precision."),
            _spread, 1f, 16f);

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
        int artwork = _cellSize - 2 * _padding;

        int safeMip = 0;
        while ((1 << (safeMip + 1)) <= Mathf.Max(_padding, 1)) safeMip++;

        // Three bytes per texel rather than one. Worth stating plainly next to the number,
        // since the whole point of MSDF is trading memory for corner sharpness and the
        // trade is only sensible if the cell size comes down to match.
        float megabytes = _cellSize * _gridWidth * _cellSize * _gridHeight * 3f / 1024f / 1024f;

        EditorGUILayout.HelpBox(
            $"Atlas: {_cellSize * _gridWidth} x {_cellSize * _gridHeight} texels, " +
            $"{capacity} cells, {artwork}x{artwork} artwork area per cell.\n" +
            $"RGB24 uncompressed: ~{megabytes:0.##} MB (3x single-channel at the same size).\n" +
            $"Padding {_padding} keeps mip levels 0-{safeMip} free of cross-cell bleed.",
            MessageType.None);
    }

    // --- Source list UI -------------------------------------------------------

    void DrawSourceList()
    {
        EditorGUILayout.LabelField("Graphics (SVG)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "List position is the cell index, which gets baked into mesh UVs. " +
            "Reordering or removing entries changes what already-placed quads display.\n\n" +
            "Coordinates are UDIM tiles: (0,0) is the atlas's bottom-left cell and +Y runs " +
            "up, so they match Blender's tile numbering directly.\n\n" +
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
        var info = SDFAtlasInfo.Create(_cellSize, _gridWidth, _gridHeight, _padding, _spread,
                                       SDFAtlasInfo.AtlasEncoding.MultiChannel);

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
            EditorUtility.DisplayProgressBar("MSDF atlas", "Encoding graphics...", 0.2f);
            Texture2D atlas = MSDFAtlasPacker.Pack(entries, info, settings);

            EditorUtility.DisplayProgressBar("MSDF atlas", "Writing atlas...", 0.8f);
            string path = $"{_outputFolder}/{_atlasName}.png";
            _lastAtlasPath = MSDFAtlasPacker.WriteAtlas(atlas, info, path);
            DestroyImmediate(atlas);

            _status = $"Built {entries.Count} graphic(s) into {path}\n" +
                      $"Manifest: {Path.GetFileName(SDFAtlasInfo.ManifestPath(path))}\n\n" +
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
