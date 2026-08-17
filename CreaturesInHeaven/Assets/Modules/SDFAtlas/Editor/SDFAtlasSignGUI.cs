using UnityEngine;
using UnityEditor;

// Material inspector shared by the SDFAtlas sign shaders.
//
// The shaders need the atlas's grid layout as material constants, but those values already
// exist in the atlas manifest. This inspector reads them off the manifest of whichever atlas
// is assigned and offers to apply them.
//
// Blend state is fixed per shader rather than exposed here, so this inspector covers only
// the properties every SDFAtlas sign shader shares.
public class SDFAtlasSignGUI : ShaderGUI
{
    SDFAtlasInfo _manifest;
    string _manifestAtlasPath;

    public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        var material = materialEditor.target as Material;

        MaterialProperty atlas = FindProperty("_Atlas", properties);
        MaterialProperty gridSize = FindProperty("_GridSize", properties);
        MaterialProperty cellSize = FindProperty("_CellSize", properties);
        MaterialProperty padding = FindProperty("_Padding", properties);
        MaterialProperty spread = FindProperty("_Spread", properties);

        EditorGUILayout.LabelField("Atlas", EditorStyles.boldLabel);
        materialEditor.ShaderProperty(atlas, "SDF atlas");

        RefreshManifest(atlas);
        DrawEncodingCheck(material);
        DrawLayoutSection(materialEditor, material, gridSize, cellSize, padding, spread);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Appearance", EditorStyles.boldLabel);
        materialEditor.ShaderProperty(FindProperty("_Color", properties), "Colour");
        materialEditor.ShaderProperty(FindProperty("_Intensity", properties), "Intensity");

        MaterialProperty vertexColor = FindProperty("_VertexColor", properties, false);
        if (vertexColor != null)
        {
            materialEditor.ShaderProperty(vertexColor, "Vertex base colour");
        }

        materialEditor.ShaderProperty(FindProperty("_EdgeBias", properties), "Edge bias (texels)");
        materialEditor.ShaderProperty(FindProperty("_EdgeSoftness", properties), "Edge softness");

        DrawCellReference();

        EditorGUILayout.Space();
        materialEditor.RenderQueueField();
        materialEditor.DoubleSidedGIField();
    }

    // --- Encoding check ------------------------------------------------------

    // Warns when the material's shader does not match the atlas's encoding.
    //
    // This mismatch is the reason the two encodings have separate shaders, and it is worth
    // catching here because neither direction fails loudly:
    //
    //   MSDF atlas + single-channel shader: reads only the red channel, which is a valid
    //   distance field covering a subset of the edges. Renders a recognisable but subtly
    //   wrong shape, with the rounded corners MSDF was chosen to avoid.
    //
    //   Single-channel atlas + MSDF shader: all three channels hold the same value, so the
    //   median returns it unchanged. Renders correctly but pays 3x the memory for nothing.
    //
    // Both look plausible enough to ship by accident, which is exactly what makes an
    // explicit check worthwhile.
    void DrawEncodingCheck(Material material)
    {
        if (_manifest == null || material == null || material.shader == null) return;

        string shaderName = material.shader.name;

        // Only comment on shaders from this module. A custom shader built on the same
        // .cginc is a legitimate thing to have and should not be nagged about.
        if (!shaderName.StartsWith("SDFAtlas/")) return;

        // Detect the shader's channel count from its name rather than comparing against an
        // exact expected string.
        bool shaderIsMultiChannel = shaderName.IndexOf("MSDF", System.StringComparison.OrdinalIgnoreCase) >= 0;

        if (shaderIsMultiChannel == _manifest.IsMultiChannel) return;

        string detail = _manifest.IsMultiChannel
            ? "This atlas is multi-channel (MSDF), but the material uses a single-channel " +
              "shader. Only the red channel will be read, and corners will round off."
            : "This atlas is single-channel, but the material uses an MSDF shader. It will " +
              "render correctly but reads three identical channels, so the atlas is three " +
              "times larger than it needs to be.";

        EditorGUILayout.HelpBox(detail, MessageType.Warning);
    }

    // --- Cell reference ------------------------------------------------------

    bool _cellListExpanded;
    Vector2 _cellScroll;

    // Lists which graphic sits in which cell, with the UV offset needed to display it.
    //
    // This is a reference table, not a picker: the cell address lives in the mesh UVs, which
    // are per-mesh rather than per-material, so nothing here can assign a graphic to an
    // object. What it does is save opening the manifest JSON to find out that (say) the exit
    // sign is at tile (3,1), so the UV island can be placed there when authoring the mesh.
    void DrawCellReference()
    {
        if (_manifest == null || _manifest.cells == null) return;

        EditorGUILayout.Space();
        _cellListExpanded = EditorGUILayout.Foldout(_cellListExpanded, "Atlas contents", true);
        if (!_cellListExpanded) return;

        EditorGUILayout.HelpBox(
            "Cell addresses live in the mesh UVs: offset a quad's UV island by the tile " +
            "coordinate to display that graphic.",
            MessageType.Info);

        _cellScroll = EditorGUILayout.BeginScrollView(_cellScroll, GUILayout.MaxHeight(200));

        for (int i = 0; i < _manifest.cells.Length; i++)
        {
            if (!_manifest.cells[i].occupied) continue;

            _manifest.IndexToCoord(i, out int cellX, out int cellY);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"({cellX}, {cellY})", GUILayout.Width(60));
            EditorGUILayout.LabelField(_manifest.cells[i].name);

            // Copies the tile coordinate in a form that can be pasted straight into a UV
            // offset field, in Blender or elsewhere.
            if (GUILayout.Button("Copy UV", GUILayout.Width(70)))
            {
                EditorGUIUtility.systemCopyBuffer = $"{cellX}, {cellY}";
            }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
    }

    // --- Layout, cross-checked against the manifest -------------------------

    void DrawLayoutSection(MaterialEditor materialEditor, Material material,
                           MaterialProperty gridSize, MaterialProperty cellSize,
                           MaterialProperty padding, MaterialProperty spread)
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Layout", EditorStyles.boldLabel);

        if (_manifest == null)
        {
            EditorGUILayout.HelpBox(
                "No manifest found for this atlas. Layout values below must be set by hand to " +
                "match how the atlas was packed -- a mismatch silently displays the wrong cells.",
                MessageType.Warning);
        }
        else
        {
            bool matches =
                Mathf.Approximately(gridSize.vectorValue.x, _manifest.gridWidth) &&
                Mathf.Approximately(gridSize.vectorValue.y, _manifest.gridHeight) &&
                Mathf.Approximately(cellSize.floatValue, _manifest.cellSize) &&
                Mathf.Approximately(padding.floatValue, _manifest.padding) &&
                Mathf.Approximately(spread.floatValue, _manifest.spread);

            if (matches)
            {
                EditorGUILayout.HelpBox("Layout matches the atlas manifest.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"Layout does not match the atlas manifest:\n" +
                    $"  grid {_manifest.gridWidth}x{_manifest.gridHeight}, " +
                    $"cell {_manifest.cellSize}, padding {_manifest.padding}, spread {_manifest.spread}",
                    MessageType.Error);

                if (GUILayout.Button("Apply manifest layout"))
                {
                    Undo.RecordObject(material, "Apply SDF atlas layout");
                    gridSize.vectorValue = new Vector4(_manifest.gridWidth, _manifest.gridHeight, 0, 0);
                    cellSize.floatValue = _manifest.cellSize;
                    padding.floatValue = _manifest.padding;
                    spread.floatValue = _manifest.spread;
                    EditorUtility.SetDirty(material);
                }
            }
        }

        materialEditor.ShaderProperty(gridSize, "Grid size (cells)");
        materialEditor.ShaderProperty(cellSize, "Cell size (texels)");
        materialEditor.ShaderProperty(padding, "Padding (texels)");
        materialEditor.ShaderProperty(spread, "Spread (texels)");
    }

    // Loads the manifest for the assigned atlas, if it has one. Cached against the atlas path
    // so this is not re-read from disk on every inspector repaint.
    void RefreshManifest(MaterialProperty atlas)
    {
        string path = atlas.textureValue != null
            ? AssetDatabase.GetAssetPath(atlas.textureValue)
            : null;

        if (path == _manifestAtlasPath) return;

        _manifestAtlasPath = path;
        _manifest = string.IsNullOrEmpty(path) ? null : SDFAtlasInfo.Load(path);
    }

}
