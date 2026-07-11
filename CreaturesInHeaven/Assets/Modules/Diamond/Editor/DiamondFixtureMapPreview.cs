using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Drives scene-view material preview for all DiamondFixtureDefinition components in edit mode.
// Subscribes to EditorApplication.update once and iterates all active definitions each tick,
// keeping DiamondFixtureDefinition itself free of any UnityEditor API.
[InitializeOnLoad]
public static class DiamondFixtureMapPreview
{
    // Per-definition MaterialPropertyBlocks, keyed by instance ID to avoid GC on every frame.
    // The head and beam each get their own block since their property names differ
    // (HeadRenderer uses _EmissionColor; BeamRenderer uses _Color / _EmitterWidth / etc).
    private static readonly Dictionary<int, MaterialPropertyBlock> _headBlocks = new();
    private static readonly Dictionary<int, MaterialPropertyBlock> _beamBlocks = new();

    // Resolved manager-wide atmosphere for one fixture. Cached per manager per tick
    // (see _atmoCache) so proxies are read once per manager, not once per fixture.
    private struct Atmo
    {
        public bool  HasManager;
        public float Haze;
        public float Scatter;
        public float Aniso;
        public float IntScale;
    }

    // Per-tick cache of resolved atmosphere, keyed by manager instance ID. Cleared
    // at the top of each OnEditorUpdate. A "no manager" fixture is not cached (it
    // just returns a HasManager=false Atmo with IntScale 1).
    private static readonly Dictionary<int, Atmo> _atmoCache = new();

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
        _atmoCache.Clear();

        foreach (var def in definitions)
        {
            // The object graph lives on DiamondFixtureDefinition now (the driver is
            // retired), so read the refs straight off def.
            if (def.LampProps == null || def.HeadRenderer == null) continue;

            var atmo = ResolveAtmo(def);

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

            if (!def.LampProps.gameObject.activeSelf)
            {
                headBlock.SetColor("_EmissionColor", Color.black);
                def.HeadRenderer.SetPropertyBlock(headBlock);

                if (def.BeamRenderer != null)
                {
                    beamBlock.SetColor("_Color", Color.clear);
                    def.BeamRenderer.SetPropertyBlock(beamBlock);
                }
                continue;
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
    }

    // Resolves the manager-wide atmosphere for one fixture by walking up to its
    // owning DiamondManager (GetComponentInParent, matching the bounds gizmo). The
    // result is cached per manager for the current tick, so N fixtures under one
    // manager only read its proxies once. Mirrors DiamondManager's static-vs-proxy
    // resolution and the runtime haze/scatter clamp.
    private static Atmo ResolveAtmo(DiamondFixtureDefinition def)
    {
        var manager = def.GetComponentInParent<DiamondManager>();
        if (manager == null)
            return new Atmo { HasManager = false, IntScale = 1f };

        int key = manager.GetInstanceID();
        if (_atmoCache.TryGetValue(key, out var cached))
            return cached;

        float haze     = (manager.AnimateHaze       && manager.HazeProxy       != null) ? manager.HazeProxy.localPosition.y       : manager.HazeDensity;
        float scatter  = (manager.AnimateScatter    && manager.ScatterProxy    != null) ? manager.ScatterProxy.localPosition.y    : manager.ScatterStrength;
        float aniso    = (manager.AnimateAnisotropy && manager.AnisotropyProxy != null) ? manager.AnisotropyProxy.localPosition.y : manager.Anisotropy;
        float intScale = (manager.AnimateBeamIntensityScale && manager.BeamIntensityScaleProxy != null) ? manager.BeamIntensityScaleProxy.localPosition.y : manager.BeamIntensityScale;

        // Match the runtime clamp so the preview shows what the beam will actually
        // reach, not an unclamped proxy overshoot.
        if (manager.AnimateHaze)    haze    = Mathf.Clamp(haze,    0f, manager.MaxHazeDensity);
        if (manager.AnimateScatter) scatter = Mathf.Clamp(scatter, 0f, manager.MaxScatterStrength);

        var atmo = new Atmo
        {
            HasManager = true,
            Haze       = haze,
            Scatter    = scatter,
            Aniso      = aniso,
            IntScale   = intScale,
        };
        _atmoCache[key] = atmo;
        return atmo;
    }
}
