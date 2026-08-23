using UnityEngine;
using UnityEditor;

// Material inspector shared by the SDFAtlas sign shaders.
//
// The shaders need the atlas's spread as a material constant, but that value already exists
// in the atlas manifest. This inspector reads it off the manifest of whichever atlas is
// assigned and offers to apply it, and lists the atlas's contents so a UV island can be
// placed over the right graphic when authoring a mesh.
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

        // Keywords are derived from property values rather than stored, so they are resynced
        // whenever a property changes. Watching the whole inspector covers the toggles being
        // changed from a script or an animation as well as from the rows below.
        EditorGUI.BeginChangeCheck();

        MaterialProperty atlas = FindProperty("_Atlas", properties);
        MaterialProperty spread = FindProperty("_Spread", properties);

        EditorGUILayout.LabelField("Atlas", EditorStyles.boldLabel);
        materialEditor.ShaderProperty(atlas, "SDF atlas");

        RefreshManifest(atlas);
        DrawEncodingCheck(material);
        DrawSpreadSection(materialEditor, material, spread);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Appearance", EditorStyles.boldLabel);
        // Labelled by what the shader declares: on the two-tone shader this colour is one of a
        // pair and "Colour" alone would not say which, while on every other shader there is
        // only one and "Foreground" would imply a background that does not exist.
        bool twoTone = FindProperty("_BackColor", properties, false) != null;
        materialEditor.ShaderProperty(FindProperty("_Color", properties),
                                      twoTone ? "Foreground colour" : "Colour");

        // Properties here vary by shader: the emissive shaders have an intensity, the lit one
        // has surface parameters instead. Look each up optionally so this one inspector can
        // serve both without either needing a subclass.
        DrawOptionalProperty(materialEditor, properties, "_BackColor", "Background colour");
        DrawOptionalProperty(materialEditor, properties, "_Intensity", "Intensity");
        DrawOptionalProperty(materialEditor, properties, "_VertexColor", "Vertex base colour");

        // Only the cutout emissive shader bakes its emission, so this sits with the appearance
        // properties rather than in a section of its own.
        DrawOptionalProperty(materialEditor, properties, "_EmissionBakeStrength", "Bake emission strength");

        // Shape before surface: edge shaping is part of how the graphic is read off the
        // atlas, so it belongs next to the atlas settings above rather than after the
        // lighting parameters. The lit shader's cutoff is edge shaping too, whatever its
        // "alpha" name suggests, so it sits here rather than with roughness and metallic.
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Shape", EditorStyles.boldLabel);
        materialEditor.ShaderProperty(FindProperty("_EdgeBias", properties), "Edge bias (texels)");
        materialEditor.ShaderProperty(FindProperty("_EdgeSoftness", properties), "Edge softness");
        DrawOptionalProperty(materialEditor, properties, "_Cutoff", "Alpha cutoff");

        DrawSurfaceSection(materialEditor, properties);
        DrawSpecularSection(materialEditor, properties);
        DrawLightVolumeSection(materialEditor, properties);
        DrawBakerySection(materialEditor, properties);

        DrawCellReference();

        EditorGUILayout.Space();
        materialEditor.RenderQueueField();
        materialEditor.DoubleSidedGIField();

        if (!EditorGUI.EndChangeCheck()) return;

        // Every selected material, not only the one being drawn: the inspector edits them all
        // at once when several are selected.
        foreach (UnityEngine.Object target in materialEditor.targets)
        {
            if (target is Material selected) SyncBakeryKeywords(selected);
        }
    }

    // Also resync when the shader is first assigned, so a material switched onto this shader
    // starts with keywords matching the values it already carries.
    public override void AssignNewShaderToMaterial(Material material, Shader oldShader, Shader newShader)
    {
        base.AssignNewShaderToMaterial(material, oldShader, newShader);
        SyncBakeryKeywords(material);
    }

    // --- Surface -------------------------------------------------------------

    // Surface parameters for the lit shader. The emissive shaders have none of these, so the
    // whole section disappears for them rather than showing an empty header.
    void DrawSurfaceSection(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        MaterialProperty roughness = FindProperty("_Roughness", properties, false);
        if (roughness == null) return;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Surface", EditorStyles.boldLabel);

        materialEditor.ShaderProperty(roughness, "Roughness");
        DrawOptionalProperty(materialEditor, properties, "_Metallic", "Metallic");
        DrawOptionalProperty(materialEditor, properties, "_OcclusionStrength", "Occlusion");
    }

    // Specularity controls for the lit shader's Filament shading model.
    //
    // Folded away by default: the defaults are the ones to use unless a sign is visibly
    // wrong, and the surface parameters above are what actually gets tuned per material.
    bool _specularExpanded;

    void DrawSpecularSection(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        MaterialProperty reflectionStrength = FindProperty("_ReflectionStrength", properties, false);
        if (reflectionStrength == null) return;

        EditorGUILayout.Space();
        _specularExpanded = EditorGUILayout.Foldout(_specularExpanded, "Specularity", true);
        if (!_specularExpanded) return;

        EditorGUILayout.HelpBox(
            "Shading follows Google Filament's model, matching Mochie Standard's " +
            "\"Google Filament\" specular mode.",
            MessageType.Info);

        // Labels follow Mochie Standard's wording so a row here maps 1:1 onto the equivalent
        // row there. Only the case and spelling are ours; renaming these to something shorter
        // would make the two inspectors harder to compare side by side.
        materialEditor.ShaderProperty(reflectionStrength, "Environment reflections");
        DrawOptionalProperty(materialEditor, properties, "_SpecularHighlightStrength", "Specular highlights");
        DrawOptionalProperty(materialEditor, properties, "_IndirectSpecularOcclusionStrength", "Indirect specular occlusion");
        DrawOptionalProperty(materialEditor, properties, "_RealtimeSpecularOcclusionStrength", "Realtime specular occlusion");

        DrawDFGCheck(materialEditor, properties);
    }

    // VRC Light Volume controls for the lit shader.
    //
    // Folded away by default for the same reason Specularity is: the defaults match Mochie
    // Standard's, so a sign agrees with its surroundings without any of these being touched.
    bool _lightVolumeExpanded;

    void DrawLightVolumeSection(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        MaterialProperty additive = FindProperty("_AdditiveLightVolumesToggle", properties, false);
        if (additive == null) return;

        EditorGUILayout.Space();
        _lightVolumeExpanded = EditorGUILayout.Foldout(_lightVolumeExpanded, "Light volumes", true);
        if (!_lightVolumeExpanded) return;

        EditorGUILayout.HelpBox(
            "Where the scene has VRC Light Volumes, they replace light probes as this " +
            "material's indirect light. Nothing here has any effect in a scene without them.",
            MessageType.Info);

        materialEditor.ShaderProperty(additive, "Additive light volumes");
        DrawOptionalProperty(materialEditor, properties, "_LightVolumeBias", "Light volume bias");
        DrawOptionalProperty(materialEditor, properties, "_LightVolumeSpecularity", "Light volume highlights");
        DrawOptionalProperty(materialEditor, properties, "_LightVolumeSpecularityStrength", "Light volume highlight strength");
    }

    // Bakery MonoSH lightmap support for the lit shader.
    //
    // Only MonoSH is offered, not Bakery's SH or RNM modes: those need extra lightmap
    // textures (and RNM a tangent frame), while MonoSH reuses the directional lightmap Unity
    // already provides.
    bool _bakeryExpanded;

    void DrawBakerySection(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        MaterialProperty monoSH = FindProperty("_BakeryMonoSH", properties, false);
        if (monoSH == null) return;

        EditorGUILayout.Space();
        _bakeryExpanded = EditorGUILayout.Foldout(_bakeryExpanded, "Bakery", true);
        if (!_bakeryExpanded) return;

        EditorGUILayout.HelpBox(
            "Enable only for meshes baked with Bakery in MonoSH mode. A MonoSH lightmap is " +
            "stored in the same texture as a Unity directional lightmap but encoded " +
            "differently, so reading one as the other renders wrong either way round.",
            MessageType.Info);

        materialEditor.ShaderProperty(monoSH, "Bakery MonoSH");

        // The remaining options only do anything once MonoSH is decoding the lightmap.
        using (new EditorGUI.DisabledScope(monoSH.floatValue == 0))
        {
            DrawOptionalProperty(materialEditor, properties, "_BAKERY_LMSPEC", "Lightmap specular");
            DrawOptionalProperty(materialEditor, properties, "_BakeryLMSpecStrength", "Lightmap specular strength");
            DrawOptionalProperty(materialEditor, properties, "_BAKERY_SHNONLINEAR", "Non-linear SH");
        }
    }

    // Mirrors the Bakery toggles onto the shader keywords they drive.
    //
    // Unity's [Toggle] attribute would derive a keyword name from the property name, but these
    // keywords are Bakery's own and have to match what the shader code checks -- so the
    // properties are plain floats and the keywords are set here instead.
    //
    // LMSPEC and SHNONLINEAR are cleared whenever MonoSH is off, so a material cannot sit in a
    // state where a variant is compiled for options that do nothing.
    static void SyncBakeryKeywords(Material material)
    {
        if (!material.HasProperty("_BakeryMonoSH")) return;

        bool monoSH = material.GetFloat("_BakeryMonoSH") != 0;

        SetKeyword(material, "BAKERY_MONOSH", monoSH);
        SetKeyword(material, "BAKERY_LMSPEC",
            monoSH && material.HasProperty("_BAKERY_LMSPEC") && material.GetFloat("_BAKERY_LMSPEC") != 0);
        SetKeyword(material, "BAKERY_SHNONLINEAR",
            monoSH && material.HasProperty("_BAKERY_SHNONLINEAR") && material.GetFloat("_BAKERY_SHNONLINEAR") != 0);
    }

    static void SetKeyword(Material material, string keyword, bool state)
    {
        if (state) material.EnableKeyword(keyword);
        else material.DisableKeyword(keyword);
    }

    // The DFG lookup is a fixed data table the Filament model cannot work without. The
    // shader importer assigns it to new materials, but a material created before the
    // property existed, or one whose reference was cleared, would silently shade wrong --
    // so check rather than assume.
    void DrawDFGCheck(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        MaterialProperty dfg = FindProperty("_DFG", properties, false);
        if (dfg == null || dfg.textureValue != null) return;

        EditorGUILayout.HelpBox(
            "No DFG lookup assigned. The Filament shading model needs it; specular will be " +
            "wrong without it.",
            MessageType.Error);

        if (!GUILayout.Button("Assign DFG lookup")) return;

        const string dfgGuid = "a10c95bc318a7a444b0ea3229dd64b50";
        string path = AssetDatabase.GUIDToAssetPath(dfgGuid);
        var texture = string.IsNullOrEmpty(path)
            ? null
            : AssetDatabase.LoadAssetAtPath<Texture>(path);

        if (texture == null)
        {
            Debug.LogError(
                "Could not find the DFG lookup texture (Mochie's dfg-multiscatter.exr). " +
                "The lit SDFAtlas shader needs it for its Filament shading model.");
            return;
        }

        dfg.textureValue = texture;
    }

    // Draws a property only if the current shader declares it.
    static void DrawOptionalProperty(MaterialEditor materialEditor, MaterialProperty[] properties,
                                     string name, string label)
    {
        MaterialProperty property = FindProperty(name, properties, false);
        if (property == null) return;

        materialEditor.ShaderProperty(property, label);
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

    // Lists which graphic sits where in the atlas, as the normalised UV rectangle its artwork
    // occupies.
    //
    // This is a reference table, not a picker: which graphic a quad shows is decided by where
    // its UV island sits, and UVs are per-mesh rather than per-material, so nothing here can
    // assign a graphic to an object. What it does is save opening the manifest JSON to find
    // out where (say) the exit sign landed, so the island can be placed over it when
    // authoring the mesh.
    //
    // The rectangle is the artwork area, inside the packing padding. The padding holds
    // distance data continuing past the artwork's edge, which is there to keep the field
    // correct under filtering and mipping, not to be displayed -- so a UV island covering the
    // whole cell would show the graphic undersized with its neighbours' fields creeping in.
    void DrawCellReference()
    {
        if (_manifest == null || _manifest.cells == null) return;

        EditorGUILayout.Space();
        _cellListExpanded = EditorGUILayout.Foldout(_cellListExpanded, "Atlas contents", true);
        if (!_cellListExpanded) return;

        EditorGUILayout.HelpBox(
            "Place a quad's UV island over a graphic's rectangle to display it. Values are " +
            "normalised UVs as (uMin, vMin) to (uMax, vMax), with (0,0) at the bottom left.",
            MessageType.Info);

        _cellScroll = EditorGUILayout.BeginScrollView(_cellScroll, GUILayout.MaxHeight(200));

        for (int i = 0; i < _manifest.cells.Length; i++)
        {
            if (!_manifest.cells[i].occupied) continue;

            Rect uv = ArtworkUVRect(i);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(_manifest.cells[i].name);
            EditorGUILayout.LabelField(
                $"{uv.xMin:0.####}, {uv.yMin:0.####} - {uv.xMax:0.####}, {uv.yMax:0.####}",
                GUILayout.Width(190));

            // Copies the rectangle in the same order the label shows it, for pasting into
            // Blender or elsewhere while placing the island.
            if (GUILayout.Button("Copy UV", GUILayout.Width(70)))
            {
                EditorGUIUtility.systemCopyBuffer =
                    $"{uv.xMin:0.######}, {uv.yMin:0.######}, {uv.xMax:0.######}, {uv.yMax:0.######}";
            }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
    }

    // Normalised UV rectangle of a cell's artwork area, excluding its padding.
    Rect ArtworkUVRect(int cellIndex)
    {
        _manifest.IndexToCoord(cellIndex, out int cellX, out int cellY);
        _manifest.CellArtworkOrigin(cellX, cellY, out int texelX, out int texelY);

        float width = _manifest.TextureWidth;
        float height = _manifest.TextureHeight;

        return new Rect(texelX / width, texelY / height,
                        _manifest.ArtworkWidth / width, _manifest.ArtworkHeight / height);
    }

    // --- Spread, cross-checked against the manifest -------------------------

    // Spread is the one packing parameter the shader still needs at runtime: it converts
    // _EdgeBias from texels into stored distance units. A wrong value does not misplace the
    // graphic, it only misreads how far a bias should push the edge, so this is a mismatch
    // worth flagging but not an error that breaks display.
    void DrawSpreadSection(MaterialEditor materialEditor, Material material,
                           MaterialProperty spread)
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Layout", EditorStyles.boldLabel);

        if (_manifest == null)
        {
            EditorGUILayout.HelpBox(
                "No manifest found for this atlas. Spread below must be set by hand to match " +
                "how the atlas was packed, or edge bias will be scaled wrongly.",
                MessageType.Warning);
        }
        else if (!Mathf.Approximately(spread.floatValue, _manifest.spread))
        {
            EditorGUILayout.HelpBox(
                $"Spread does not match the atlas manifest, which packed at {_manifest.spread} texels.",
                MessageType.Warning);

            if (GUILayout.Button("Apply manifest spread"))
            {
                Undo.RecordObject(material, "Apply SDF atlas spread");
                spread.floatValue = _manifest.spread;
                EditorUtility.SetDirty(material);
            }
        }

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
