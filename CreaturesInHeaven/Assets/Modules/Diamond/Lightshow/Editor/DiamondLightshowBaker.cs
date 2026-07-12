using UnityEngine;
using UnityEditor;

// Offline baker: steps a lighting-rig AnimationClip frame-by-frame and records each
// fixture's driven shader values into an RGBA32 lookup texture, so at runtime the
// fixtures' shaders sample their own row on the GPU instead of DiamondManager reading
// every proxy transform in an Udon loop (which boxes a Vector3 per read -- the whole
// reason this system exists; see DIAMOND-GPU-ACCEL.md and memory
// udon_transform_read_boxing.md).
//
// This is the Diamond analogue of the Moment ALV baker, but far simpler: the sample
// is a plain transform read (no Bakery render, no async finish callback), so the whole
// bake is one synchronous loop with a progress bar. It reuses Moment's two hard-won
// patterns: AnimationMode.SampleAnimationClip for force-evaluating a pose, and the
// GUID-preserving in-place Texture2D write (LoadAssetAtPath + SetPixels, never PNG).
//
// Note: Moment defers a tick after SampleAnimationClip before reading, but that's
// because Bakery reads scene *geometry* (which lags a frame). SampleAnimationClip
// writes transform local values synchronously, so reading localPosition/localScale/
// localEulerAngles right after the call returns the sampled pose -- no settle needed
// here. (If a baked frame ever looks one-frame-stale, revisit this assumption.)
//
// The row order is the SAME crawl DiamondManagerDefinition.BakeFixtures uses
// (GetComponentsInChildren<DiamondFixtureDefinition>), so fixture i's runtime
// _FixtureRow = i lines up with the row baked here. Re-run BakeFixtures and this baker
// together after any fixture add/remove/reorder.
//
// Opens via Tools > Diamond > Bake lightshow...
public class DiamondLightshowBaker : EditorWindow
{
    // --- Window state ---------------------------------------------------

    DiamondManager _manager;
    AnimationClip _clip;

    // The Animator the clip's paths are relative to. SampleAnimationClip must be given
    // THIS GameObject as root, or the proxy transforms won't move and the bake comes out
    // all-static (a silent, nasty failure). Auto-resolved from the manager when possible
    // (GetComponentInChildren<Animator>), but exposed so it can be corrected if the rig's
    // Animator isn't under the manager root.
    Animator _animator;

    // Bake resolution: frames sampled = round(clip length * frameRate) + 1 (inclusive
    // of the final frame). Overridable so a quick low-res test bake is possible.
    int _frameRate = 60;
    bool _overrideFrameRate = false;

    string _lastMessage = "";
    MessageType _lastMessageType = MessageType.Info;

    [MenuItem("Tools/Diamond/Bake lightshow...")]
    static void Open() => GetWindow<DiamondLightshowBaker>("Bake Diamond Lightshow");

    void OnGUI()
    {
        EditorGUILayout.LabelField("Diamond Lightshow Baker", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Samples the clip at every frame and bakes each fixture's driven colour, " +
            "zoom, focus, and beam intensity into a lookup texture. Run \"Bake fixtures\" " +
            "on the DiamondManagerDefinition first so the fixture arrays are current.",
            MessageType.None);

        EditorGUILayout.Space();

        EditorGUI.BeginChangeCheck();
        _manager = (DiamondManager)EditorGUILayout.ObjectField(
            "Manager", _manager, typeof(DiamondManager), true);
        // Auto-fill the animator from the manager when the manager changes and none is set.
        if (EditorGUI.EndChangeCheck() && _manager != null && _animator == null)
            _animator = _manager.GetComponentInChildren<Animator>();

        _animator = (Animator)EditorGUILayout.ObjectField(
            "Animator (clip root)", _animator, typeof(Animator), true);
        _clip = (AnimationClip)EditorGUILayout.ObjectField(
            "Rig clip", _clip, typeof(AnimationClip), false);

        _overrideFrameRate = EditorGUILayout.Toggle("Override frame rate", _overrideFrameRate);
        using (new EditorGUI.DisabledScope(!_overrideFrameRate))
            _frameRate = EditorGUILayout.IntField("Frame rate", _frameRate);

        EditorGUILayout.Space();

        // Live preview of what the bake would produce, before committing.
        if (_manager != null && _clip != null && _animator != null)
        {
            int rate = _overrideFrameRate ? Mathf.Max(1, _frameRate) : Mathf.RoundToInt(_clip.frameRate);
            int frames = Mathf.RoundToInt(_clip.length * rate) + 1;
            int fixtures = _manager.LampProps != null ? _manager.LampProps.Length : 0;
            int w = DiamondLightshowFormat.FlatWidth(frames);
            int h = DiamondLightshowFormat.FlatHeight(fixtures);
            long bytes = (long)w * h * 4;
            bool fits = DiamondLightshowFormat.FitsFlat(frames, fixtures);

            EditorGUILayout.HelpBox(
                $"{fixtures} fixtures x {frames} frames @ {rate}fps\n" +
                $"Texture: {w} x {h} RGBA32 ({bytes / (1024f * 1024f):0.0} MB) -- column=frame, row=fixture*2+slot" +
                (fits ? "" : $"\nAN AXIS EXCEEDS {DiamondLightshowFormat.MaxTextureAxis} CAP -- wrap not yet implemented."),
                fits ? MessageType.Info : MessageType.Error);

            using (new EditorGUI.DisabledScope(!fits || fixtures == 0))
                if (GUILayout.Button("Bake lightshow", GUILayout.Height(28)))
                    Bake(rate, frames, fixtures);
        }
        else
        {
            EditorGUILayout.HelpBox("Assign a DiamondManager, its Animator (the clip's path root), and the rig clip.", MessageType.Info);
        }

        if (!string.IsNullOrEmpty(_lastMessage))
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(_lastMessage, _lastMessageType);
        }
    }

    // --- Bake -----------------------------------------------------------

    void Bake(int rate, int frameCount, int fixtureCount)
    {
        // Snapshot the fixture references off the manager. These are the same
        // index-aligned arrays DiamondManagerDefinition.BakeFixtures filled, so row i
        // here == _FixtureRow i at runtime.
        Transform[] lampProps = _manager.LampProps;
        Transform[] beamProps = _manager.BeamProps;

        if (lampProps == null || lampProps.Length != fixtureCount)
        {
            SetMessage("Manager fixture arrays are empty or stale. Run \"Bake fixtures\" first.", MessageType.Error);
            return;
        }

        // Sample against the Animator's GameObject: the clip's paths are relative to it,
        // so this is the root SampleAnimationClip must be given for the proxy transforms
        // to move. (Using the manager root would silently produce a static bake if the
        // manager sits above the Animator.)
        GameObject root = _animator.gameObject;

        // Two passes over cached samples. Pass 1 steps the clip once, reading every
        // fixture's channels into `raw` and measuring the HDR peaks (drivenColour and
        // beamIntensity) across the whole show. Pass 2 re-visits `raw` (NOT the clip) to
        // scale by those peaks into [0,1] and pack the pixels -- the peak isn't known
        // until every frame is seen, so the scale can only be applied after pass 1.
        // Caching avoids stepping the clip twice; at 420x5568 that's ~2.3M small structs,
        // a few tens of MB transiently in the editor, which is fine.
        var raw = new RawSample[frameCount * fixtureCount];

        bool started = !AnimationMode.InAnimationMode();
        if (started) AnimationMode.StartAnimationMode();

        try
        {
            float colourPeak = 1e-6f;
            float beamPeak    = 1e-6f;
            bool colourOverflow = false;

            // --- Pass 1: sample + measure peaks ---
            for (int f = 0; f < frameCount; f++)
            {
                if (EditorUtility.DisplayCancelableProgressBar(
                        "Baking Diamond lightshow", $"Sampling frame {f + 1}/{frameCount}",
                        0.5f * f / frameCount))
                {
                    Cleanup(started);
                    SetMessage("Bake cancelled.", MessageType.Warning);
                    return;
                }

                float t = frameCount > 1 ? _clip.length * f / (frameCount - 1) : 0f;
                AnimationMode.SampleAnimationClip(root, _clip, t);

                for (int i = 0; i < fixtureCount; i++)
                {
                    Transform lamp = lampProps[i];
                    Transform beam = beamProps != null && i < beamProps.Length ? beamProps[i] : null;

                    RawSample s = default;

                    if (lamp != null)
                    {
                        // Off-ness mirrors DiamondManager.IsLightOff: inactive proxy,
                        // zero brightness, or black colour all bake as zero output.
                        bool active = lamp.gameObject.activeSelf;
                        float brightness = lamp.localPosition.y;
                        Vector3 colour = lamp.localScale;
                        bool off = !active || brightness == 0f
                                   || (colour.x == 0f && colour.y == 0f && colour.z == 0f);

                        if (!off)
                        {
                            // drivenColour = colour * brightness, shared by head + beam.
                            s.cr = colour.x * brightness;
                            s.cg = colour.y * brightness;
                            s.cb = colour.z * brightness;

                            // colour proxy is assumed SDR [0,1]; flag if authored beyond.
                            if (colour.x > 1f || colour.y > 1f || colour.z > 1f) colourOverflow = true;

                            if (beam != null)
                            {
                                s.zoom  = beam.localEulerAngles.x;
                                s.focus = beam.localPosition.y;
                                s.beam  = beam.localScale.y;
                            }
                            s.on = true;

                            colourPeak = Mathf.Max(colourPeak, Mathf.Max(s.cr, Mathf.Max(s.cg, s.cb)));
                            beamPeak    = Mathf.Max(beamPeak, s.beam);
                        }
                    }

                    raw[f * fixtureCount + i] = s;
                }
            }

            // --- Pass 2: encode into pixels ---
            // Vertical-stacked layout: column = frame, row = fixture*2 + slot. A fixture
            // owns two adjacent rows (colour, beam); the shader reads them as a vertical
            // pair at the frame's column. See DiamondLightshowFormat.
            int w = DiamondLightshowFormat.FlatWidth(frameCount);
            int h = DiamondLightshowFormat.FlatHeight(fixtureCount);
            var pixels = new Color[w * h];

            float invColour = 1f / colourPeak;
            float invBeam   = 1f / beamPeak;

            for (int i = 0; i < fixtureCount; i++)
            {
                if (EditorUtility.DisplayCancelableProgressBar(
                        "Baking Diamond lightshow", $"Encoding fixture {i + 1}/{fixtureCount}",
                        0.5f + 0.5f * i / fixtureCount))
                {
                    Cleanup(started);
                    SetMessage("Bake cancelled.", MessageType.Warning);
                    return;
                }

                int colourRow = DiamondLightshowFormat.RowOf(i, DiamondLightshowFormat.SlotColour);
                int beamRow   = DiamondLightshowFormat.RowOf(i, DiamondLightshowFormat.SlotBeam);

                for (int f = 0; f < frameCount; f++)
                {
                    RawSample s = raw[f * fixtureCount + i];

                    // Same column f, two rows.
                    int colourIdx = colourRow * w + f;
                    int beamIdx   = beamRow   * w + f;

                    if (s.on)
                    {
                        pixels[colourIdx] = new Color(
                            s.cr * invColour, s.cg * invColour, s.cb * invColour, 1f);
                        pixels[beamIdx] = new Color(
                            s.zoom, s.focus, s.beam * invBeam, 1f);
                    }
                    else
                    {
                        // Off bakes as zero colour (shader early-outs on black), and the
                        // beam row is irrelevant when colour is zero.
                        pixels[colourIdx] = Color.clear;
                        pixels[beamIdx]   = Color.clear;
                    }
                }
            }

            // --- Write the texture as a PNG with enforced data-texture import settings ---
            string assetPath = ResolveAssetPath();
            Texture2D tex = WriteTexturePng(assetPath, pixels, w, h);

            // --- Write the descriptor onto the manager + definition ---
            WriteDescriptor(tex, frameCount, fixtureCount, colourPeak, beamPeak);

            // Enable the shader keyword on the beam materials at EDIT TIME so the texture
            // path is on the moment the bake exists, independent of whether Udon's
            // Material.EnableKeyword works at runtime. Persists on the material asset.
            EnableLightshowKeyword();

            Cleanup(started);

            // Surface this manager's show slot and flag any collision. ShowIndex is set by
            // hand in the inspector (not baked), so a second manager left at the default 0
            // would share slot 0 with the first: both write _UdonDiamondLightshowFrames[0]
            // and every fixture under both reads the same frame -- a silent cross-talk bug,
            // exactly the multi-manager case the slot axis exists to prevent. Nothing else
            // warns, so the baker does.
            string showSlotWarn = DescribeShowIndexCollision(_manager);

            string warn = colourOverflow
                ? " WARNING: a colour proxy exceeded 1.0 (HDR colour authoring); it was scaled into range with the intensity peak, which may not be intended."
                : "";
            var msgType = (colourOverflow || showSlotWarn != "") ? MessageType.Warning : MessageType.Info;
            SetMessage(
                $"Baked {fixtureCount} fixtures x {frameCount} frames to {assetPath}\n" +
                $"ShowIndex (frame-array slot) = {_manager.ShowIndex}.\n" +
                $"ColourScale (peak) = {colourPeak:0.###}, BeamIntensityScale (peak) = {beamPeak:0.###}.{warn}{showSlotWarn}",
                msgType);
            Debug.Log($"[Diamond] Lightshow bake complete: {assetPath} " +
                      $"({w}x{h} RGBA32, ShowIndex={_manager.ShowIndex}, colourPeak={colourPeak:0.###}, beamPeak={beamPeak:0.###})", tex);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    // Encodes the pixels to a PNG file and enforces the import settings a DATA texture
    // needs, so the imported Texture2D holds exactly the baked bytes (no gamma, no
    // compression, no mips, no filtering/rows bleeding). PNG so the asset is a normal
    // image you can open and eyeball; RGBA32 PNG is lossless, so nothing is lost versus
    // a native .asset. The baker RE-ASSERTS these importer settings on every bake, so a
    // stray manual change can't corrupt a later bake (this is why enforcing-in-code, not
    // the file type, is what actually guarantees correctness).
    //
    // Returns the imported Texture2D (loaded back after reimport) for the descriptor.
    static Texture2D WriteTexturePng(string assetPath, Color[] pixels, int w, int h)
    {
        // Build a transient RGBA32 texture just to encode. filterMode etc. here don't
        // matter -- the imported asset's settings (below) are what the GPU sees.
        var tmp = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
        tmp.SetPixels(pixels);
        tmp.Apply(false);
        byte[] png = tmp.EncodeToPNG();
        Object.DestroyImmediate(tmp);

        // Write via the filesystem (absolute path), then import.
        string absPath = System.IO.Path.Combine(Application.dataPath, "..", assetPath)
            .Replace('\\', '/');
        System.IO.File.WriteAllBytes(absPath, png);
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

        // Enforce data-texture import settings. Re-applied every bake so they can't drift.
        var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
        if (importer != null)
        {
            importer.textureType        = TextureImporterType.Default;
            importer.sRGBTexture        = false;                       // raw values, no gamma
            importer.mipmapEnabled      = false;
            importer.isReadable         = false;                       // GPU-only; no CPU copy needed at runtime
            importer.filterMode         = FilterMode.Point;            // shader lerps frames itself; rows must not blend
            importer.wrapMode           = TextureWrapMode.Clamp;
            importer.npotScale          = TextureImporterNPOTScale.None;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize     = 16384;                       // don't downscale the wide strip
            importer.alphaSource        = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.SaveAndReimport();
        }
        else
        {
            Debug.LogWarning($"[Diamond] No TextureImporter at {assetPath}; import settings NOT enforced. The bake may be gamma/compression-corrupted.");
        }

        return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
    }

    // Enables DIAMOND_LIGHTSHOW_TEX on every distinct material the manager owns (beam AND
    // lamp-lens), at edit time, and marks them dirty so it saves onto the asset.
    // Belt-and-suspenders with the runtime EnableKeyword in DiamondManager.Start (in case
    // Udon can't call it). Matches the manager's runtime enable exactly: both renderer
    // arrays, and sharedMaterialS (plural) since the lamp lens carries Mochie + the glow
    // pass -- .sharedMaterial (singular) would miss the glow slot, so the lamp glow would
    // stay on the edit-preview path in a fresh play session until the manager re-enabled it.
    void EnableLightshowKeyword()
    {
        var seen = new System.Collections.Generic.HashSet<Material>();
        EnableKeywordOn(_manager.BeamRenderers, seen);
        EnableKeywordOn(_manager.HeadRenderers, seen);
        AssetDatabase.SaveAssets();
    }

    // Enables the keyword on each distinct shared material across a renderer array. `seen`
    // dedupes across both arrays so a material shared by beam and head is touched once.
    static void EnableKeywordOn(Renderer[] renderers, System.Collections.Generic.HashSet<Material> seen)
    {
        if (renderers == null) return;
        foreach (var r in renderers)
        {
            if (r == null) continue;
            foreach (var m in r.sharedMaterials)   // plural: lamp lens is Mochie + glow
            {
                if (m == null || !seen.Add(m)) continue;
                // A material whose shader doesn't declare the keyword (e.g. Mochie) just
                // never reports it enabled; enabling is a harmless no-op there.
                if (!m.IsKeywordEnabled("DIAMOND_LIGHTSHOW_TEX"))
                {
                    m.EnableKeyword("DIAMOND_LIGHTSHOW_TEX");
                    EditorUtility.SetDirty(m);
                }
            }
        }
    }

    // Stores the descriptor on the DiamondManager (runtime-readable serialized fields,
    // seeded into shaders at Start) via SerializedObject so it persists and marks dirty.
    void WriteDescriptor(Texture2D tex, int frameCount, int fixtureCount, float colourPeak, float beamPeak)
    {
        var so = new SerializedObject(_manager);
        SetObj(so,   "LightshowTex",          tex);
        SetInt(so,   "LightshowFrameCount",   frameCount);
        SetInt(so,   "LightshowFixtureCount", fixtureCount);
        SetInt(so,   "LightshowTexelsPerFixture", DiamondLightshowFormat.TexelsPerFixture);
        SetFloat(so, "LightshowColourScale",  colourPeak);
        SetFloat(so, "LightshowBeamScale",    beamPeak);
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(_manager);
    }

    static void SetObj(SerializedObject so, string prop, Object v)
    {
        var p = so.FindProperty(prop);
        if (p != null) p.objectReferenceValue = v;
        else Debug.LogWarning($"[Diamond] DiamondManager has no serialized field '{prop}'; descriptor not fully written.");
    }
    static void SetInt(SerializedObject so, string prop, int v)
    {
        var p = so.FindProperty(prop);
        if (p != null) p.intValue = v;
        else Debug.LogWarning($"[Diamond] DiamondManager has no serialized field '{prop}'; descriptor not fully written.");
    }
    static void SetFloat(SerializedObject so, string prop, float v)
    {
        var p = so.FindProperty(prop);
        if (p != null) p.floatValue = v;
        else Debug.LogWarning($"[Diamond] DiamondManager has no serialized field '{prop}'; descriptor not fully written.");
    }

    // Returns a warning string if any OTHER active DiamondManager in the scene shares
    // this manager's ShowIndex (they'd collide on the same _UdonDiamondLightshowFrames
    // slot), or "" if the slot is unique. Only meaningful for multi-manager scenes; a
    // lone manager is always fine at the default 0.
    static string DescribeShowIndexCollision(DiamondManager manager)
    {
        var all = Object.FindObjectsByType<DiamondManager>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var clashes = new System.Collections.Generic.List<string>();
        foreach (var other in all)
        {
            if (other == manager) continue;
            if (other.ShowIndex == manager.ShowIndex)
                clashes.Add(other.gameObject.name);
        }
        if (clashes.Count == 0) return "";
        return $"\nWARNING: ShowIndex {manager.ShowIndex} is also used by: {string.Join(", ", clashes)}. " +
               "Give each concurrent manager a distinct ShowIndex, or they'll share one frame-array slot " +
               "and read each other's playback position.";
    }

    // Places the texture next to the clip, named after the manager, so multiple
    // managers/shows in one project don't collide.
    string ResolveAssetPath()
    {
        string clipPath = AssetDatabase.GetAssetPath(_clip);
        string dir = string.IsNullOrEmpty(clipPath) ? "Assets" : System.IO.Path.GetDirectoryName(clipPath);
        string safeName = MakeSafe(_manager.gameObject.scene.name + "_" + _manager.gameObject.name);
        return $"{dir}/DiamondLightshow_{safeName}.png".Replace('\\', '/');
    }

    static string MakeSafe(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    void Cleanup(bool started)
    {
        if (started && AnimationMode.InAnimationMode())
            AnimationMode.StopAnimationMode();
    }

    void SetMessage(string msg, MessageType type)
    {
        _lastMessage = msg;
        _lastMessageType = type;
        Repaint();
    }

    // One sampled fixture-frame before encoding. drivenColour is colour*brightness;
    // zoom/focus/beam are the raw beam proxy channels. 'on' is false for off fixtures.
    struct RawSample
    {
        public float cr, cg, cb;   // drivenColour rgb (HDR, pre-scale)
        public float zoom, focus;  // beam zoom (tan half-angle), focus (0..1)
        public float beam;         // beam intensity (HDR, pre-scale)
        public bool  on;
    }
}
