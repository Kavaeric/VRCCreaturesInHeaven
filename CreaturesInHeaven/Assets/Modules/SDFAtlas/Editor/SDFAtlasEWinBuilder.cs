using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

// Builds an SDF atlas from an explicit list of source graphics.
//
// Cell assignment is explicit and stable by design. Each graphic sits at a fixed cell index
// which is baked into mesh UVs at authoring time, so the list order is meaningful data
// rather than presentation: reordering it changes what every already-placed quad displays.
// The list is therefore edited by deliberate add/remove/move actions, never re-sorted
// automatically.
//
// Opens via Tools > SDF Atlas > Atlas builder...
public class SDFAtlasEWinBuilder : EditorWindow
{
    // --- Atlas configuration --------------------------------------------

    int _cellSize = 64;
    int _gridWidth = 16;
    int _gridHeight = 16;
    int _padding = 2;
    float _spread = 4f;
    SDFAtlasEncoder.CoverageChannel _channel = SDFAtlasEncoder.CoverageChannel.Alpha;
    SDFAtlasEncoder.EdgeMode _edgeMode = SDFAtlasEncoder.EdgeMode.SubTexel;
    float _coverageThreshold = 0.5f;

    string _atlasName = "SignageAtlas";
    string _outputFolder = "Assets/Modules/SDFAtlas/Generated";

    // --- Source list ------------------------------------------------------

    // Ordered list of sources. Position in this list is the cell index, so gaps are
    // represented by null entries rather than by compacting the list.
    readonly List<Texture2D> _sources = new List<Texture2D>();

    Vector2 _scroll;
    string _status;
    MessageType _statusType = MessageType.None;
    string _lastAtlasPath;

    // --- Window lifecycle ---------------------------------------------------

    [MenuItem("Tools/SDF Atlas/Atlas builder...")]
    static void Open() => GetWindow<SDFAtlasEWinBuilder>("SDF atlas builder");

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
            new[] {
                    new GUIContent("16"),
                    new GUIContent("32"),
                    new GUIContent("64"),
                    new GUIContent("128"),
                    new GUIContent("256"),
                    new GUIContent("512"),
                    new GUIContent("1024"),
                    new GUIContent("2048"),
                  },
            new[] { 16, 32, 64, 128, 256, 512, 1024, 2048 });

        _gridWidth = Mathf.Max(1, EditorGUILayout.IntField("Grid width (cells)", _gridWidth));
        _gridHeight = Mathf.Max(1, EditorGUILayout.IntField("Grid height (cells)", _gridHeight));

        _padding = Mathf.Clamp(EditorGUILayout.IntField(
            new GUIContent("Padding (texels)",
                "Border inside each cell carrying distance data that continues past the artwork. " +
                "Smaller values pack artwork more tightly but may result in bleeding."),
            _padding), 0, _cellSize / 2 - 1);

        _spread = EditorGUILayout.Slider(
            new GUIContent("Spread (texels)",
                "Distance range mapped to the stored 0..1. Smaller records edge position more " +
                "precisely; larger leaves gradient further from the edge for effects."),
            _spread, 1f, 16f);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Encoding", EditorStyles.boldLabel);

        _channel = (SDFAtlasEncoder.CoverageChannel)EditorGUILayout.EnumPopup(
            new GUIContent("Coverage channel", "Which channel carries shape coverage."), _channel);

        _edgeMode = (SDFAtlasEncoder.EdgeMode)EditorGUILayout.EnumPopup(
            new GUIContent("Edge mode",
                "Sub-texel uses the source's antialiased edge to place the boundary between " +
                "pixel centres. Binary rounds it to the nearest pixel first, and is kept only " +
                "for comparison."), _edgeMode);

        _coverageThreshold = EditorGUILayout.Slider("Coverage threshold", _coverageThreshold, 0.01f, 0.99f);

        // Sub-texel refinement reads the grey values along the source's antialiased edge, so
        // a source with a hard-edged alpha has nothing for it to read and falls back to
        // binary behaviour on its own.
        if (_edgeMode == SDFAtlasEncoder.EdgeMode.SubTexel)
        {
            EditorGUILayout.HelpBox(
                "Sub-texel needs antialiased source alpha. Sources exported with hard-edged " +
                "or 1-bit alpha gain nothing from it.", MessageType.None);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        _atlasName = EditorGUILayout.TextField("Atlas name", _atlasName);
        _outputFolder = EditorGUILayout.TextField("Output folder", _outputFolder);

        // Surface the derived numbers rather than making the user compute them.
        int capacity = _gridWidth * _gridHeight;
        int artwork = _cellSize - 2 * _padding;

        // Padding buys clean mip levels: a level-N mip texel averages a 2^N block, so it only
        // stays within its own cell while the padding is at least that wide. Deeper levels
        // bleed between neighbours.
        int safeMip = 0;
        while ((1 << (safeMip + 1)) <= Mathf.Max(_padding, 1)) safeMip++;

        EditorGUILayout.HelpBox(
            $"Atlas: {_cellSize * _gridWidth} x {_cellSize * _gridHeight} texels, " +
            $"{capacity} cells, {artwork}x{artwork} artwork area per cell.\n" +
            $"R8 uncompressed: ~{_cellSize * _gridWidth * _cellSize * _gridHeight / 1024 / 1024f:0.##} MB.\n" +
            $"Padding {_padding} keeps mip levels 0-{safeMip} free of cross-cell bleed.",
            MessageType.None);
    }

    // --- Source list UI -------------------------------------------------------

    void DrawSourceList()
    {
        EditorGUILayout.LabelField("Graphics", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "List position is the cell index, which gets baked into mesh UVs. " +
            "Reordering or removing entries changes what already-placed quads display.\n\n" +
            "Coordinates are UDIM tiles: (0,0) is the atlas's bottom-left cell and +Y runs " +
            "up, so they match Blender's tile numbering directly.",
            MessageType.Warning);

        int capacity = _gridWidth * _gridHeight;

        for (int i = 0; i < _sources.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();

            // Show the cell index and its UDIM tile coordinate, since that coordinate is what
            // gets authored into the mesh UVs. Cell Y runs bottom-up to match UV space, so
            // index 0 is the atlas's bottom-left cell and the list fills upward.
            int cellX = i % _gridWidth;
            int cellY = i / _gridWidth;
            EditorGUILayout.LabelField($"{i}  ({cellX},{cellY})", GUILayout.Width(80));

            _sources[i] = (Texture2D)EditorGUILayout.ObjectField(_sources[i], typeof(Texture2D), false);

            using (new EditorGUI.DisabledScope(i == 0))
            {
                if (GUILayout.Button("↑", GUILayout.Width(24)))
                {
                    (_sources[i - 1], _sources[i]) = (_sources[i], _sources[i - 1]);
                }
            }
            using (new EditorGUI.DisabledScope(i == _sources.Count - 1))
            {
                if (GUILayout.Button("↓", GUILayout.Width(24)))
                {
                    (_sources[i + 1], _sources[i]) = (_sources[i], _sources[i + 1]);
                }
            }
            if (GUILayout.Button("✕", GUILayout.Width(24)))
            {
                _sources.RemoveAt(i);
                EditorGUILayout.EndHorizontal();
                break;
            }

            EditorGUILayout.EndHorizontal();
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
            if (GUILayout.Button("Build atlas", GUILayout.Height(30)))
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
        var info = SDFAtlasInfo.Create(_cellSize, _gridWidth, _gridHeight, _padding, _spread);

        var settings = SDFAtlasEncoder.Settings.Default;
        settings.channel = _channel;
        settings.coverageThreshold = _coverageThreshold;
        settings.edgeMode = _edgeMode;

        var entries = new List<SDFAtlasPacker.Entry>();
        for (int i = 0; i < _sources.Count; i++)
        {
            if (_sources[i] == null) continue;   // Deliberate gap; cell stays empty.
            if (!ValidateSource(_sources[i])) return;

            entries.Add(new SDFAtlasPacker.Entry
            {
                source = _sources[i],
                cellIndex = i,
                name = _sources[i].name,
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
            EditorUtility.DisplayProgressBar("SDF atlas", "Encoding graphics...", 0.2f);
            Texture2D atlas = SDFAtlasPacker.Pack(entries, info, settings);

            EditorUtility.DisplayProgressBar("SDF atlas", "Writing atlas...", 0.8f);
            string path = $"{_outputFolder}/{_atlasName}.png";
            _lastAtlasPath = SDFAtlasPacker.WriteAtlas(atlas, info, path);
            DestroyImmediate(atlas);

            _status = $"Built {entries.Count} graphic(s) into {path}\n" +
                      $"Manifest: {Path.GetFileName(SDFAtlasInfo.ManifestPath(path))}";
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

    // Rejects sources that would encode incorrectly.
    bool ValidateSource(Texture2D source)
    {
        var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(source)) as TextureImporter;
        if (importer == null) return true;

        if (!importer.isReadable)
        {
            _status = $"'{source.name}' is not readable.\n\n" +
                      "Enable Read/Write Enabled in its import settings and re-import.";
            _statusType = MessageType.Error;
            return false;
        }

        if (importer.textureCompression != TextureImporterCompression.Uncompressed)
        {
            Debug.LogWarning(
                $"[SDFAtlas] '{source.name}' is block-compressed; edge precision will suffer. " +
                "Set Compression to None for best results.");
        }

        return true;
    }
}
