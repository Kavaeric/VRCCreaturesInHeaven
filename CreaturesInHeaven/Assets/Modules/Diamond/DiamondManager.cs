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
    // genuinely-baked runtime scalar -- everything else animated comes from the
    // proxies, everything else static is edit-time-only.
    public bool[] SymmetricBeam;

    // --- Per-fixture property blocks ---------------------------------
    // One MaterialPropertyBlock per fixture per renderer, allocated once in
    // Start. Reused every frame so we never churn allocations. Index-aligned
    // with the fixture arrays.
    private MaterialPropertyBlock[] _headBlocks;
    private MaterialPropertyBlock[] _beamBlocks;

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

        _black = new Color(0f, 0f, 0f, 0f);

        int count = LampProps == null ? 0 : LampProps.Length;

        _headBlocks = new MaterialPropertyBlock[count];
        _beamBlocks = new MaterialPropertyBlock[count];

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

            // Seed the static emitter size into the per-fixture beam block once.
            // It isn't serialized on the renderer, so it must be re-applied at
            // runtime; writing it here (rather than each Update) means every later
            // per-frame SetPropertyBlock(beamBlock) preserves it, since the loop
            // only ever sets *other* keys on this same block. Beamless fixtures
            // (null beam renderer) just never get the block applied.
            Renderer beamRenderer = BeamRenderers == null ? null : BeamRenderers[i];
            if (beamRenderer != null && FixtureEmitterSizes != null)
            {
                _beamBlocks[i].SetFloat(_idEmitterWidth,  FixtureEmitterSizes[i].x);
                _beamBlocks[i].SetFloat(_idEmitterHeight, FixtureEmitterSizes[i].y);
                beamRenderer.SetPropertyBlock(_beamBlocks[i]);
            }
        }
    }

    // --- Per-frame loop ----------------------------------------------

    public void Update()
    {
        if (LampProps == null) return;

        int count = LampProps.Length;
        for (int i = 0; i < count; i++)
        {
            Transform lamp = LampProps[i];
            if (lamp == null) continue;

            // Read the raw animated inputs once, exactly as the old driver did.
            // These are the only channels the animator drives per frame;
            // everything applied below is a deterministic function of them.
            bool    lampActive    = lamp.gameObject.activeSelf;
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
            beamBlock.SetFloat(_idBeamIntensity, beamIntensity);
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
