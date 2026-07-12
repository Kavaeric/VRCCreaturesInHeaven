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
//   beamProps[i].localEulerAngles.x  - Beam zoom, as tan(half-angle).
//   beamProps[i].localPosition.y     - Beam focus, 0-1 direct pass-through.
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

    // Proxy transform carrying zoom (localEulerAngles.x), focus
    // (localPosition.y), and beam intensity (localScale.y). May contain
    // nulls for fixtures with no beam.
    public Transform[] BeamProps;

    // The moving-head child. Keyed directly by the animator; the manager does
    // not read or apply it (listed for tooling/stage-3 completeness).
    public Transform[] Heads;

    // Renderer whose _EmissionColor is driven by brightness*colour.
    public Renderer[] HeadRenderers;

    // Renderer on the volumetric beam cube. May be null for beamless fixtures.
    public Renderer[] BeamRenderers;

    // Baked per-fixture runtime flag: true for round (symmetric-cone) fixtures
    // using the BeamRound shader, which reads only _ZoomX. When set, the loop
    // skips the _ZoomZ write the round shader would ignore. This is the only
    // genuinely-baked runtime scalar. Everything else animated comes from the
    // proxies, everything else static is edit-time-only.
    public bool[] SymmetricBeam;

    // --- Baked lightshow descriptor ----------------------------------
    // Written by the offline DiamondLightshowBaker (edit-time). At runtime the Start()
    // shader-seed reads these to bind the bake texture and its packing constants onto
    // each fixture's property block, so the fixtures sample their own row on the GPU
    // instead of this manager reading proxy transforms per frame. Null LightshowTex =
    // no bake yet; the manager falls back to the live proxy-read path (see Update()).
    // See DIAMOND-GPU-ACCEL.md.

    // The baked RGBA32 lookup texture: row = fixture index, column = frame *
    // TexelsPerFixture + slot. Point-sampled; the shader lerps frames itself.
    public Texture LightshowTex;

    // Frames baked (columns / TexelsPerFixture). Maps the normalised [0,1] time to a
    // frame column at runtime.
    public int LightshowFrameCount;

    // Fixtures baked (rows). Should equal LampProps.Length; a mismatch means the bake
    // is stale and the manager should fall back to the proxy path.
    public int LightshowFixtureCount;

    // RGBA32 texels per fixture per frame (2 for the current channel set: colour texel
    // + beam texel). The shader's column stride.
    public int LightshowTexelsPerFixture;

    // Per-bake HDR scales: the texture stores SDR [0,1]; the shader multiplies back.
    // ColourScale = peak of (colour*brightness) across the show; BeamScale = peak beam
    // intensity across the show. See DIAMOND-GPU-ACCEL.md open item #1.
    public float LightshowColourScale = 1f;
    public float LightshowBeamScale   = 1f;

    // This manager's slot in the shared global _UdonDiamondLightshowFrames[] array
    // (its show identity on the show-identity axis). Seeded into every fixture's
    // block as _ShowIndex at Start; written each frame as
    // _UdonDiamondLightshowFrames[ShowIndex]. Multiple concurrent managers each take
    // a distinct slot. See DIAMOND-GPU-ACCEL.md "Addressing model" + open item #7.
    public int ShowIndex;

    // --- Time input (module-independent) -----------------------------
    // The show's playback position comes from a normalised [0,1] Animator float param,
    // NOT from Heartache -- Diamond must be usable standalone (see DIAMOND-GPU-ACCEL.md
    // "Module independence"). Anything that drives an Animator float 0->1 (Heartache's
    // existing _Time write, a plain clip lerp, a manual scrub) drives the show. When no
    // param is set, the serialized Time field below is used (inspector-scrubbable).
    public Animator AnimatorSource;
    public string TimeParameter = "_Time";
    [Range(0f, 1f)] public float Time;
    private bool _hasTimeParam;

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
    private int _idZoomX;
    private int _idZoomZ;
    private int _idFocus;
    private int _idEmitterWidth;
    private int _idEmitterHeight;
    private int _idHazeDensity;
    private int _idScatterStrength;
    private int _idAnisotropy;

    // Texture-mode (baked lightshow) property IDs.
    private int _idFixtureRow;
    private int _idShowIndex;
    private int _idLightshowTex;
    private int _idLightshowTexelsPerFixture;
    private int _idLightshowColourScale;
    private int _idLightshowBeamScale;
    private int _idLightshowFrameCount;
    private int _idLightshowFrames;
    // Manager-wide master beam-intensity scale, as a GLOBAL in texture mode (the proxy
    // path folds it per-fixture into _BeamIntensity, but texture mode never touches the
    // per-fixture blocks, so it rides as one global the shader multiplies in).
    private int _idBeamIntensityScaleGlobal;

    // Reused "off" colour so the dark path constructs nothing per frame.
    private Color _black;

    // --- Texture-mode (baked lightshow) state ------------------------
    // True when a bake texture is assigned: the manager runs the O(1) frame-index
    // path (Update just writes this manager's slot of the global frame array) and the
    // per-fixture proxy loop is skipped entirely. False = fall back to the live proxy
    // read path below, unchanged. Resolved once in Start.
    private bool _textureMode;

    // Reused global frame array so Update allocates nothing. Length covers this
    // manager's slot; a single manager fills index ShowIndex. The shader's fixed-length
    // _UdonDiamondLightshowFrames[DIAMOND_MAX_SHOWS] (=16, in DiamondLightshowSample.cginc)
    // is the ceiling; a ShowIndex beyond that would read a clamped slot. (Multi-manager
    // coordination is deferred -- see DIAMOND-GPU-ACCEL.md open item #7.)
    private float[] _frames;

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
    // Colour cached as three float arrays, not a Vector3[]. A Vector3 == compare is
    // an Udon extern that boxes both operands (one extra heap alloc per fixture per
    // frame); comparing the components as floats boxes nothing. Reading colour.x/.y/.z
    // off the already-boxed 'colour' local is free -- the box happened at the read.
    private float[]   _lastColourX;
    private float[]   _lastColourY;
    private float[]   _lastColourZ;
    private float[]   _lastZoom;
    private float[]   _lastFocus;
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
        _idZoomX         = VRCShader.PropertyToID("_ZoomX");
        _idZoomZ         = VRCShader.PropertyToID("_ZoomZ");
        _idFocus         = VRCShader.PropertyToID("_Focus");
        _idEmitterWidth  = VRCShader.PropertyToID("_EmitterWidth");
        _idEmitterHeight = VRCShader.PropertyToID("_EmitterHeight");
        _idHazeDensity     = VRCShader.PropertyToID("_HazeDensity");
        _idScatterStrength = VRCShader.PropertyToID("_ScatterStrength");
        _idAnisotropy      = VRCShader.PropertyToID("_Anisotropy");

        // Per-block properties (_FixtureRow/_ShowIndex) keep plain names -- they're set
        // via MaterialPropertyBlock, not globals. The GLOBAL ones must start with _Udon:
        // VRChat blocks Udon from setting any global shader property outside that
        // namespace (SetGlobal* on a non-_Udon name throws at runtime). Prefixed with
        // _UdonDiamondLightshow (not just _UdonLightshow) to avoid colliding with any
        // other world/package's own _Udon-namespaced globals.
        _idFixtureRow                = VRCShader.PropertyToID("_FixtureRow");
        _idShowIndex                 = VRCShader.PropertyToID("_ShowIndex");
        _idLightshowTex              = VRCShader.PropertyToID("_UdonDiamondLightshowTex");
        _idLightshowTexelsPerFixture = VRCShader.PropertyToID("_UdonDiamondLightshowTexelsPerFixture");
        _idLightshowColourScale      = VRCShader.PropertyToID("_UdonDiamondLightshowColourScale");
        _idLightshowBeamScale        = VRCShader.PropertyToID("_UdonDiamondLightshowBeamScale");
        _idLightshowFrameCount       = VRCShader.PropertyToID("_UdonDiamondLightshowFrameCount");
        _idLightshowFrames           = VRCShader.PropertyToID("_UdonDiamondLightshowFrames");
        _idBeamIntensityScaleGlobal  = VRCShader.PropertyToID("_UdonDiamondBeamIntensityScale");

        _black = new Color(0f, 0f, 0f, 0f);

        // Texture mode: a baked lightshow texture is assigned AND its fixture count
        // matches the current arrays (a stale bake falls back to the proxy path rather
        // than mis-indexing). In texture mode the fixtures' shaders read their own row;
        // the manager only pushes one global frame index per frame.
        int count = LampProps == null ? 0 : LampProps.Length;
        _textureMode = LightshowTex != null
                       && LightshowFixtureCount == count
                       && LightshowFrameCount > 0;

        if (_textureMode)
        {
            // The global scalars the shader unpack needs. Set once; constant for the show.
            // Explicit (float) casts: Udon extern calls don't implicitly widen int->float.
            VRCShader.SetGlobalTexture(_idLightshowTex, LightshowTex);
            VRCShader.SetGlobalFloat(_idLightshowTexelsPerFixture, (float)LightshowTexelsPerFixture);
            VRCShader.SetGlobalFloat(_idLightshowColourScale, LightshowColourScale);
            VRCShader.SetGlobalFloat(_idLightshowBeamScale, LightshowBeamScale);
            VRCShader.SetGlobalFloat(_idLightshowFrameCount, (float)LightshowFrameCount);

            // Master beam-intensity scale as a global. Seed it here so the STATIC case
            // (not animated -> the per-frame push skips it) has a valid value, and so
            // frame 0 never renders with an unset (0 -> all beams dark) global. When
            // animated, PushAnimatedAtmosphereGlobal overwrites it every frame.
            VRCShader.SetGlobalFloat(_idBeamIntensityScaleGlobal, BeamIntensityScale);

            // Global frame array sized to hold this manager's slot. One manager fills
            // ShowIndex; a length of ShowIndex+1 is enough (the shader clamps its read).
            _frames = new float[ShowIndex + 1];

            // Resolve the time source once. Empty param name (or no animator) falls back
            // to the serialized Time field.
            _hasTimeParam = AnimatorSource != null && TimeParameter != null && TimeParameter != "";
        }

        _headBlocks  = new MaterialPropertyBlock[count];
        _beamBlocks  = new MaterialPropertyBlock[count];
        _lampObjects = new GameObject[count];

        _cacheValid        = new bool[count];
        _lastLampActive    = new bool[count];
        _lastBrightness    = new float[count];
        _lastColourX       = new float[count];
        _lastColourY       = new float[count];
        _lastColourZ       = new float[count];
        _lastZoom          = new float[count];
        _lastFocus         = new float[count];
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

                // Texture mode: seed this fixture's static row + show slot into the
                // beam block once. The shader reads these to fetch its own row from the
                // bake texture every frame -- so the manager never drives colour/zoom/
                // focus/intensity per fixture again.
                if (_textureMode)
                {
                    _beamBlocks[i].SetFloat(_idFixtureRow, (float)i);
                    _beamBlocks[i].SetFloat(_idShowIndex,  (float)ShowIndex);
                }

                beamRenderer.SetPropertyBlock(_beamBlocks[i]);
            }

            // Texture mode: the lamp lens's glow pass (Diamond/LampGlow) reads the SAME
            // row + show slot from the bake texture, so seed them into the head block
            // once and apply it. This is what drives lamp glow in texture mode -- the
            // per-fixture ApplyFixture head write (proxy path) never runs here, so
            // without this the lamp would stay dark. Head renderer may be null / may
            // carry a non-glow material (e.g. a fixture whose lens has no glow pass);
            // seeding the block is harmless in that case (the props just go unread).
            if (_textureMode)
            {
                Renderer headRenderer = HeadRenderers == null ? null : HeadRenderers[i];
                if (headRenderer != null)
                {
                    _headBlocks[i].SetFloat(_idFixtureRow, (float)i);
                    _headBlocks[i].SetFloat(_idShowIndex,  (float)ShowIndex);
                    headRenderer.SetPropertyBlock(_headBlocks[i]);
                }
            }
        }

        // Texture mode: turn on the shader's baked-lightshow path on every beam
        // material this manager owns. EnableKeyword on the shared material affects all
        // instances; done once here. (The proxy fallback path leaves it off.)
        if (_textureMode)
            EnableLightshowKeyword();
    }

    // --- Shared atmosphere resolution --------------------------------
    // The single source of truth for turning the manager-wide atmosphere state into four
    // concrete scalars. For each parameter: the proxy's localPosition.y when it's animated
    // (and wired), else the static inspector float. Haze and scatter are clamped to their
    // Max ceilings when animated, so a proxy overshoot can't spill the beam past the
    // culling AABB the bake sized to that ceiling (a static value sizes its own bounds and
    // needs no cap). Anisotropy and the master intensity scale have no bounds ceiling, so
    // they pass through unclamped.
    //
    // Called by BOTH runtime paths (PushAnimatedAtmosphereGlobal in texture mode,
    // ApplyAnimatedManagerChannels in the proxy fallback) AND the edit-mode preview
    // (DiamondFixtureMapPreview.ResolveAtmo), so all three read atmosphere identically --
    // the clamp rules live in exactly one place. Public for that editor call; in edit mode
    // the behaviour runs as plain C#, so it's an ordinary method invocation, no Udon VM.
    // Reads only manager fields and writes nothing (pure), so it's safe to call anywhere.
    public void ResolveAtmosphere(out float haze, out float scatter, out float aniso, out float intensityScale)
    {
        haze           = (AnimateHaze               && HazeProxy               != null) ? HazeProxy.localPosition.y               : HazeDensity;
        scatter        = (AnimateScatter            && ScatterProxy            != null) ? ScatterProxy.localPosition.y            : ScatterStrength;
        aniso          = (AnimateAnisotropy         && AnisotropyProxy         != null) ? AnisotropyProxy.localPosition.y         : Anisotropy;
        intensityScale = (AnimateBeamIntensityScale && BeamIntensityScaleProxy != null) ? BeamIntensityScaleProxy.localPosition.y : BeamIntensityScale;

        if (AnimateHaze)    haze    = Mathf.Clamp(haze,    0f, MaxHazeDensity);
        if (AnimateScatter) scatter = Mathf.Clamp(scatter, 0f, MaxScatterStrength);
    }

    // Texture-mode atmosphere push. Static haze/scatter/aniso are seeded per-block in
    // Start (they override the material). Animated ones aren't in the block, so pushing
    // them as GLOBAL uniforms (one call each, not per-fixture) drives every beam at
    // once. Cheap, and only touches a param that actually animates.
    private void PushAnimatedAtmosphereGlobal()
    {
        // Values (incl. the haze/scatter clamp) come from the shared resolver. The push
        // gates stay here: push only the animated params, and only when their proxy is
        // wired -- a static param was already seeded (per-block for haze/scatter/aniso, as
        // a global for the master scale) in Start, so re-pushing it would be redundant.
        float haze, scatter, aniso, intensityScale;
        ResolveAtmosphere(out haze, out scatter, out aniso, out intensityScale);

        if (AnimateHaze               && HazeProxy               != null) VRCShader.SetGlobalFloat(_idHazeDensity,               haze);
        if (AnimateScatter            && ScatterProxy            != null) VRCShader.SetGlobalFloat(_idScatterStrength,           scatter);
        if (AnimateAnisotropy         && AnisotropyProxy         != null) VRCShader.SetGlobalFloat(_idAnisotropy,                aniso);
        // Master beam-intensity scale. The texture holds per-fixture beamIntensity; the
        // shader multiplies this global on top (matching the proxy path's per-fixture
        // `beamIntensity * BeamIntensityScale`). Not clamped: like the proxy path, master
        // scale has no bake ceiling (it's a straight multiply, not a bounds-baked param).
        if (AnimateBeamIntensityScale && BeamIntensityScaleProxy != null) VRCShader.SetGlobalFloat(_idBeamIntensityScaleGlobal, intensityScale);
    }

    // Enables DIAMOND_LIGHTSHOW_TEX on the beam and lamp-glow materials so their shaders
    // take the texture-read path (play mode). Both shape shaders and DiamondLampGlow gate
    // on this keyword; DiamondFixtureMapPreview forces it off in edit mode so the proxy
    // preview path lights up instead. They never run at once, so whoever's active owns it.
    //
    // Uses sharedMaterials (plural) on the head renderer: the lamp lens carries two
    // materials (its Mochie look + the Diamond glow), and .sharedMaterial would only reach
    // the first. Enabling the keyword on a material whose shader doesn't declare it (e.g.
    // Mochie) is a harmless no-op, so we don't need to identify which slot is the glow.
    private void EnableLightshowKeyword()
    {
        SetLightshowKeywordOn(BeamRenderers);
        SetLightshowKeywordOn(HeadRenderers);
    }

    private void SetLightshowKeywordOn(Renderer[] renderers)
    {
        if (renderers == null) return;
        int n = renderers.Length;
        for (int i = 0; i < n; i++)
        {
            Renderer r = renderers[i];
            if (r == null) continue;
            Material[] mats = r.sharedMaterials;   // may hold Mochie + Diamond glow
            if (mats == null) continue;
            int mc = mats.Length;
            for (int j = 0; j < mc; j++)
                if (mats[j] != null) mats[j].EnableKeyword("DIAMOND_LIGHTSHOW_TEX");
        }
    }

    // --- Per-frame loop ----------------------------------------------

    public void Update()
    {
        if (LampProps == null) return;

        int count = LampProps.Length;

        // --- Texture mode: O(1) per frame --------------------------------
        // The whole per-fixture apply now lives on the GPU (each beam samples its own
        // row of the bake texture). All the manager does is publish the current frame
        // column into its slot of the shared global array. No per-fixture loop, no
        // proxy reads, no boxing -- this is the entire point of the rewrite.
        if (_textureMode)
        {
            float t = _hasTimeParam ? AnimatorSource.GetFloat(TimeParameter) : Time;
            // Normalised [0,1] -> continuous frame column. The shader lerps the two
            // bracketing columns, so a fractional value is expected.
            float frame = Mathf.Clamp01(t) * (float)(LightshowFrameCount - 1);
            _frames[ShowIndex] = frame;
            VRCShader.SetGlobalFloatArray(_idLightshowFrames, _frames);

            // Animated manager-wide atmosphere still needs pushing, but it's a global
            // uniform (same value for every beam), so it's ONE SetGlobalFloat per
            // animated param per frame -- O(1), not a per-fixture fan-out. Static
            // atmosphere was already seeded per-block in Start. (In this project none of
            // these animate, so this is usually a no-op; kept for correctness.)
            PushAnimatedAtmosphereGlobal();
            return;
        }

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
            //
            // Performance floor (profiled): each transform.local* read below is a
            // Vector3-returning extern that Udon boxes onto its heap. Boxing is per
            // extern CALL, not per component (.y is free once boxed), so up to 6
            // boxes/fixture/frame -- the entire per-frame GC.Alloc baseline. Reading
            // fewer transforms is the only lever, but the proxy-per-channel layout is
            // a deliberate authoring choice, so we eat the boxes rather than repack.
            // A real fix means rethinking how the animator drives fixtures.
            bool    lampActive    = _lampObjects[i].activeSelf;
            float   brightness    = lamp.localPosition.y;
            Vector3 colour        = lamp.localScale;

            // Decide off-ness from lamp data alone, before touching the beam proxy.
            // A dark fixture forces the beam to _black and never applies its
            // zoom/focus/intensity, so reading them would just box three Vector3s
            // for nothing. This is the one lever that reduces boxed reads without
            // repacking the proxy layout: skip the beam reads entirely when off.
            bool off = IsLightOff(lampActive, brightness, colour);

            float zoom          = 0f;
            float focus         = 1f;
            float beamIntensity = 1f;

            // Only read the beam proxy (3 boxed Vector3 externs) when the light is
            // actually on. Off fixtures skip all three, and in a show where many
            // fixtures are dark at once, that's the bulk of the per-frame boxing.
            Transform beam = off ? null : BeamProps[i];
            if (beam != null)
            {
                zoom          = beam.localEulerAngles.x;
                focus         = beam.localPosition.y;
                beamIntensity = beam.localScale.y;
            }

            // Dirty-check. An off fixture compares only the values it actually read
            // (lampActive/brightness/colour) plus the off-state itself: its beam
            // channels weren't read, so they must not participate in the compare or
            // we'd test a stale local against last frame's value. An on fixture
            // compares everything, as before. _cacheValid[i] guarantees first apply.
            //
            // _lastLampActive doubles as the "was off last frame" record: an off
            // fixture writes lampActive (false when !active) but the off-state is
            // fully captured by re-deriving off from the stored lamp values, so a
            // still-off, still-same-colour fixture short-circuits here.
            bool unchanged = _cacheValid[i]
                && lampActive == _lastLampActive[i]
                && brightness == _lastBrightness[i]
                && colour.x   == _lastColourX[i]
                && colour.y   == _lastColourY[i]
                && colour.z   == _lastColourZ[i];
            if (unchanged && !off)
            {
                // On and lamp unchanged: still must check the beam channels, since
                // those can move while the lamp holds steady (e.g. a zoom sweep).
                unchanged = zoom          == _lastZoom[i]
                         && focus         == _lastFocus[i]
                         && beamIntensity == _lastBeamIntensity[i];
            }
            if (unchanged)
                continue;

            _lastLampActive[i]    = lampActive;
            _lastBrightness[i]    = brightness;
            _lastColourX[i]       = colour.x;
            _lastColourY[i]       = colour.y;
            _lastColourZ[i]       = colour.z;
            // Only overwrite the cached beam channels when we actually read them.
            // Leaving them untouched for an off fixture is correct: they're not
            // compared while off, and when the fixture next turns on it's a lamp
            // change (off -> on), so the dirty-check already forces a fresh apply.
            if (!off)
            {
                _lastZoom[i]          = zoom;
                _lastFocus[i]         = focus;
                _lastBeamIntensity[i] = beamIntensity;
            }
            _cacheValid[i]        = true;

            ApplyFixture(i, off, brightness, colour, zoom, focus, beamIntensity);
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
        // Resolve the current animated values (static-or-proxy, with the haze/scatter
        // clamp) through the shared resolver, so this path and the texture path can't
        // disagree on how atmosphere is read or clamped. Named intScale locally, as before.
        float haze, scatter, aniso, intScale;
        ResolveAtmosphere(out haze, out scatter, out aniso, out intScale);

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
    // 'off' is computed by the caller (from lamp data alone) and passed in, so we
    // don't re-derive it here -- the caller uses it to gate the beam reads too.
    private void ApplyFixture(int i, bool off, float brightness, Vector3 colour, float zoom, float focus, float beamIntensity)
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

        if (off)
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

        // Mirror brightness-modulated colour, animated intensity, zoom, and
        // focus onto the beam shaft. Zoom is symmetric (X = Z) for a square
        // cone. Focus is a single instanced shader property, same on both
        // shapes -- no X/Z split needed.
        if (beamRenderer != null)
        {
            // See note above: guard the toggle so we don't re-activate every frame.
            if (!beamRenderer.gameObject.activeSelf)
                beamRenderer.gameObject.SetActive(true);
            beamBlock.SetColor(_idColor, drivenColour);
            // Blanket master scale on the shaft intensity (default 1 = no-op).
            // Folds into the write we already pay; see BeamIntensityScale.
            beamBlock.SetFloat(_idBeamIntensity, beamIntensity * BeamIntensityScale);
            beamBlock.SetFloat(_idZoomX, zoom);
            beamBlock.SetFloat(_idFocus, focus);

            // Round fixtures use the BeamRound shader, which reads only _ZoomX.
            // Rect fixtures need _ZoomZ too (here mirrored from _ZoomX for a
            // symmetric square cone).
            if (!SymmetricBeam[i])
                beamBlock.SetFloat(_idZoomZ, zoom);

            beamRenderer.SetPropertyBlock(beamBlock);
        }
    }
}
