using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

// Central runtime driver for a set of lighting fixtures. One manager owns the whole
// fixture set as parallel (structure-of-arrays) arrays and does every per-frame apply
// in a single Update() loop, so the cost is one behaviour dispatch per frame rather
// than one per fixture.
//
// This is the "World + System" in ECS terms: a fixture is not an object, it is an index
// i shared across every array below. Row i is one fixture, so the arrays must stay
// index-aligned. DiamondManagerDefinition (the bake step) populates them at edit time
// and is stripped at build; at runtime this behaviour just reads its serialized arrays.
// Parallel arrays rather than an array of config structs because Udon can't read fields
// off [Serializable] class/struct array elements at runtime.
//
// The animator keys proxy transforms per fixture (LampProps / BeamProps / Head), not
// these arrays. This loop is the sole reader of those proxies and pushes their values
// into per-fixture MaterialPropertyBlocks (which can't be animated directly without
// breaking instancing). The proxy channel layout:
//
//   lampProps[i].localPosition.y       Brightness (emissive multiplier).
//   lampProps[i].localScale.xyz        Emission colour (RGB, HDR).
//   lampProps[i].gameObject.activeSelf On/off.
//   beamProps[i].localEulerAngles.x    Beam zoom, as tan(half-angle).
//   beamProps[i].localPosition.y       Beam focus, 0-1 direct pass-through.
//   beamProps[i].localScale.y          Beam intensity.
//   heads[i].localRotation             Head aim (keyed directly, not read here).

// How a DiamondManager drives its fixtures each frame. An explicit author choice: the
// two paths trade performance against authoring convenience, so the author states which
// one they want and the manager honours it.
//
//   LiveProxy    Update() reads each fixture's animated proxy transforms and pushes them
//                into per-fixture MaterialPropertyBlocks. Instant to iterate (no bake
//                step), fine for small shows (~dozen fixtures).
//   BakedTexture The GPU path (DiLET, "Diamond Lightshow Encoding Texture"): each fixture
//                samples its own row of a baked lookup texture on the GPU, and Update()
//                just publishes the current frame index. Far cheaper at scale, but needs
//                an offline re-bake whenever the show changes (see DiamondLightshowBaker
//                and DIAMOND-GPU-ACCEL.md).
//
// BakedTexture requires a valid, current bake (LightshowTex present, fixture count
// matching). A missing or stale bake is a loud error with beams left dark, not a silent
// downgrade to LiveProxy (see Start).
//
// Declared top-level rather than nested in DiamondManager because U# doesn't support
// nested type declarations; a non-behaviour type may share the file as long as the
// behaviour's class name matches the filename.
public enum DiamondDriveMode { LiveProxy, BakedTexture }

// Which render path the editor scene-view preview drives, independent of the runtime
// DriveMode. Read only by DiamondFixtureMapPreview; the runtime behaviour ignores it.
// The decoupling is the point: run a manager on LiveProxy at runtime while previewing its
// BakedTexture output in the editor to check the bake matches, or the reverse, without
// changing how it ships.
//
//   LiveProxy    The proxy preview: read the animated proxy transforms and push them into
//                per-fixture blocks (keyword forced off). Also the only path that scrubs
//                without a bake.
//   BakedTexture The baked preview: bind the bake texture and globals, enable the
//                DIAMOND_LIGHTSHOW_TEX keyword, and drive the frame index from Time, so the
//                scene view samples the actual bake.
//
// If a BakedTexture preview has no valid bake, the editor shows the proxy preview instead
// (with a one-shot console note) rather than a black scene. This is a preview-only
// convenience: the runtime never falls back, it errors and leaves the beams dark (see
// DiamondDriveMode and Start).
//
// Top-level for the same U# reason as DiamondDriveMode.
public enum DiamondPreviewMode { LiveProxy, BakedTexture }

public class DiamondManager : UdonSharpBehaviour
{
    // --- Drive mode --------------------------------------------------
    // See DiamondDriveMode (top of file) for what each mode does. Defaults to LiveProxy so a
    // freshly-added manager with no bake works immediately.
    public DiamondDriveMode DriveMode = DiamondDriveMode.LiveProxy;

    // Which render path the editor preview drives (see DiamondPreviewMode, top of file).
    // Editor-only: read by DiamondFixtureMapPreview, never by the runtime. Defaults to
    // LiveProxy, which always works without a bake.
    public DiamondPreviewMode PreviewMode = DiamondPreviewMode.LiveProxy;

    // --- Fixture arrays (object graph) -------------------------------
    // One aligned entry per fixture, populated at edit time by DiamondManagerDefinition.
    // Every array here is the same length and index-aligned: fixture i is (LampProps[i],
    // BeamProps[i], Heads[i], HeadRenderers[i], BeamRenderers[i], SymmetricBeam[i]).

    // Per-fixture stable scene identity (GlobalObjectId string), recorded at bake time. Not
    // read at runtime; it's the key group membership, presets, and external addressing use
    // in place of the array index.
    public string[] SceneIds;

    // The fixture root GameObject carrying DiamondFixtureDefinition, the same scene object the
    // fixture map keys on. Recorded at bake time and not read by the loop; it's the anchor
    // tooling resolves a fixture through (identity, selection, index mapping). SceneIds[i] is
    // its identity.
    public GameObject[] Fixtures;

    // Per-fixture beam emitter size (_EmitterWidth / _EmitterHeight), from the bake. Static,
    // but re-applied at runtime: a MaterialPropertyBlock is instance state, not serialized on
    // the renderer, so anything written in edit mode is gone after entering play.
    public Vector2[] FixtureEmitterSizes;

    // Proxy transform carrying brightness (localPosition.y), emission colour (localScale.xyz),
    // and on/off (gameObject.activeSelf).
    public Transform[] LampProps;

    // Proxy transform carrying zoom (localEulerAngles.x), focus (localPosition.y), and beam
    // intensity (localScale.y). May contain nulls for fixtures with no beam.
    public Transform[] BeamProps;

    // The moving-head child. Keyed directly by the animator; the manager doesn't read or apply
    // it (listed for tooling completeness).
    public Transform[] Heads;

    // Renderer whose _EmissionColor is driven by brightness*colour.
    public Renderer[] HeadRenderers;

    // Renderer on the volumetric beam cube. May be null for beamless fixtures.
    public Renderer[] BeamRenderers;

    // Per-fixture flag from the bake: true for round (symmetric-cone) fixtures using the
    // BeamRound shader, which reads only _ZoomX. When set, the loop skips the _ZoomZ write the
    // round shader would ignore. This is the only baked runtime scalar; everything else
    // animated comes from the proxies, and everything else static is edit-time-only.
    public bool[] SymmetricBeam;

    // --- Baked lightshow descriptor ----------------------------------
    // Written by the offline DiamondLightshowBaker at edit time. In BakedTexture mode, Start
    // reads these to bind the bake texture and its packing constants onto each fixture's
    // property block, so the fixtures sample their own row on the GPU rather than the manager
    // reading proxy transforms per frame. See DIAMOND-GPU-ACCEL.md.

    // The baked RGBA32 lookup texture: row = fixture index, column = frame * TexelsPerFixture +
    // slot. Point-sampled; the shader lerps frames itself.
    public Texture LightshowTex;

    // Frames baked (columns / TexelsPerFixture). Maps the normalised [0,1] time to a frame
    // column at runtime.
    public int LightshowFrameCount;

    // Fixtures baked (rows). Equals LampProps.Length for a current bake; a mismatch means the
    // bake is stale and fails ValidateBake.
    public int LightshowFixtureCount;

    // RGBA32 texels per fixture per frame (2 for the current channel set: colour texel + beam
    // texel). The shader's column stride.
    public int LightshowTexelsPerFixture;

    // Per-bake HDR scales: the texture stores SDR [0,1] and the shader multiplies back.
    // ColourScale is the peak of (colour*brightness) across the show; BeamScale is the peak
    // beam intensity. See DIAMOND-GPU-ACCEL.md open item #1.
    public float LightshowColourScale = 1f;
    public float LightshowBeamScale   = 1f;

    // This manager's slot in the shared global _UdonDiamondLightshowFrames[] array, its
    // identity on the show axis. Seeded into every fixture's block as _ShowIndex at Start, and
    // written each frame as _UdonDiamondLightshowFrames[ShowIndex]. Concurrent managers each
    // take a distinct slot. See DIAMOND-GPU-ACCEL.md "Addressing model" and open item #7.
    public int ShowIndex;

    // --- Time input (module-independent) -----------------------------
    // The show's playback position comes from a normalised [0,1] Animator float param so
    // Diamond is usable standalone, not tied to Heartache (see DIAMOND-GPU-ACCEL.md "Module
    // independence"). Anything that drives an Animator float 0->1 drives the show: Heartache's
    // _Time write, a plain clip lerp, or a manual scrub. With no param set, the serialized Time
    // field below is used (inspector-scrubbable).
    public Animator AnimatorSource;
    public string TimeParameter = "_Time";
    [Range(0f, 1f)] public float Time;
    private bool _hasTimeParam;

    // --- Manager-wide atmosphere -------------------------------------
    // Haze density, scatter strength, and anisotropy are properties of the room's air, not the
    // fixture, so one value is shared across every beam. They live on the manager rather than
    // per-material for central control, and each is independently static or animated (see
    // DIAMOND-MANAGER.md). Once written into the block in Start they override the beam
    // material's serialized _HazeDensity / _ScatterStrength / _Anisotropy for that renderer.
    //
    // Each parameter has an Animate toggle, a static float (used when off), and a proxy
    // transform (used when on). Every proxy is read on one axis, localPosition.y, so unrelated
    // params never share a Vector3 (which keys as a unit) and their Animate toggles stay
    // independent.

    // Off: HazeDensity float. On: HazeProxy.localPosition.y drives _HazeDensity.
    public bool AnimateHaze;
    public float HazeDensity = 0.03f;
    public Transform HazeProxy;

    [Space]

    // Off: ScatterStrength float. On: ScatterProxy.localPosition.y drives _ScatterStrength.
    public bool AnimateScatter;
    public float ScatterStrength = 0.5f;
    public Transform ScatterProxy;

    [Space]

    // Bounds ceilings for the two animated params that widen the beam's lateral spill. The
    // culling AABB is baked once at edit time, so if animated haze/scatter exceeds the value
    // the bounds were sized for, the beam spills past the AABB and gets frustum-culled. These
    // are the max the animated value may reach: the bake sizes bounds to them (when the
    // matching Animate toggle is on), and the runtime clamps the proxy read to them, so runtime
    // never exceeds the baked worst case. Only consulted for an animated param; a static one
    // sizes bounds from its own float. Re-bake after changing a ceiling.
    public float MaxHazeDensity     = 0.15f;
    public float MaxScatterStrength = 1f;

    [Space]

    // Off: Anisotropy float. On: AnisotropyProxy.localPosition.y drives _Anisotropy.
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
    // lamp.gameObject.activeSelf is two extern calls (.gameObject, then
    // .activeSelf). Caching the GameObject makes it one.
    private GameObject[] _lampObjects;

    // --- Cached shader property IDs ----------------------------------
    // Resolved once in Start via VRCShader.PropertyToID and reused every frame. This is what
    // keeps the loop allocation-free: the string-keyed SetColor/SetFloat overloads marshal
    // their string argument on every call, which at hundreds of fixtures per frame is a
    // per-frame GC.Alloc spike. The int overloads allocate nothing.
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

    // BakedTexture-mode (baked lightshow) property IDs.
    private int _idFixtureRow;
    private int _idShowIndex;
    private int _idLightshowTex;
    private int _idLightshowTexelsPerFixture;
    private int _idLightshowColourScale;
    private int _idLightshowBeamScale;
    private int _idLightshowFrameCount;
    private int _idLightshowFrames;
    // Manager-wide master beam-intensity scale, a global in BakedTexture mode. The proxy path
    // folds it per-fixture into _BeamIntensity, but BakedTexture never touches the per-fixture
    // blocks, so it rides as one global the shader multiplies in.
    private int _idBeamIntensityScaleGlobal;

    // Reused "off" colour so the dark path constructs nothing per frame.
    private Color _black;

    // --- Baked-texture runtime guard ---------------------------------
    // The flag the per-frame code checks to know the baked path is actually live this run,
    // distinct from DriveMode (which is the author's request). Set true in Start only when
    // DriveMode is BakedTexture and the bake passed ValidateBake. If the bake is missing or
    // stale, Start logs an error and returns early, leaving this false and the beams dark
    // rather than falling back to the proxy path. Under LiveProxy it stays false and the proxy
    // loop runs.
    private bool _bakeValid;

    // Reused global frame array so Update allocates nothing. Sized to cover this manager's
    // slot, which it fills at index ShowIndex. The shader's fixed-length
    // _UdonDiamondLightshowFrames[DIAMOND_MAX_SHOWS] (16, in DiamondLightshowSample.cginc) is
    // the ceiling; Start rejects a ShowIndex beyond it. Multi-manager coordination is deferred
    // (see DIAMOND-GPU-ACCEL.md open item #7).
    private float[] _frames;

    // --- Per-fixture dirty-check cache -------------------------------
    // The animator only moves a channel on a keyframe, but Update runs every frame. Caching
    // the last-applied inputs per fixture lets an unchanged fixture skip its property-block
    // writes and its beam SetActive toggle. The SetActive skip matters most: SetActive(false)
    // on an already-inactive object still runs GameObject.Deactivate bookkeeping, which at
    // hundreds of fixtures dominates the main thread if it fires every frame.
    //
    // _cacheValid[i] is false until fixture i applies once, so its initial state is always
    // written regardless of what the arrays happen to hold.
    private bool[]    _cacheValid;
    private bool[]    _lastLampActive;
    private float[]   _lastBrightness;
    // Colour cached as three float arrays, not a Vector3[]. A Vector3 == compare is an Udon
    // extern that boxes both operands (one extra heap alloc per fixture per frame); comparing
    // the components as floats boxes nothing. Reading colour.x/.y/.z off the already-boxed
    // 'colour' local is free, since the box happened at the read.
    private float[]   _lastColourX;
    private float[]   _lastColourY;
    private float[]   _lastColourZ;
    private float[]   _lastZoom;
    private float[]   _lastFocus;
    private float[]   _lastBeamIntensity;

    // --- Manager-wide animated-channel dirty-check -------------------
    // Last-applied value per animated manager parameter, so the shared section skips its work
    // when a value is unchanged. Only meaningful when the matching Animate toggle is set.
    // Seeded false so the first frame always applies.
    private bool  _atmoCacheValid;
    private float _lastHaze;
    private float _lastScatter;
    private float _lastAnisotropy;
    private float _lastBeamIntensityScale;

    // --- Lifecycle ---------------------------------------------------

    // Ceiling on ShowIndex: the shader's _UdonDiamondLightshowFrames[] is a fixed-length array
    // (DIAMOND_MAX_SHOWS in DiamondLightshowSample.cginc), so a slot at or beyond it can never
    // be read by the GPU even though the CPU can write it. Kept in sync by hand with the
    // shader's #define, since there's no shared C#/HLSL constant across the Udon seam.
    private const int DiamondMaxShows = 16;   // == DIAMOND_MAX_SHOWS in DiamondLightshowSample.cginc

    // Set in Start if the fixture arrays fail their alignment invariant (see
    // ValidateFixtureArrays). When true, both per-frame paths bail, so a null or short sibling
    // array is one loud line at Start instead of an exception on every frame.
    private bool _fixtureArraysBroken;

    // Start dispatches on the author's declared DriveMode. StartShared always runs (IDs,
    // blocks, arrays, static per-fixture seeding, all of which both paths need), then exactly
    // one mode-specific init runs. BakedTexture requires a valid bake: a missing or stale one
    // logs and returns, leaving beams dark rather than dropping to the proxy path.
    public void Start()
    {
        // Alignment invariant first: everything below (and both per-frame loops) trusts the
        // parallel arrays to be non-null and the same length as LampProps. A partial or failed
        // bake, or a hand-edited manager, can break that, so catch it once here, loudly, instead
        // of letting the hot loop throw on the first "on" fixture every frame.
        if (!ValidateFixtureArrays())
        {
            _fixtureArraysBroken = true;
            return;
        }

        StartShared();

        if (DriveMode == DiamondDriveMode.BakedTexture)
        {
            if (!ValidateBake())
            {
                Debug.LogError("[Diamond] " + name + ": DriveMode is BakedTexture but the " +
                    "bake is missing or stale (no LightshowTex, LightshowFixtureCount != " +
                    "fixture count, LightshowFrameCount <= 0, or LightshowTexelsPerFixture " +
                    "!= " + DiamondLightshowFormat.TexelsPerFixture + "). Beams will not light. " +
                    "Re-bake this manager, or set DriveMode to LiveProxy.");
                return;   // no silent fallback: the author declared baked intent
            }
            // ShowIndex must land in the shader's fixed frame-array window, or the frame this
            // manager publishes lands in a slot the GPU can never read and the beams freeze at
            // frame 0. Loud error, consistent with the module's no-silent-failure rule.
            if (ShowIndex < 0 || ShowIndex >= DiamondMaxShows)
            {
                Debug.LogError("[Diamond] " + name + ": ShowIndex " + ShowIndex + " is out of the " +
                    "supported range [0, " + (DiamondMaxShows - 1) + "]. The shader's frame array " +
                    "can't be read at that slot, so beams would freeze at frame 0. Assign a " +
                    "ShowIndex in range.");
                return;
            }
            _bakeValid = true;
            StartBakedTexture();
        }
        else
        {
            StartLiveProxy();
        }
    }

    // Whether the parallel fixture arrays satisfy the alignment invariant every path relies
    // on: LampProps present, and every sibling array non-null and exactly LampProps.Length.
    // Individual elements may still be null (a beamless fixture has a null BeamProps[i], and
    // the loops handle that); what this rules out is a null or wrong-length array, which would
    // make the per-frame indexing throw. An empty manager (no LampProps, or length 0) is valid
    // and does nothing. Logs the specific offender so a bad bake is diagnosable.
    //
    // The four sibling arrays are different element types (Transform[], Renderer[], bool[]), so
    // each is length-checked inline: U# has no reliable System.Array base-type parameter, and
    // one helper per type would just be four helpers. A -1 (the array itself is null) folds the
    // null and wrong-length cases into one comparison and one message per array.
    private bool ValidateFixtureArrays()
    {
        if (LampProps == null) return true;   // empty manager: Update's own null-guard handles it
        int count = LampProps.Length;
        if (count == 0) return true;

        bool ok = true;
        if ((BeamProps     == null ? -1 : BeamProps.Length)     != count) ok = LogArrayLen("BeamProps",     BeamProps     == null ? -1 : BeamProps.Length,     count) && ok;
        if ((HeadRenderers == null ? -1 : HeadRenderers.Length) != count) ok = LogArrayLen("HeadRenderers", HeadRenderers == null ? -1 : HeadRenderers.Length, count) && ok;
        if ((BeamRenderers == null ? -1 : BeamRenderers.Length) != count) ok = LogArrayLen("BeamRenderers", BeamRenderers == null ? -1 : BeamRenderers.Length, count) && ok;
        if ((SymmetricBeam == null ? -1 : SymmetricBeam.Length) != count) ok = LogArrayLen("SymmetricBeam", SymmetricBeam == null ? -1 : SymmetricBeam.Length, count) && ok;
        return ok;
    }

    // Logs one array-alignment failure and returns false, so the caller can fold it in and
    // still report every bad array in one Start rather than only the first. len is the array's
    // length, or -1 if the array itself was null.
    private bool LogArrayLen(string arrName, int len, int count)
    {
        Debug.LogError("[Diamond] " + name + ": fixture array '" + arrName + "' is " +
            (len < 0 ? "null" : "length " + len) + " but LampProps has " + count +
            " fixtures. The arrays must be index-aligned; re-bake fixtures on the " +
            "DiamondManagerDefinition. Beams will not light.");
        return false;
    }

    // Whether the assigned bake is present and current for the fixture arrays as they stand
    // this run. A false result under BakedTexture is a hard error (see Start): a stale bake
    // would mis-index rows against the live fixture set. Public so the editor preview
    // (DiamondFixtureMapPreview) reuses this exact predicate rather than keeping a drift-prone
    // copy; in edit mode the behaviour runs as plain C#, so it's an ordinary method call. Reads
    // only fields, writes nothing.
    public bool ValidateBake()
    {
        int count = LampProps == null ? 0 : LampProps.Length;
        return LightshowTex != null
               && LightshowFixtureCount == count
               && LightshowFrameCount > 0
               && LightshowTexelsPerFixture == DiamondLightshowFormat.TexelsPerFixture;
    }

    // --- Shared init (both drive modes) ------------------------------
    // Runs for every DriveMode: shader-property IDs, the reusable "off" colour, the per-fixture
    // property blocks, cached lamp objects, dirty-check arrays, and the static per-fixture and
    // manager-wide seeding that neither path re-applies per frame. The mode-specific extras
    // (bake globals, _FixtureRow/_ShowIndex seeding, keyword enable) live in StartBakedTexture.
    private void StartShared()
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

        // Per-block properties (_FixtureRow/_ShowIndex) keep plain names, since they're set via
        // MaterialPropertyBlock rather than as globals. The global ones must start with _Udon:
        // VRChat blocks Udon from setting any global shader property outside that namespace
        // (SetGlobal* on a non-_Udon name throws at runtime). They're prefixed with
        // _UdonDiamondLightshow rather than just _UdonLightshow to avoid colliding with any
        // other world or package's own _Udon-namespaced globals.
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

        int count = LampProps == null ? 0 : LampProps.Length;

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

            // Cache the lamp's GameObject so the per-frame activeSelf read is a single extern
            // instead of lamp.gameObject.activeSelf (two).
            Transform lampT = LampProps == null ? null : LampProps[i];
            _lampObjects[i] = lampT == null ? null : lampT.gameObject;

            // Seed the static per-fixture and manager-wide values into the beam block once.
            // Neither is serialized on the renderer, so both are re-applied at runtime; writing
            // them here rather than each Update means every later per-frame
            // SetPropertyBlock(beamBlock) preserves them, since those only set other keys on
            // this same block. Beamless fixtures (null beam renderer) never get the block.
            //
            //   Emitter size   per-fixture static, from the bake.
            //   Atmosphere     manager-wide, seeded here only if static. An animated param is
            //                  owned by the per-frame path (which pushes its proxy value on the
            //                  first frame), so seeding it here would just be immediately
            //                  overwritten.
            //
            // The BakedTexture path's per-fixture row/show-slot seeding is not here; it
            // re-applies these same blocks in StartBakedTexture, after this.
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

    // --- Shared atmosphere resolution --------------------------------
    // The single source of truth for turning the manager-wide atmosphere state into four
    // concrete scalars. Each parameter reads the proxy's localPosition.y when animated (and
    // wired), else the static inspector float. Haze and scatter are clamped to their Max
    // ceilings when animated, so a proxy overshoot can't spill the beam past the culling AABB
    // the bake sized to that ceiling; a static value sizes its own bounds and needs no cap.
    // Anisotropy and the master intensity scale have no bounds ceiling and pass through
    // unclamped.
    //
    // Called by both runtime paths (PushAnimatedAtmosphereGlobal in BakedTexture,
    // ApplyAnimatedManagerChannels in LiveProxy) and the edit-mode preview, so all three read
    // atmosphere identically and the clamp rules live in one place. Public for the editor call;
    // in edit mode the behaviour runs as plain C#, so it's an ordinary method invocation. Reads
    // only manager fields and writes nothing, so it's safe to call anywhere.
    public void ResolveAtmosphere(out float haze, out float scatter, out float aniso, out float intensityScale)
    {
        haze           = (AnimateHaze               && HazeProxy               != null) ? HazeProxy.localPosition.y               : HazeDensity;
        scatter        = (AnimateScatter            && ScatterProxy            != null) ? ScatterProxy.localPosition.y            : ScatterStrength;
        aniso          = (AnimateAnisotropy         && AnisotropyProxy         != null) ? AnisotropyProxy.localPosition.y         : Anisotropy;
        intensityScale = (AnimateBeamIntensityScale && BeamIntensityScaleProxy != null) ? BeamIntensityScaleProxy.localPosition.y : BeamIntensityScale;

        if (AnimateHaze)    haze    = Mathf.Clamp(haze,    0f, MaxHazeDensity);
        if (AnimateScatter) scatter = Mathf.Clamp(scatter, 0f, MaxScatterStrength);
    }

    // --- Per-frame dispatch ------------------------------------------
    // The single Update entry point. Branches once on DriveMode to the mode's per-frame body.
    // _bakeValid gates the BakedTexture path: if the bake failed validation, Start already
    // logged and left it false, so this does nothing and the beams stay dark rather than
    // falling back to the proxy path. Both bodies re-guard on LampProps through their use of it.
    public void Update()
    {
        // Start found a null or short sibling array and already logged it. Bailing here keeps
        // that from becoming a per-frame throw in the proxy loop's array indexing.
        if (_fixtureArraysBroken) return;
        if (LampProps == null) return;

        if (DriveMode == DiamondDriveMode.BakedTexture)
        {
            if (_bakeValid) UpdateBakedTexture();
            return;
        }

        UpdateLiveProxy();
    }

    // ======================================================================
    //  BakedTexture path (DiLET, Diamond Lightshow Encoding Texture)
    //  Each fixture samples its own row of a baked lookup texture on the GPU.
    //  The manager only publishes the current frame index and animated globals
    //  per frame; there is no per-fixture CPU work. Requires a valid bake.
    // ======================================================================

    // BakedTexture init. Runs only after ValidateBake passed (see Start). Seeds the show-wide
    // global scalars, sizes the frame array, resolves the time source, seeds each fixture's
    // static row and show slot into its blocks, and enables the texture-read shader keyword.
    // StartShared has already built the blocks and seeded emitter size and static atmosphere;
    // this only adds the bake-specific keys.
    private void StartBakedTexture()
    {
        // The global scalars the shader's unpack needs. Set once, constant for the show. The
        // explicit (float) casts are because Udon extern calls don't implicitly widen int to
        // float.
        VRCShader.SetGlobalTexture(_idLightshowTex, LightshowTex);
        VRCShader.SetGlobalFloat(_idLightshowTexelsPerFixture, (float)LightshowTexelsPerFixture);
        VRCShader.SetGlobalFloat(_idLightshowColourScale, LightshowColourScale);
        VRCShader.SetGlobalFloat(_idLightshowBeamScale, LightshowBeamScale);
        VRCShader.SetGlobalFloat(_idLightshowFrameCount, (float)LightshowFrameCount);

        // Master beam-intensity scale as a global. Seeded here so the static case (not
        // animated, so the per-frame push skips it) has a valid value, and so frame 0 never
        // renders with an unset global (0 would leave all beams dark). When animated,
        // PushAnimatedAtmosphereGlobal overwrites it every frame.
        VRCShader.SetGlobalFloat(_idBeamIntensityScaleGlobal, BeamIntensityScale);

        // Global frame array sized to hold this manager's slot. One manager fills ShowIndex, so
        // a length of ShowIndex+1 is enough (the shader clamps its read).
        _frames = new float[ShowIndex + 1];

        // Resolve the time source once. An empty param name (or no animator) falls back to the
        // serialized Time field.
        _hasTimeParam = AnimatorSource != null && TimeParameter != null && TimeParameter != "";

        // Seed each fixture's static row and show slot into its blocks and re-apply. The shader
        // reads these to fetch its own row from the bake texture every frame, so the manager
        // never drives colour/zoom/focus/intensity per fixture. The beam and head blocks were
        // already built and shared-seeded in StartShared; here we add the two bake keys and
        // re-SetPropertyBlock so they land.
        int count = LampProps == null ? 0 : LampProps.Length;
        for (int i = 0; i < count; i++)
        {
            // Beam block: the shaft shaders sample the bake row here.
            Renderer beamRenderer = BeamRenderers == null ? null : BeamRenderers[i];
            if (beamRenderer != null)
            {
                _beamBlocks[i].SetFloat(_idFixtureRow, (float)i);
                _beamBlocks[i].SetFloat(_idShowIndex,  (float)ShowIndex);
                beamRenderer.SetPropertyBlock(_beamBlocks[i]);
            }

            // Head block: the lamp lens's glow pass (Diamond/LampGlow) reads the same row and
            // show slot. This is what drives lamp glow in BakedTexture mode, since the LiveProxy
            // per-fixture head write never runs; without it the lamp stays dark. The head
            // renderer may be null, or carry a non-glow material (a lens with no glow pass);
            // seeding the block is harmless then, as the props go unread.
            Renderer headRenderer = HeadRenderers == null ? null : HeadRenderers[i];
            if (headRenderer != null)
            {
                _headBlocks[i].SetFloat(_idFixtureRow, (float)i);
                _headBlocks[i].SetFloat(_idShowIndex,  (float)ShowIndex);
                headRenderer.SetPropertyBlock(_headBlocks[i]);
            }
        }

        // Turn the shader's baked-lightshow path on for every beam and lamp-glow material this
        // manager owns. EnableKeyword on the shared material affects all instances, so it's done
        // once here. The LiveProxy path leaves it off.
        EnableLightshowKeyword();
    }

    // BakedTexture per-frame body, O(1). The whole per-fixture apply lives on the GPU, where
    // each beam samples its own row of the bake texture. All the manager does is publish the
    // current frame column into its slot of the shared global array: no per-fixture loop, no
    // proxy reads, no boxing.
    private void UpdateBakedTexture()
    {
        float t = _hasTimeParam ? AnimatorSource.GetFloat(TimeParameter) : Time;
        // Normalised [0,1] to a continuous frame column. The shader lerps the two bracketing
        // columns, so a fractional value is expected.
        float frame = Mathf.Clamp01(t) * (float)(LightshowFrameCount - 1);
        _frames[ShowIndex] = frame;
        VRCShader.SetGlobalFloatArray(_idLightshowFrames, _frames);

        // Animated manager-wide atmosphere still needs pushing, but it's a global uniform (the
        // same value for every beam), so it's one SetGlobalFloat per animated param per frame
        // rather than a per-fixture fan-out. Static atmosphere was already seeded per-block in
        // Start. In this project none of these animate, so this is usually a no-op, kept for
        // correctness.
        PushAnimatedAtmosphereGlobal();
    }

    // BakedTexture atmosphere push. Static haze/scatter/aniso are seeded per-block in Start,
    // where they override the material. Animated ones aren't in the block, so pushing them as
    // global uniforms (one call each, not per-fixture) drives every beam at once. Cheap, and
    // only touches a param that actually animates.
    private void PushAnimatedAtmosphereGlobal()
    {
        // Values (including the haze/scatter clamp) come from the shared resolver. The push
        // gates stay here: push only the animated params, and only when their proxy is wired. A
        // static param was already seeded in Start (per-block for haze/scatter/aniso, as a
        // global for the master scale), so re-pushing it would be redundant.
        float haze, scatter, aniso, intensityScale;
        ResolveAtmosphere(out haze, out scatter, out aniso, out intensityScale);

        if (AnimateHaze               && HazeProxy               != null) VRCShader.SetGlobalFloat(_idHazeDensity,               haze);
        if (AnimateScatter            && ScatterProxy            != null) VRCShader.SetGlobalFloat(_idScatterStrength,           scatter);
        if (AnimateAnisotropy         && AnisotropyProxy         != null) VRCShader.SetGlobalFloat(_idAnisotropy,                aniso);
        // Master beam-intensity scale. The texture holds per-fixture beamIntensity, and the
        // shader multiplies this global on top, matching the proxy path's per-fixture
        // `beamIntensity * BeamIntensityScale`. Not clamped: like the proxy path, master scale
        // has no bake ceiling, since it's a straight multiply, not a bounds-baked param.
        if (AnimateBeamIntensityScale && BeamIntensityScaleProxy != null) VRCShader.SetGlobalFloat(_idBeamIntensityScaleGlobal, intensityScale);
    }

    // Enables DIAMOND_LIGHTSHOW_TEX on the beam and lamp-glow materials so their shaders take
    // the texture-read path in play mode. Both shape shaders and DiamondLampGlow gate on this
    // keyword; DiamondFixtureMapPreview forces it off in edit mode so the proxy preview lights
    // up instead. The two never run at once, so whichever is active owns the keyword.
    //
    // Uses sharedMaterials (plural) on the head renderer, because the lamp lens carries two
    // materials (its Mochie look and the Diamond glow) and .sharedMaterial would only reach the
    // first. Enabling the keyword on a material whose shader doesn't declare it (Mochie) is a
    // harmless no-op, so we don't need to identify which slot is the glow.
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

    // ======================================================================
    //  LiveProxy path (U# per-fixture array update)
    //  Update reads each fixture's animated proxy transforms and pushes the
    //  driven values into per-fixture MaterialPropertyBlocks. Instant to
    //  iterate (no bake), fine for small shows.
    // ======================================================================

    // LiveProxy init. StartShared already built the per-fixture blocks, cached the lamp
    // GameObjects, allocated the dirty-check arrays, and seeded static emitter and atmosphere,
    // which is everything this path needs at Start. Kept as an explicit but empty hook so the
    // two paths stay symmetric and a future backend has an obvious seam.
    private void StartLiveProxy()
    {
    }

    // LiveProxy per-frame body. Reads the animated proxies for every fixture, dirty-checks
    // against last frame, and applies only what changed. Manager-wide animated channels run
    // once up front, before the per-fixture loop, so an intensity-scale change can invalidate
    // the per-fixture cache in time for this same frame's loop to recompute.
    private void UpdateLiveProxy()
    {
        int count = LampProps.Length;

        ApplyAnimatedManagerChannels(count);

        for (int i = 0; i < count; i++)
        {
            Transform lamp = LampProps[i];
            if (lamp == null) continue;

            // Read the raw animated inputs once. These are the only channels the animator
            // drives per frame; everything applied below is a deterministic function of them.
            // activeSelf comes off the cached GameObject (one extern, not two).
            //
            // This is the profiled performance floor: each transform.local* read below is a
            // Vector3-returning extern that Udon boxes onto its heap. Boxing is per extern call,
            // not per component (.y is free once boxed), so up to six boxes per fixture per
            // frame, which is the per-frame GC.Alloc baseline. Reading fewer transforms is the
            // only lever, but the proxy-per-channel layout is a deliberate authoring choice, so
            // we eat the boxes rather than repack; a real fix means rethinking how the animator
            // drives fixtures.
            bool    lampActive    = _lampObjects[i].activeSelf;
            float   brightness    = lamp.localPosition.y;
            Vector3 colour        = lamp.localScale;

            // Decide off-ness from lamp data alone, before touching the beam proxy. A dark
            // fixture forces the beam to _black and never applies its zoom/focus/intensity, so
            // reading them would just box three Vector3s for nothing. This is the one lever that
            // reduces boxed reads without repacking the proxy layout: skip the beam reads
            // entirely when off.
            bool off = IsLightOff(lampActive, brightness, colour);

            float zoom          = 0f;
            float focus         = 1f;
            float beamIntensity = 1f;

            // Only read the beam proxy (three boxed Vector3 externs) when the light is on. Off
            // fixtures skip all three, and in a show where many fixtures are dark at once, that's
            // the bulk of the per-frame boxing.
            Transform beam = off ? null : BeamProps[i];
            if (beam != null)
            {
                zoom          = beam.localEulerAngles.x;
                focus         = beam.localPosition.y;
                beamIntensity = beam.localScale.y;
            }

            // Dirty-check. An off fixture compares only the values it actually read (lampActive,
            // brightness, colour) plus the off-state itself: its beam channels weren't read, so
            // they must not participate in the compare, or we'd test a stale local against last
            // frame's value. An on fixture compares everything. _cacheValid[i] guarantees the
            // first apply.
            //
            // _lastLampActive doubles as the "was off last frame" record: an off fixture writes
            // lampActive (false when inactive), but the off-state is fully captured by
            // re-deriving off from the stored lamp values, so a still-off, still-same-colour
            // fixture short-circuits here.
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
            // Only overwrite the cached beam channels when we actually read them. Leaving them
            // untouched for an off fixture is correct: they're not compared while off, and when
            // the fixture next turns on it's a lamp change (off to on), so the dirty-check
            // already forces a fresh apply.
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

    // Reads the animated manager-wide proxies once per frame and applies any that changed.
    // Cheap when idle: for each animated parameter, a bool check, one proxy read, and one float
    // compare, and nothing more until the value moves. Only a real change fans out across the N
    // fixtures.
    //
    // Two kinds of write:
    //   Haze/scatter/anisotropy each own a shared shader key, so on change we push just that key
    //     onto every beam block (preserving the per-fixture keys already there) and
    //     re-SetPropertyBlock.
    //   BeamIntensityScale is a per-fixture multiplier (final _BeamIntensity = animated * scale),
    //     so there's no single key to push. On change, invalidate the per-fixture dirty-check
    //     cache so the loop below recomputes each fixture's _BeamIntensity through the normal
    //     ApplyFixture path.
    //
    // _atmoCacheValid guards the first frame: false until the first read, so an animated channel
    // always applies once regardless of its authored float.
    private void ApplyAnimatedManagerChannels(int count)
    {
        // Resolve the current animated values (static-or-proxy, with the haze/scatter clamp)
        // through the shared resolver, so this path and the texture path can't disagree on how
        // atmosphere is read or clamped.
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

        // A beam-intensity-scale change can't be pushed as a shared key, since the final value
        // is per-fixture. Apply it by making the value current, then forcing every fixture to
        // recompute in the loop below this frame.
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

    // Whether fixture i is effectively dark: proxy disabled, zero brightness, or black colour.
    // Takes the already-read inputs, passed in by the caller, so the dirty-check and the apply
    // agree on the same values.
    private bool IsLightOff(bool lampActive, float brightness, Vector3 colour)
    {
        if (!lampActive) return true;
        if (brightness == 0f) return true;
        if (colour.x == 0f && colour.y == 0f && colour.z == 0f) return true;
        return false;
    }

    // Applies the driven state for fixture i to its renderers. 'off' is computed by the caller
    // from lamp data alone and passed in (the caller also uses it to gate the beam reads), so we
    // don't re-derive it here.
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
                // Only toggle when actually changing state. SetActive(false) on an
                // already-inactive object still runs deactivation bookkeeping
                // (GameObject.Deactivate), which at hundreds of fixtures dominates the main
                // thread if it fires every frame.
                if (beamRenderer.gameObject.activeSelf)
                    beamRenderer.gameObject.SetActive(false);
            }
            return;
        }

        // Colour is the animated RGB from LampProps.localScale, scaled by brightness. Alpha is
        // 1, and the shader's emission ignores it.
        Color drivenColour = new Color(colour.x, colour.y, colour.z, 1f) * brightness;

        headBlock.SetColor(_idEmissionColor, drivenColour);
        headRenderer.SetPropertyBlock(headBlock);

        // Mirror brightness-modulated colour, animated intensity, zoom, and focus onto the beam
        // shaft. Zoom is symmetric (X = Z) for a square cone. Focus is a single instanced shader
        // property, the same on both shapes, so it needs no X/Z split.
        if (beamRenderer != null)
        {
            // Guard the toggle so we don't re-activate every frame (see the SetActive note
            // above).
            if (!beamRenderer.gameObject.activeSelf)
                beamRenderer.gameObject.SetActive(true);
            beamBlock.SetColor(_idColor, drivenColour);
            // Blanket master scale on the shaft intensity (1 by default, a no-op). Folds into
            // the write we already pay; see BeamIntensityScale.
            beamBlock.SetFloat(_idBeamIntensity, beamIntensity * BeamIntensityScale);
            beamBlock.SetFloat(_idZoomX, zoom);
            beamBlock.SetFloat(_idFocus, focus);

            // Round fixtures use the BeamRound shader, which reads only _ZoomX. Rect fixtures
            // need _ZoomZ too, mirrored from _ZoomX here for a symmetric square cone.
            if (!SymmetricBeam[i])
                beamBlock.SetFloat(_idZoomZ, zoom);

            beamRenderer.SetPropertyBlock(beamBlock);
        }
    }
}
