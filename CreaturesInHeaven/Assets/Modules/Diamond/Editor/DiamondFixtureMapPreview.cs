using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Drives scene-view material preview for all DiamondFixtureDefinition components in edit mode.
// Subscribes to EditorApplication.update once and iterates all active definitions each tick,
// keeping DiamondFixtureDefinition itself free of any UnityEditor API.
//
// Each fixture is previewed through the render path its owning DiamondManager selects via
// DiamondManager.PreviewMode (LiveProxy or BakedTexture -- an outright author choice, NOT
// tied to the runtime DriveMode):
//
//   LiveProxy    - read the animated proxy transforms and push driven colour/zoom/focus/
//                  intensity into per-fixture MaterialPropertyBlocks, keyword forced OFF.
//                  The original edit-mode preview; the only path that works without a bake.
//   BakedTexture - bind the bake texture + globals, enable DIAMOND_LIGHTSHOW_TEX, and drive
//                  the frame index from the manager's Time, so the scene view samples the
//                  ACTUAL bake (bake-accuracy check). Mirrors DiamondManager.StartBakedTexture
//                  / UpdateBakedTexture in editor C#. Udon doesn't run in edit mode, so nothing
//                  else would seed those globals -- this is the only way to see a bake off-play.
//
// A BakedTexture request whose bake is missing/stale falls back to the LiveProxy preview (with
// a one-shot console note) so the scene never goes dark mid-scrub -- unlike the runtime, which
// deliberately errors + stays dark. The editor is a WYSIWYG surface, not the ship target, so a
// visible fallback beats a black stage here.
[InitializeOnLoad]
public static class DiamondFixtureMapPreview
{
    // Per-definition MaterialPropertyBlocks, keyed by instance ID to avoid GC on every frame.
    // The head and beam each get their own block since their property names differ
    // (HeadRenderer uses _EmissionColor; BeamRenderer uses _Color / _EmitterWidth / etc).
    private static readonly Dictionary<int, MaterialPropertyBlock> _headBlocks = new();
    private static readonly Dictionary<int, MaterialPropertyBlock> _beamBlocks = new();

    // Shared global frame array for the baked preview, one slot per manager show
    // (index = manager.ShowIndex). Mirrors DiamondManager's _frames + the shader's
    // fixed-length _UdonDiamondLightshowFrames[DIAMOND_MAX_SHOWS]. Sized to that ceiling
    // (16, from DiamondLightshowSample.cginc) so several baked managers with distinct
    // ShowIndexes coexist in one tick, exactly as at runtime. Reused across ticks.
    private const int DiamondMaxShows = 16;   // == DIAMOND_MAX_SHOWS in DiamondLightshowSample.cginc
    private static readonly float[] _frames = new float[DiamondMaxShows];

    // Resolved manager-wide state for one manager, cached per manager per tick (see
    // _managerCache). Carries the atmosphere the proxy preview writes per beam, plus the
    // effective preview mode and a "bake globals already pushed this tick" latch so the
    // BakedTexture path seeds each manager's globals exactly once per tick.
    private struct ManagerState
    {
        public bool  HasManager;
        public float Haze;
        public float Scatter;
        public float Aniso;
        public float IntScale;

        // True once this manager resolves to the baked preview AND its bake validated.
        // When false, fixtures under it take the proxy preview (either PreviewMode ==
        // LiveProxy, or a BakedTexture request whose bake was stale).
        public bool  UseBaked;
        // Set the first time a fixture under this manager pushes its bake globals this tick,
        // so we don't re-push the show-wide globals once per fixture.
        public bool  GlobalsPushed;
    }

    // Per-tick cache of resolved manager state, keyed by manager instance ID. Cleared at the
    // top of each OnEditorUpdate. A "no manager" fixture is not cached (it just returns a
    // HasManager=false state with IntScale 1 and UseBaked false -> proxy preview).
    private static readonly Dictionary<int, ManagerState> _managerCache = new();

    // Managers we've already logged a stale-bake fallback for, so the BakedTexture->proxy
    // fallback note fires once per manager rather than every editor tick. Keyed by instance
    // ID; entries are pruned lazily when a manager's bake becomes valid again (see below).
    private static readonly HashSet<int> _staleBakeLogged = new();

    static DiamondFixtureMapPreview()
    {
        EditorApplication.update += OnEditorUpdate;
    }

    private static void OnEditorUpdate()
    {
        if (Application.isPlaying) return;

        var definitions = Object.FindObjectsByType<DiamondFixtureDefinition>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        // The atmosphere params (haze/scatter/anisotropy) and the beam-intensity
        // master live on DiamondManager, not the fixture, so the runtime only
        // applies them in Start/Update. In edit mode that never runs, so without
        // this the beams show frozen material-default haze while everything else
        // scrubs. A scene can have more than one manager, each owning its own
        // fixture subtree, so we can't grab a single global manager. Each fixture
        // must resolve its own via GetComponentInParent (as the bounds gizmo does).
        // Manager param resolution is cached per manager for this tick so we only
        // read its proxies once, not once per fixture under it.
        _managerCache.Clear();

        foreach (var def in definitions)
        {
            // The object graph lives on DiamondFixtureDefinition now (the driver is
            // retired), so read the refs straight off def.
            if (def.LampProps == null || def.HeadRenderer == null) continue;

            var manager = def.GetComponentInParent<DiamondManager>();
            var state   = ResolveManager(manager);

            int id = def.GetInstanceID();
            if (!_headBlocks.TryGetValue(id, out var headBlock))
            {
                headBlock = new MaterialPropertyBlock();
                _headBlocks[id] = headBlock;
            }
            if (!_beamBlocks.TryGetValue(id, out var beamBlock))
            {
                beamBlock = new MaterialPropertyBlock();
                _beamBlocks[id] = beamBlock;
            }

            if (state.UseBaked)
                PreviewBaked(def, manager, state, headBlock, beamBlock);
            else
                PreviewProxy(def, state, headBlock, beamBlock);
        }
    }

    // -------------------------------------------------------------------------
    //  LiveProxy preview -- read proxy transforms, push driven values per fixture.
    //  The original edit-mode preview path. Keyword forced OFF so the shaders read
    //  the block values below instead of a (nonexistent, in edit mode) bake sample.
    // -------------------------------------------------------------------------
    private static void PreviewProxy(
        DiamondFixtureDefinition def, ManagerState atmo,
        MaterialPropertyBlock headBlock, MaterialPropertyBlock beamBlock)
    {
        // Force the texture-path keyword OFF in edit mode on this fixture's beam and
        // lamp materials, so their shaders read the proxy values we're about to write
        // (below) instead of the bake texture. Udon doesn't run in edit mode, so the
        // texture path has no _UdonDiamondLightshowFrames to sample -- without this the
        // beam and lamp glow freeze/blank while scrubbing. DiamondManager re-enables it
        // in play. Idempotent, and DisableKeyword on a shader lacking it is a no-op.
        SetLightshowKeyword(def.BeamRenderer, false);
        SetLightshowKeyword(def.HeadRenderer, false);

        if (!def.LampProps.gameObject.activeSelf)
        {
            headBlock.SetColor("_EmissionColor", Color.black);
            def.HeadRenderer.SetPropertyBlock(headBlock);

            if (def.BeamRenderer != null)
            {
                beamBlock.SetColor("_Color", Color.clear);
                def.BeamRenderer.SetPropertyBlock(beamBlock);
            }
            return;
        }

        Color emission = def.Colour == DiamondFixtureDefinition.ColourMode.Blackbody
            ? DiamondFixtureDefinition.BlackbodyToRGB(def.ColourTemperature)
            : def.EmissionColor;

        float linearBrightness = def.LampProps.localPosition.y;
        // BeamProps is optional -- fixtures without a beam shaft just won't
        // have one wired up, in which case zoom/focus/intensity stay at defaults.
        float zoom          = def.BeamProps != null ? def.BeamProps.localEulerAngles.x : 0f;
        float focus         = def.BeamProps != null ? def.BeamProps.localPosition.y     : 1f;
        float beamIntensity = def.BeamProps != null ? def.BeamProps.localScale.y       : 1f;
        Color drivenColour  = emission * linearBrightness;

        headBlock.SetColor("_EmissionColor", drivenColour);
        def.HeadRenderer.SetPropertyBlock(headBlock);

        // Mirror onto the beam shaft: brightness-modulated colour, animated
        // intensity, animated zoom (stored as tan(half-angle)), animated
        // focus (0-1 direct pass-through), and the emitter dimensions from
        // the profile (via def.FixtureEmitterSize).
        if (def.BeamRenderer != null)
        {
            Vector2 emitter = def.FixtureEmitterSize;
            beamBlock.SetColor("_Color", drivenColour);
            beamBlock.SetFloat("_EmitterWidth",  emitter.x);
            beamBlock.SetFloat("_EmitterHeight", emitter.y);
            // BeamIntensityScale is a manager-wide multiplier on the shaft
            // intensity, matching ApplyFixture's beamIntensity * BeamIntensityScale.
            beamBlock.SetFloat("_BeamIntensity", beamIntensity * atmo.IntScale);
            beamBlock.SetFloat("_ZoomX",         zoom);
            beamBlock.SetFloat("_Focus",         focus);

            // Manager-wide atmosphere. Only written when this fixture has a
            // manager in its parent chain; otherwise leave the material's
            // serialized values alone.
            if (atmo.HasManager)
            {
                beamBlock.SetFloat("_HazeDensity",     atmo.Haze);
                beamBlock.SetFloat("_ScatterStrength", atmo.Scatter);
                beamBlock.SetFloat("_Anisotropy",      atmo.Aniso);
            }

            // Match the runtime manager: round (symmetric) beams use the
            // BeamRound shader, which reads only _ZoomX. Only rect beams
            // need _ZoomZ.
            if (!def.SymmetricBeam)
                beamBlock.SetFloat("_ZoomZ",     zoom);

            def.BeamRenderer.SetPropertyBlock(beamBlock);
        }
    }

    // -------------------------------------------------------------------------
    //  BakedTexture preview -- sample the actual bake, exactly as the runtime GPU
    //  path does, so the scene view verifies bake accuracy off-play. Mirrors
    //  DiamondManager.StartBakedTexture (per-fixture _FixtureRow/_ShowIndex + emitter
    //  seeding, keyword ON) and UpdateBakedTexture (frame index from Time). The
    //  show-wide globals are pushed once per manager per tick (see PushBakeGlobals).
    // -------------------------------------------------------------------------
    private static void PreviewBaked(
        DiamondFixtureDefinition def, DiamondManager manager, ManagerState atmo,
        MaterialPropertyBlock headBlock, MaterialPropertyBlock beamBlock)
    {
        // Turn the texture path ON (opposite of the proxy preview) so the shaders sample
        // the bake instead of the per-fixture block colour. The block still carries
        // _FixtureRow/_ShowIndex + emitter dims, which the baked shader reads.
        SetLightshowKeyword(def.BeamRenderer, true);
        SetLightshowKeyword(def.HeadRenderer, true);

        // Fixture row: the fixture's index in the manager's arrays IS its bake row. The
        // runtime seeds `(float)i` from its Start loop; here we find i by matching the
        // fixture's LampProps against the manager's LampProps array (the definition
        // doesn't store its own index). Not found -> leave the fixture on defaults.
        int row = FixtureRow(manager, def);
        if (row < 0) return;

        float showIndex = manager.ShowIndex;

        // Head block: the lamp-glow pass samples the bake row + show slot, same as the
        // runtime StartBakedTexture head seed.
        headBlock.SetFloat("_FixtureRow", row);
        headBlock.SetFloat("_ShowIndex",  showIndex);
        def.HeadRenderer.SetPropertyBlock(headBlock);

        if (def.BeamRenderer != null)
        {
            // Beam block: bake row + show slot for the sample, plus the static emitter
            // dims the shaft shader reads from the block regardless of path.
            Vector2 emitter = def.FixtureEmitterSize;
            beamBlock.SetFloat("_FixtureRow",    row);
            beamBlock.SetFloat("_ShowIndex",     showIndex);
            beamBlock.SetFloat("_EmitterWidth",  emitter.x);
            beamBlock.SetFloat("_EmitterHeight", emitter.y);

            // Static manager-wide atmosphere, seeded per-block exactly as the runtime does
            // in StartBakedTexture (via the shared blocks StartShared built). Animated
            // atmosphere is pushed as a global in PushBakeGlobals, matching
            // PushAnimatedAtmosphereGlobal.
            if (atmo.HasManager)
            {
                beamBlock.SetFloat("_HazeDensity",     atmo.Haze);
                beamBlock.SetFloat("_ScatterStrength", atmo.Scatter);
                beamBlock.SetFloat("_Anisotropy",      atmo.Aniso);
            }

            def.BeamRenderer.SetPropertyBlock(beamBlock);
        }
    }

    // Finds a fixture's bake row: its index in the manager's LampProps array (which the
    // bake rows are aligned to). Returns -1 if not found (stale mapping / fixture not in
    // this manager's set). Linear scan -- fine at edit-time preview scale.
    private static int FixtureRow(DiamondManager manager, DiamondFixtureDefinition def)
    {
        var props = manager.LampProps;
        if (props == null) return -1;
        for (int i = 0; i < props.Length; i++)
            if (props[i] == def.LampProps) return i;
        return -1;
    }

    // Pushes the show-wide bake globals for one manager: the bake texture + packing
    // constants, the master beam-intensity scale, and this manager's frame column into its
    // ShowIndex slot of the shared _frames array. Mirrors DiamondManager.StartBakedTexture's
    // global seed + UpdateBakedTexture's frame publish. Called once per manager per tick
    // (latched by ManagerState.GlobalsPushed). The per-fixture _FixtureRow/_ShowIndex are
    // NOT set here -- those are per-block, done in PreviewBaked.
    private static void PushBakeGlobals(DiamondManager manager)
    {
        Shader.SetGlobalTexture("_UdonDiamondLightshowTex", manager.LightshowTex);
        Shader.SetGlobalFloat("_UdonDiamondLightshowTexelsPerFixture", manager.LightshowTexelsPerFixture);
        Shader.SetGlobalFloat("_UdonDiamondLightshowColourScale",      manager.LightshowColourScale);
        Shader.SetGlobalFloat("_UdonDiamondLightshowBeamScale",        manager.LightshowBeamScale);
        Shader.SetGlobalFloat("_UdonDiamondLightshowFrameCount",       manager.LightshowFrameCount);
        Shader.SetGlobalFloat("_UdonDiamondBeamIntensityScale",        manager.BeamIntensityScale);

        // Frame column from the manager's normalised [0,1] time, exactly as
        // UpdateBakedTexture computes it. In edit mode the animator param isn't driven,
        // so scrub via the manager's serialized Time field (the inspector slider) or an
        // animation clip keying it. ShowIndex is clamped to the shared array so a manager
        // configured beyond DIAMOND_MAX_SHOWS can't index out of bounds.
        float t     = Mathf.Clamp01(manager.Time);
        float frame = t * (manager.LightshowFrameCount - 1);
        int   slot  = Mathf.Clamp(manager.ShowIndex, 0, DiamondMaxShows - 1);
        _frames[slot] = frame;
        Shader.SetGlobalFloatArray("_UdonDiamondLightshowFrames", _frames);
    }

    // Forces DIAMOND_LIGHTSHOW_TEX on/off across all of a renderer's shared materials
    // (the lamp lens carries Mochie + the Diamond glow, so we check every slot). Guarded
    // so we only touch a material whose state actually differs -- otherwise this would
    // dirty the material asset every editor tick. A material whose shader doesn't declare
    // the keyword just never reports it enabled, so the guard skips it.
    private static void SetLightshowKeyword(Renderer r, bool on)
    {
        if (r == null) return;
        var mats = r.sharedMaterials;
        if (mats == null) return;
        foreach (var m in mats)
        {
            if (m == null) continue;
            if (m.IsKeywordEnabled("DIAMOND_LIGHTSHOW_TEX") == on) continue;
            if (on) m.EnableKeyword("DIAMOND_LIGHTSHOW_TEX");
            else    m.DisableKeyword("DIAMOND_LIGHTSHOW_TEX");
        }
    }

    // Resolves a manager's per-tick state: its atmosphere (for the proxy preview), whether
    // it should preview baked, and -- if baked -- pushes its show-wide bake globals once.
    // Cached per manager for the current tick, so N fixtures under one manager only resolve
    // it (and push its globals) once. Mirrors DiamondManager's static-vs-proxy atmosphere
    // resolution and the runtime haze/scatter clamp via the manager's own resolver.
    private static ManagerState ResolveManager(DiamondManager manager)
    {
        if (manager == null)
            return new ManagerState { HasManager = false, IntScale = 1f, UseBaked = false };

        int key = manager.GetInstanceID();
        if (_managerCache.TryGetValue(key, out var cached))
            return cached;

        // Resolve through the manager's own canonical resolver, so the preview reads
        // atmosphere exactly the way the runtime does -- same static-or-proxy choice, same
        // haze/scatter clamp -- instead of re-implementing (and drifting from) that logic
        // here. In edit mode the UdonSharpBehaviour runs as plain C#, so this is a direct
        // method call, no Udon VM. Single source of truth: DiamondManager.ResolveAtmosphere.
        float haze, scatter, aniso, intScale;
        manager.ResolveAtmosphere(out haze, out scatter, out aniso, out intScale);

        // Only BakedTexture (with a valid bake) takes the baked preview path; LiveProxy is
        // the proxy preview. PreviewMode is an outright author choice -- it does NOT follow
        // the runtime DriveMode.
        bool wantsBaked = manager.PreviewMode == DiamondPreviewMode.BakedTexture;
        bool useBaked   = wantsBaked && BakeIsValid(manager);

        // A BakedTexture request whose bake is missing/stale: fall back to the proxy preview
        // and note it once. (Unlike the runtime, which errors + goes dark -- the editor is a
        // preview surface, so a visible fallback is friendlier than a black scene.) Prune the
        // "already logged" latch when the bake goes valid again, so a later break re-notifies.
        if (wantsBaked && !useBaked)
        {
            if (_staleBakeLogged.Add(key))
                Debug.LogWarning("[Diamond] " + manager.name + ": PreviewMode wants BakedTexture " +
                    "but the bake is missing or stale (no LightshowTex, LightshowFixtureCount != " +
                    "fixture count, or LightshowFrameCount <= 0). Previewing LiveProxy instead. " +
                    "Re-bake to preview the baked result.", manager);
        }
        else
        {
            _staleBakeLogged.Remove(key);
        }

        if (useBaked)
            PushBakeGlobals(manager);

        var state = new ManagerState
        {
            HasManager    = true,
            Haze          = haze,
            Scatter       = scatter,
            Aniso         = aniso,
            IntScale      = intScale,
            UseBaked      = useBaked,
            GlobalsPushed = useBaked,   // PushBakeGlobals ran above for this manager
        };
        _managerCache[key] = state;
        return state;
    }

    // The same predicate DiamondManager.ValidateBake uses at runtime, re-checked here so the
    // preview only takes the baked path when there's actually a valid, current bake to sample.
    private static bool BakeIsValid(DiamondManager manager)
    {
        int count = manager.LampProps == null ? 0 : manager.LampProps.Length;
        return manager.LightshowTex != null
            && manager.LightshowFixtureCount == count
            && manager.LightshowFrameCount > 0;
    }
}
