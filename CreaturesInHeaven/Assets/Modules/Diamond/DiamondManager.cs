using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

// Central runtime driver for a set of lighting fixtures. One manager owns the
// whole fixture set as parallel (structure-of-arrays) arrays and does every
// per-frame apply in a single Update() loop -- replacing the old per-fixture
// DiamondFixtureDriver, which paid one UdonBehaviour.ManagedUpdate() dispatch
// per fixture per frame (~8 ms at ~600 fixtures) regardless of whether its body
// did any work.
//
// This is the "World + System" in ECS terms: a fixture is not an object, it is
// an index i shared across every array below. Row i is one fixture; the arrays
// must stay index-aligned. They are populated at edit time by
// DiamondManagerDefinition (the bake step), which is stripped at build -- at
// runtime this behaviour just reads its serialized arrays.
//
// Why parallel arrays and not an array of a config struct: Udon can't read
// fields off [Serializable] class/struct array elements at runtime, so each
// channel is its own aligned array.
//
// The animator does NOT key these arrays. It keys proxy transforms per fixture
// (LampProps / BeamProps / Head), exactly as before; this loop is the sole
// reader of those proxies and pushes their values into per-fixture
// MaterialPropertyBlocks (which can't be animated directly without breaking
// instancing). Proxy channel layout, unchanged from the old driver:
//
//   lampProps[i].localPosition.y     - Brightness (emissive multiplier).
//   lampProps[i].localScale.xyz      - Emission colour (RGB, HDR).
//   lampProps[i].gameObject.activeSelf - On/off.
//   beamProps[i].localEulerAngles.x  - Beam spread, as tan(half-angle).
//   beamProps[i].localScale.y        - Beam intensity.
//   heads[i].localRotation           - Head aim (keyed directly, not read here).
public class DiamondManager : UdonSharpBehaviour
{
    // --- Fixture arrays (object graph) -------------------------------
    // One aligned entry per fixture. Populated at edit time by
    // DiamondManagerDefinition. Every array here MUST be the same length and
    // index-aligned: fixture i is (lampProps[i], beamProps[i], heads[i],
    // headRenderers[i], beamRenderers[i], symmetricBeam[i]).

    // Per-fixture stable scene identity (GlobalObjectId string), recorded at
    // bake time. Not read at runtime. Group membership, presets, and external
    // addressing would key on this rather than the array index.
    public string[] SceneIds;

    // The fixture root GameObject: the object carrying DiamondFixtureDefinition,
    // the same "scene object" the fixture map keys on. Recorded at bake time and
    // not read by the loop; it's the anchor tooling resolves a fixture through
    // (identity, selection, stage-3 index mapping). SceneIds[i] is its identity.
    public GameObject[] Fixtures;

    // Baked per-fixture beam emitter size (_EmitterWidth / _EmitterHeight). Not
    // animated , but it must be re-applied at runtime: a MaterialPropertyBlock is
    // instance state, not serialized on the renderer, so anything written in edit
    // mode is gone after entering play.
    public Vector2[] FixtureEmitterSizes;

    // Proxy transform carrying brightness (localPosition.y), emission colour
    // (localScale.xyz), and on/off (gameObject.activeSelf).
    public Transform[] LampProps;

    // Proxy transform carrying spread (localEulerAngles.x) and beam intensity
    // (localScale.y). May contain nulls for fixtures with no beam.
    public Transform[] BeamProps;

    // The moving-head child. Keyed directly by the animator; the manager does
    // not read or apply it (listed for tooling/stage-3 completeness).
    public Transform[] Heads;

    // Renderer whose _EmissionColor is driven by brightness*colour.
    public Renderer[] HeadRenderers;

    // Renderer on the volumetric beam cube. May be null for beamless fixtures.
    public Renderer[] BeamRenderers;

    // Baked per-fixture runtime flag: true for round (symmetric-cone) fixtures
    // using the BeamRound shader, which reads only _SpreadX. When set, the loop
    // skips the _SpreadZ write the round shader would ignore. This is the only
    // genuinely-baked runtime scalar. Everything else animated comes from the
    // proxies, everything else static is edit-time-only.
    public bool[] SymmetricBeam;

    // --- Manager-wide atmosphere -------------------------------------
    // Haze density, scatter strength, and anisotropy are properties of the room's
    // air, not the fixture -- one value shared across every beam. They live here
    // on the manager rather than per-material so they can be controlled centrally.
    //
    // Each parameter is independently static or animated (see DIAMOND-MANAGER.md).
    //
    // These seed the same shader floats the beam material used to hold
    // (_HazeDensity / _ScatterStrength / _Anisotropy); once written into the block
    // in Start they override the material's serialized values for that renderer.

    // Each parameter: Animate toggle, static float (used when off), and proxy
    // transform (used when on). Every proxy is read on ONE axis, localPosition.y --
    // one scalar per object so unrelated params never share a Vector3 (which keys
    // as a unit) and their Animate toggles stay independent.

    // Off: HazeDensity float. On: HazeProxy.localPosition.y -> _HazeDensity.
    public bool AnimateHaze;
    public float HazeDensity = 0.03f;
    public Transform HazeProxy;

    [Space]

    // Off: ScatterStrength float. On: ScatterProxy.localPosition.y -> _ScatterStrength.
    public bool AnimateScatter;
    public float ScatterStrength = 0.5f;
    public Transform ScatterProxy;

    [Space]

    // Bounds ceilings for the two animated params that widen the beam's lateral
    // spill. The culling AABB is baked once (edit time), so if animated haze/scatter
    // can exceed the value the bounds were sized for, the beam spills past the AABB
    // and gets frustum-culled. These are the MAX the animated value may reach: the
    // bake sizes bounds to them (when the matching Animate<X> is on), and the
    // runtime CLAMPS the proxy read to them, so runtime can never exceed the baked
    // worst case. Only consulted for an animated param; a static one sizes bounds
    // from its own float. Re-bake after changing a ceiling.
    public float MaxHazeDensity     = 0.15f;
    public float MaxScatterStrength = 1f;

    [Space]

    // Off: Anisotropy float. On: AnisotropyProxy.localPosition.y -> _Anisotropy.
    public bool AnimateAnisotropy;
    public float Anisotropy = 0.5f;
    public Transform AnisotropyProxy;

    [Space]

    // Off: BeamIntensityScale float. On: BeamIntensityScaleProxy.localPosition.y.
    // Multiplier on per-fixture _BeamIntensity, not its own shader key.
    public bool AnimateBeamIntensityScale;
    public float BeamIntensityScale = 1f;
    public Transform BeamIntensityScaleProxy;

    // --- Per-fixture property blocks ---------------------------------
    // One MaterialPropertyBlock per fixture per renderer, allocated once in
    // Start. Reused every frame so we never churn allocations. Index-aligned
    // with the fixture arrays.
    private MaterialPropertyBlock[] _headBlocks;
    private MaterialPropertyBlock[] _beamBlocks;

    // --- Cached lamp GameObjects -------------------------------------
    // LampProps[i].gameObject, resolved once in Start. The steady-state read
    // path checks activeSelf every frame for every fixture; reading it as
    // lamp.gameObject.activeSelf is TWO extern calls (.gameObject, then
    // .activeSelf). Caching the GameObject makes it one.
    private GameObject[] _lampObjects;

    // --- Cached shader property IDs ----------------------------------
    // Resolved once in Start via VRCShader.PropertyToID and reused every frame.
    // This is the key to the loop being allocation-free: the string-keyed
    // SetColor/SetFloat overloads marshal their string argument on every call,
    // which at hundreds of fixtures per frame showed up as a GC.Alloc spike that
    // ate the whole dispatch-cost win. The int overloads allocate nothing.
    private int _idEmissionColor;
    private int _idColor;
    private int _idBeamIntensity;
    private int _idSpreadX;
    private int _idSpreadZ;
    private int _idEmitterWidth;
    private int _idEmitterHeight;
    private int _idHazeDensity;
    private int _idScatterStrength;
    private int _idAnisotropy;

    // Reused "off" colour so the dark path constructs nothing per frame.
    private Color _black;

    // --- Per-fixture dirty-check cache -------------------------------
    // Same rationale as the old driver: the animator only moves a channel on a
    // keyframe, but Update runs every frame. Caching the last-applied inputs per
    // fixture lets an unchanged fixture skip its property-block writes AND its
    // beam SetActive toggle. The SetActive skip is the important one:
    // SetActive(false) on an already-inactive object still runs
    // GameObject.Deactivate bookkeeping, which at ~600 fixtures dominated the
    // main thread when it fired every frame.
    //
    // _cacheValid[i] is false until fixture i applies once, so its initial state
    // is always written regardless of what the arrays happen to hold.
    private bool[]    _cacheValid;
    private bool[]    _lastLampActive;
    private float[]   _lastBrightness;
    private Vector3[] _lastColour;
    private float[]   _lastSpread;
    private float[]   _lastBeamIntensity;

    // --- Manager-wide animated-channel dirty-check -------------------
    // Last-applied value per animated manager parameter, so the shared section
    // skips its work when a value is unchanged. Only meaningful when the matching
    // Animate<Param> bool is set. Seeded false so the first frame always applies.
    private bool  _atmoCacheValid;
    private float _lastHaze;
    private float _lastScatter;
    private float _lastAnisotropy;
    private float _lastBeamIntensityScale;

    // --- Lifecycle ---------------------------------------------------

    public void Start()
    {
        // Resolve shader property IDs once. Reused every frame so the per-frame
        // SetColor/SetFloat calls take the int overload (no string marshalling).
        _idEmissionColor = VRCShader.PropertyToID("_EmissionColor");
        _idColor         = VRCShader.PropertyToID("_Color");
        _idBeamIntensity = VRCShader.PropertyToID("_BeamIntensity");
        _idSpreadX       = VRCShader.PropertyToID("_SpreadX");
        _idSpreadZ       = VRCShader.PropertyToID("_SpreadZ");
        _idEmitterWidth  = VRCShader.PropertyToID("_EmitterWidth");
        _idEmitterHeight = VRCShader.PropertyToID("_EmitterHeight");
        _idHazeDensity     = VRCShader.PropertyToID("_HazeDensity");
        _idScatterStrength = VRCShader.PropertyToID("_ScatterStrength");
        _idAnisotropy      = VRCShader.PropertyToID("_Anisotropy");

        _black = new Color(0f, 0f, 0f, 0f);

        int count = LampProps == null ? 0 : LampProps.Length;

        _headBlocks  = new MaterialPropertyBlock[count];
        _beamBlocks  = new MaterialPropertyBlock[count];
        _lampObjects = new GameObject[count];

        _cacheValid        = new bool[count];
        _lastLampActive    = new bool[count];
        _lastBrightness    = new float[count];
        _lastColour        = new Vector3[count];
        _lastSpread        = new float[count];
        _lastBeamIntensity = new float[count];

        for (int i = 0; i < count; i++)
        {
            _headBlocks[i] = new MaterialPropertyBlock();
            _beamBlocks[i] = new MaterialPropertyBlock();
            _cacheValid[i] = false;

            // Cache the lamp's GameObject so the per-frame activeSelf read is a
            // single extern instead of lamp.gameObject.activeSelf (two).
            Transform lampT = LampProps == null ? null : LampProps[i];
            _lampObjects[i] = lampT == null ? null : lampT.gameObject;

            // Seed the static per-fixture and manager-wide values into the beam
            // block once. Neither is serialized on the renderer, so both must be
            // re-applied at runtime; writing them here (rather than each Update)
            // means every later per-frame SetPropertyBlock(beamBlock) preserves
            // them, since the loop only ever sets *other* keys on this same block.
            // Beamless fixtures (null beam renderer) just never get the block.
            //
            //   Emitter size   -- per-fixture static (from the bake).
            //   Atmosphere     -- manager-wide, seeded here ONLY if static. An
            //                     animated param is owned by the Update shared
            //                     section (which pushes its proxy value on the
            //                     first frame), so seeding it here would just be
            //                     an immediately-overwritten write.
            Renderer beamRenderer = BeamRenderers == null ? null : BeamRenderers[i];
            if (beamRenderer != null)
            {
                if (FixtureEmitterSizes != null)
                {
                    _beamBlocks[i].SetFloat(_idEmitterWidth,  FixtureEmitterSizes[i].x);
                    _beamBlocks[i].SetFloat(_idEmitterHeight, FixtureEmitterSizes[i].y);
                }

                if (!AnimateHaze)       _beamBlocks[i].SetFloat(_idHazeDensity,     HazeDensity);
                if (!AnimateScatter)    _beamBlocks[i].SetFloat(_idScatterStrength, ScatterStrength);
                if (!AnimateAnisotropy) _beamBlocks[i].SetFloat(_idAnisotropy,      Anisotropy);

                beamRenderer.SetPropertyBlock(_beamBlocks[i]);
            }
        }
    }

    // --- Per-frame loop ----------------------------------------------

    public void Update()
    {
        if (LampProps == null) return;

        int count = LampProps.Length;

        // Manager-wide animated channels, handled ONCE per frame (not per fixture)
        // before the per-fixture loop. Must run first so an intensity-scale change
        // invalidates the per-fixture cache in time for the loop below to recompute
        // this same frame.
        ApplyAnimatedManagerChannels(count);

        for (int i = 0; i < count; i++)
        {
            Transform lamp = LampProps[i];
            if (lamp == null) continue;

            // Read the raw animated inputs once, exactly as the old driver did.
            // These are the only channels the animator drives per frame;
            // everything applied below is a deterministic function of them.
            // activeSelf comes off the cached GameObject (one extern, not two).
            bool    lampActive    = _lampObjects[i].activeSelf;
            float   brightness    = lamp.localPosition.y;
            Vector3 colour        = lamp.localScale;
            float   spread        = 0f;
            float   beamIntensity = 1f;

            Transform beam = BeamProps[i];
            if (beam != null)
            {
                spread        = beam.localEulerAngles.x;
                beamIntensity = beam.localScale.y;
            }

            // Skip the whole apply when nothing the animator drives has moved
            // since last frame. _cacheValid[i] guarantees the first apply.
            if (_cacheValid[i]
                && lampActive    == _lastLampActive[i]
                && brightness    == _lastBrightness[i]
                && colour        == _lastColour[i]
                && spread        == _lastSpread[i]
                && beamIntensity == _lastBeamIntensity[i])
            {
                continue;
            }

            _lastLampActive[i]    = lampActive;
            _lastBrightness[i]    = brightness;
            _lastColour[i]        = colour;
            _lastSpread[i]        = spread;
            _lastBeamIntensity[i] = beamIntensity;
            _cacheValid[i]        = true;

            ApplyFixture(i, lampActive, brightness, colour, spread, beamIntensity);
        }
    }

    // --- Manager-wide animated channels ------------------------------

    // Reads the animated manager-wide proxies once per frame (the shared section)
    // and applies any that changed. Cheap when idle: for each animated parameter,
    // a bool check + one proxy read + one float compare, and nothing more until the
    // value actually moves. Only a real change fans out across the N fixtures.
    //
    // Two kinds of write:
    //   - Haze/scatter/anisotropy each own a shared shader key: on change, push
    //     just that key onto every beam block (preserving the per-fixture keys
    //     already there) and re-SetPropertyBlock.
    //   - BeamIntensityScale is a per-fixture multiplier (final _BeamIntensity =
    //     animated * scale), so there's no single key to push. On change, invalidate
    //     the per-fixture dirty-check cache so the loop below recomputes each
    //     fixture's _BeamIntensity through the normal ApplyFixture path.
    //
    // _atmoCacheValid guards the first frame: false until the first read, so an
    // animated channel always applies once regardless of its authored float.
    private void ApplyAnimatedManagerChannels(int count)
    {
        // Read the current animated values (only where the proxy exists).
        float haze       = (AnimateHaze              && HazeProxy               != null) ? HazeProxy.localPosition.y               : HazeDensity;
        float scatter    = (AnimateScatter           && ScatterProxy            != null) ? ScatterProxy.localPosition.y            : ScatterStrength;
        float aniso      = (AnimateAnisotropy        && AnisotropyProxy         != null) ? AnisotropyProxy.localPosition.y         : Anisotropy;
        float intScale   = (AnimateBeamIntensityScale && BeamIntensityScaleProxy != null) ? BeamIntensityScaleProxy.localPosition.y : BeamIntensityScale;

        // Clamp animated haze/scatter to the ceilings the bounds were baked to, so
        // a proxy overshoot can't spill the beam past its (edit-time) culling AABB.
        // Only clamp when animated -- a static value sizes its own bounds and needs
        // no cap. Floor at 0 too, since a negative proxy read is meaningless here.
        if (AnimateHaze)    haze    = Mathf.Clamp(haze,    0f, MaxHazeDensity);
        if (AnimateScatter) scatter = Mathf.Clamp(scatter, 0f, MaxScatterStrength);

        // First frame: seed the last-values and apply once. After that, only the
        // channels that actually moved do any work.
        bool first = !_atmoCacheValid;

        bool hazeChanged    = AnimateHaze              && (first || haze     != _lastHaze);
        bool scatterChanged = AnimateScatter           && (first || scatter  != _lastScatter);
        bool anisoChanged   = AnimateAnisotropy        && (first || aniso    != _lastAnisotropy);
        bool intChanged     = AnimateBeamIntensityScale && (first || intScale != _lastBeamIntensityScale);

        // Push any changed shared-key atmosphere param across all beam blocks.
        if (hazeChanged || scatterChanged || anisoChanged)
        {
            for (int i = 0; i < count; i++)
            {
                Renderer beamRenderer = BeamRenderers[i];
                if (beamRenderer == null) continue;

                MaterialPropertyBlock beamBlock = _beamBlocks[i];
                if (hazeChanged)    beamBlock.SetFloat(_idHazeDensity,     haze);
                if (scatterChanged) beamBlock.SetFloat(_idScatterStrength, scatter);
                if (anisoChanged)   beamBlock.SetFloat(_idAnisotropy,      aniso);
                beamRenderer.SetPropertyBlock(beamBlock);
            }
        }

        // A beam-intensity-scale change can't be pushed as a shared key -- the final
        // value is per-fixture. Apply it by making the value current, then forcing
        // every fixture to recompute in the loop below (this frame).
        if (intChanged)
        {
            BeamIntensityScale = intScale;
            for (int i = 0; i < count; i++)
                _cacheValid[i] = false;
        }

        _lastHaze               = haze;
        _lastScatter            = scatter;
        _lastAnisotropy         = aniso;
        _lastBeamIntensityScale = intScale;
        _atmoCacheValid         = true;
    }

    // --- Application -------------------------------------------------

    // Whether fixture i is effectively dark: proxy disabled, zero brightness, or
    // black colour. Takes the already-read inputs so the dirty-check and the
    // apply agree on the same values (the old driver re-read them; here they're
    // passed in).
    private bool IsLightOff(bool lampActive, float brightness, Vector3 colour)
    {
        if (!lampActive) return true;
        if (brightness == 0f) return true;
        if (colour.x == 0f && colour.y == 0f && colour.z == 0f) return true;
        return false;
    }

    // Applies the driven state for fixture i to its renderers. Mirrors the old
    // DiamondFixtureDriver.ApplyMaterialProperties, but indexed into the arrays.
    private void ApplyFixture(int i, bool lampActive, float brightness, Vector3 colour, float spread, float beamIntensity)
    {
        Renderer headRenderer = HeadRenderers[i];
        if (headRenderer == null)
        {
            Debug.LogWarning("[Diamond] Manager fixture " + i + " has no HeadRenderer.");
            return;
        }

        MaterialPropertyBlock headBlock = _headBlocks[i];
        Renderer beamRenderer = BeamRenderers[i];
        MaterialPropertyBlock beamBlock = _beamBlocks[i];

        if (IsLightOff(lampActive, brightness, colour))
        {
            headBlock.SetColor(_idEmissionColor, _black);
            headRenderer.SetPropertyBlock(headBlock);

            if (beamRenderer != null)
            {
                beamBlock.SetColor(_idColor, _black);
                beamRenderer.SetPropertyBlock(beamBlock);
                // Only toggle when actually changing state. SetActive(false) on
                // an already-inactive object still runs deactivation bookkeeping
                // (GameObject.Deactivate), which at ~600 fixtures dominated the
                // main thread when this fired every frame.
                if (beamRenderer.gameObject.activeSelf)
                    beamRenderer.gameObject.SetActive(false);
            }
            return;
        }

        // Colour is the animated RGB from LampProps.localScale, scaled by
        // brightness. Alpha is 1; the shader's emission ignores it.
        Color drivenColour = new Color(colour.x, colour.y, colour.z, 1f) * brightness;

        headBlock.SetColor(_idEmissionColor, drivenColour);
        headRenderer.SetPropertyBlock(headBlock);

        // Mirror brightness-modulated colour, animated intensity, and spread
        // onto the beam shaft. Spread is symmetric (X = Z) for a square cone.
        if (beamRenderer != null)
        {
            // See note above: guard the toggle so we don't re-activate every frame.
            if (!beamRenderer.gameObject.activeSelf)
                beamRenderer.gameObject.SetActive(true);
            beamBlock.SetColor(_idColor, drivenColour);
            // Blanket master scale on the shaft intensity (default 1 = no-op).
            // Folds into the write we already pay; see BeamIntensityScale.
            beamBlock.SetFloat(_idBeamIntensity, beamIntensity * BeamIntensityScale);
            beamBlock.SetFloat(_idSpreadX, spread);

            // Round fixtures use the BeamRound shader, which reads only _SpreadX.
            // Rect fixtures need _SpreadZ too (here mirrored from _SpreadX for a
            // symmetric square cone).
            if (!SymmetricBeam[i])
                beamBlock.SetFloat(_idSpreadZ, spread);

            beamRenderer.SetPropertyBlock(beamBlock);
        }
    }
}
