using System;
using UdonSharp;
using UnityEngine;

// Runtime driver for a lighting fixture. Attach to the fixture root prefab.
// The parent animator keys properties on two proxy transforms and the head's
// localRotation directly. This script reads those each frame and applies
// brightness/collimation via MaterialPropertyBlock to preserve batching.
//
// Animatable channels are split across two transforms, each using a different
// Unity property, so the animator records them as fully independent curves
// rather than bundling them as a single Vector3 keyframe.
//
// _LampProps:
//   .localPosition.y    - Brightness (emissive multiplier, HDR range 0..2).
//                         Stored on position, not scale, so that localScale
//                         remains free to carry the RGB colour vector.
//   .localScale.xyz     - Emission colour (RGB, HDR: components may exceed 1).
//                         Bundled as one Vector3 so colour fades key cleanly.
//                         FixtureDefinition seeds this at edit time. Blackbody
//                         values get converted to RGB.
//   .gameObject.activeSelf - On/off.
//
// _BeamProps:
//   .localEulerAngles.x - Beam spread, stored as tan(half-angle). UIs convert
//                         to/from degrees at the boundary. (Rotation, not
//                         scale, so it doesn't bundle with intensity.)
//   .localScale.y       - Beam intensity (volumetric shaft brightness; haze).
//
// Free slots on _LampProps: localPosition.x/z, localEulerAngles.xyz.
// Free slots on _BeamProps: localEulerAngles.y/z, localScale.x/z,
// localPosition.xyz -- eight more independent floats.
public class DiamondFixtureDriver : UdonSharpBehaviour
{
    // --- Inspector references ----------------------------------------

    // The moving head child GameObject. The animator keys its localRotation directly.
    public Transform Head;

    // Proxy transform whose localPosition.y carries animated brightness, and
    // whose gameObject.activeSelf is the on/off state.
    public Transform LampProps;

    // Proxy transform whose localEulerAngles.x carries animated spread and
    // localScale.y carries animated beam intensity.
    public Transform BeamProps;

    // The renderer on the head whose emissive is driven by brightness.
    public Renderer HeadRenderer;

    // The renderer on the volumetric beam cube. Optional; leave null if the
    // fixture has no beam (e.g. a wash light with no visible shaft).
    // Material should use the Diamond/Beam shader.
    public Renderer BeamRenderer;

    public Vector2 EmitterSize = new Vector2(1, 1);

    // True for round (symmetric-cone) fixtures using the Diamond/BeamRound
    // shader, which reads only _SpreadX. Mirrored from the profile's BeamShape
    // by DiamondFixtureDefinition.SyncEmitterSize. When set, the driver skips
    // the _SpreadZ write that the round shader would ignore anyway.
    public bool SymmetricBeam = false;

    // Worst-case spread (as tan(half-angle)) used for renderer-bounds sizing.
    // Mirrored from the FixtureProfile by DiamondFixtureDefinition.SyncBounds.
    // Defaults to tan(45 degrees) = 1.0, or a 90-degree max cone.
    public float MaxSpreadTan = 1f;

    // Worst-case beam length (metres) used for renderer-bounds sizing.
    // Mirrored from the shader's _BeamLengthMax. Hardcoded fallback matches
    // the shader's default; override per fixture if a beam material uses a
    // different cap.
    public float MaxBeamLength = 50f;

    // Material-level values that widen the beam's lateral spill, mirrored for
    // renderer-bounds sizing so the culling AABB encloses the vertex shader's
    // expanded box (see DiamondBeamMath.LateralHalfExtent). Worst-case defaults;
    // override per fixture if the beam material differs.
    public float MaxHazeDensity     = 0.05f;
    public float MaxScatterStrength = 1f;

    // Per-axis shear lean (_ShearX / _ShearZ), a sideways displacement per metre
    // of depth. The rect shader can lean the beam off-axis, pushing the far cap
    // a long way sideways (shear * MaxBeamLength metres), so the culling AABB must
    // account for it per axis. Round has no shear (both stay 0). Mirrored from the
    // material by DiamondFixtureDefinition.SyncEmitterSize.
    public float MaxShearX          = 0f;
    public float MaxShearZ          = 0f;

    // Material _CubeLocalScale: the counter-scale the beam is authored against.
    // The vertex shader renders in "beam space" (world metres) then divides by
    // this before applying ObjectToWorld, so the cube's own localScale cancels
    // and the rendered size is transform-scale-independent (see DiamondBeamVert).
    // The bounds math must divide by the same factor, or the box comes out scaled
    // by the cube's localScale (e.g. a 50 m beam culled at 5 m when scale is 0.1).
    // Mirrored from the material; defaults match the material's 0.1.
    public Vector3 CubeLocalScale = Vector3.one * 0.1f;

    // Edit-time seed for the emission colour. FixtureDefinition writes this into
    // LampProps.localScale (the runtime colour source) as the fixture's rest
    // colour; the driver does NOT read this field at runtime. Kept so the
    // authored/blackbody colour has somewhere to live in the inspector.
    public Color EmissionColor = Color.white;

    private MaterialPropertyBlock _propBlock;
    private MaterialPropertyBlock _beamPropBlock;

    // --- Per-frame dirty-check cache ---------------------------------
    // The animator only changes the driven channels on keyframes, but Update
    // runs every frame. Rather than re-push property blocks (and, worse, re-
    // toggle the beam GameObject) on every static frame, we cache the last
    // applied inputs and early-out when nothing moved. This matters at scale:
    // ~600 fixtures each calling SetActive(false) every frame showed up in the
    // profiler as GameObject.Deactivate dominating the main thread, because
    // SetActive(false) on an already-inactive object still does deactivation
    // bookkeeping (unlike SetActive(true), which short-circuits when active).
    //
    // _cacheValid is false until the first Update applies once, so the initial
    // state is always written regardless of what the fields happen to hold.
    private bool    _cacheValid = false;
    private bool    _lastLampActive;
    private float   _lastBrightness;
    private Vector3 _lastColour;
    private float   _lastSpread;
    private float   _lastBeamIntensity;

    // --- Lifecycle ---------------------------------------------------

    public void Start()
    {
        EnsurePropertyBlocks();
        ApplyBeamEmitterSize();
        ApplyBeamRendererBounds();
    }

    // Computes worst-case bounds for the beam renderer and writes them so
    // Unity's frustum culler doesn't disable the renderer when the small
    // proxy cube goes off-screen but the actual beam volume is still visible.
    //
    // The bounds are sized in local space to the fixture root, then assigned
    // as world-space bounds (Unity transforms them by the renderer's local
    // matrix internally when used for culling). Safe to call from edit mode.
    public void ApplyBeamRendererBounds()
    {
        if (BeamRenderer == null) return;

        // Lateral half-extent at the far cap of the beam. Derived from the same
        // formula the vertex shader inflates its bounding cube to
        // (DiamondBeamMath.LateralHalfExtent, mirror of ExpandUnitCubeToFrustumBounds),
        // so the culling AABB is guaranteed to enclose the rasterised geometry
        // instead of relying on a hand-tuned margin that could undersize it when
        // spill grows. Spread is symmetric (X = Z) until per-axis spread is wired,
        // but shear is genuinely per-axis (rect can lean each axis independently;
        // round leaves both 0).
        float halfLateralX = DiamondBeamMath.LateralHalfExtent(
            EmitterSize.x * 0.5f, MaxSpreadTan, MaxShearX,
            MaxHazeDensity, MaxScatterStrength, MaxBeamLength);
        float halfLateralZ = DiamondBeamMath.LateralHalfExtent(
            EmitterSize.y * 0.5f, MaxSpreadTan, MaxShearZ,
            MaxHazeDensity, MaxScatterStrength, MaxBeamLength);

        // Beam-space AABB (world metres): beam fires along +Y from 0 to MaxBeamLength.
        Vector3 beamCenter = new Vector3(0f, MaxBeamLength * 0.5f, 0f);
        Vector3 beamSize   = new Vector3(halfLateralX * 2f, MaxBeamLength, halfLateralZ * 2f);

        // Convert beam space -> object space by dividing out the cube's counter-scale,
        // exactly as DiamondBeamVert does (objectSpace = beamSpace / cubeLocalScale).
        // localToWorld then re-applies the cube's localScale, cancelling it so the box
        // lands at true world size instead of being shrunk by the counter-scale.
        Vector3 cs = SafeCubeLocalScale();
        Vector3 center = new Vector3(beamCenter.x / cs.x, beamCenter.y / cs.y, beamCenter.z / cs.z);
        Vector3 size   = new Vector3(beamSize.x   / cs.x, beamSize.y   / cs.y, beamSize.z   / cs.z);
        Bounds localBounds = new Bounds(center, size);

        // Transform to world space. Renderer.bounds is in world space, so we
        // need to convert. Use the beam renderer's transform (not the fixture
        // root) since the bounds are about that GameObject's mesh.
        var t = BeamRenderer.transform;
        Vector3 worldCenter = t.TransformPoint(localBounds.center);
        // For arbitrary rotations a full corner-transform is needed, but the
        // simpler axis-aligned extent works fine for the worst-case sizing
        // (slightly overestimates after rotation, which is what we want).
        Vector3 worldExtents = t.TransformVector(localBounds.extents);
        worldExtents = new Vector3(Mathf.Abs(worldExtents.x), Mathf.Abs(worldExtents.y), Mathf.Abs(worldExtents.z));

        BeamRenderer.bounds = new Bounds(worldCenter, worldExtents * 2f);
    }

    // CubeLocalScale with any zero/near-zero component replaced by 1, so the
    // beam-space -> object-space divide in the bounds math can't blow up. A zero
    // counter-scale is a misconfiguration; treating it as 1 fails safe (no divide,
    // box stays beam-sized) rather than producing an infinite bound.
    public Vector3 SafeCubeLocalScale()
    {
        return new Vector3(
            Mathf.Abs(CubeLocalScale.x) < 1e-6f ? 1f : CubeLocalScale.x,
            Mathf.Abs(CubeLocalScale.y) < 1e-6f ? 1f : CubeLocalScale.y,
            Mathf.Abs(CubeLocalScale.z) < 1e-6f ? 1f : CubeLocalScale.z);
    }

    // Lazily creates the property blocks so callers from edit mode (e.g.
    // FixtureDefinition.OnValidate -> ApplyBeamEmitterSize) don't NRE before
    // Start has had a chance to run.
    private void EnsurePropertyBlocks()
    {
        if (_propBlock == null)     _propBlock = new MaterialPropertyBlock();
        if (_beamPropBlock == null) _beamPropBlock = new MaterialPropertyBlock();
    }

    // Pushes EmitterSize onto the beam renderer's property block.
    // Safe to call from edit mode (used by FixtureDefinition.SyncEmitterSize).
    public void ApplyBeamEmitterSize()
    {
        if (BeamRenderer == null) return;

        EnsurePropertyBlocks();

        // Merge with whatever's already on the renderer so edit-time sync
        // doesn't clobber other property-block entries (e.g. _Color from
        // a preview helper).
        BeamRenderer.GetPropertyBlock(_beamPropBlock);
        _beamPropBlock.SetFloat("_EmitterWidth",  EmitterSize.x);
        _beamPropBlock.SetFloat("_EmitterHeight", EmitterSize.y);
        BeamRenderer.SetPropertyBlock(_beamPropBlock);
    }

    public void Update()
    {
        if (LampProps == null) return;

        // Read the raw animated inputs once. These are the only channels the
        // animator drives per frame; everything else the driver applies is a
        // deterministic function of them.
        bool    lampActive    = LampProps.gameObject.activeSelf;
        float   brightness    = LampProps.localPosition.y;
        Vector3 colour        = LampProps.localScale;
        float   spread        = 0f;
        float   beamIntensity = 1f;
        if (BeamProps != null)
        {
            spread        = BeamProps.localEulerAngles.x;
            beamIntensity = BeamProps.localScale.y;
        }

        // Skip the whole apply (property-block writes AND the beam SetActive
        // toggle) when nothing the animator drives has moved since last frame.
        // _cacheValid guarantees the first frame always applies.
        if (_cacheValid
            && lampActive    == _lastLampActive
            && brightness    == _lastBrightness
            && colour        == _lastColour
            && spread        == _lastSpread
            && beamIntensity == _lastBeamIntensity)
        {
            return;
        }

        _lastLampActive    = lampActive;
        _lastBrightness    = brightness;
        _lastColour        = colour;
        _lastSpread        = spread;
        _lastBeamIntensity = beamIntensity;
        _cacheValid        = true;

        ApplyMaterialProperties(brightness, colour, spread, beamIntensity);
    }

    // --- Application -------------------------------------------------

    private bool IsLightOff()
    {
        // If LampProps is disabled, it's off.
        if (!LampProps.gameObject.activeSelf)
        {
            return true;
        }

        // If brightness is 0, it basically is.
        float brightness = LampProps.localPosition.y;
        if (brightness == 0)
        {
            return true;
        }

        // So is if the animated colour (LampProps.localScale) is black.
        Vector3 colour = LampProps.localScale;
        if (colour.x == 0f && colour.y == 0f && colour.z == 0f)
        {
            return true;
        }

        return false;
    }

    // Applies the driven state to the renderers. Inputs are read once in Update
    // and passed in so the dirty-check and the apply agree on the same values.
    // Colour comes from LampProps.localScale (RGB); the driver has no static
    // colour source at runtime.
    private void ApplyMaterialProperties(float brightness, Vector3 colour, float spread, float beamIntensity)
    {
        if (HeadRenderer == null || LampProps == null)
        {
            Debug.LogWarning("[Diamond] No HeadRenderer or LampProps.");
            return;
        }

        if (IsLightOff())
        {
            _propBlock.SetColor("_EmissionColor", new Color(0f, 0f, 0f, 0f));
            HeadRenderer.SetPropertyBlock(_propBlock);

            if (BeamRenderer != null)
            {
                _beamPropBlock.SetColor("_Color", new Color(0f, 0f, 0f, 0f));
                BeamRenderer.SetPropertyBlock(_beamPropBlock);
                // Only toggle when actually changing state. SetActive(false) on
                // an already-inactive object still runs deactivation bookkeeping
                // (GameObject.Deactivate), which at ~600 fixtures dominated the
                // main thread when this fired every frame.
                if (BeamRenderer.gameObject.activeSelf)
                    BeamRenderer.gameObject.SetActive(false);
            }
            return;
        }

        // Colour is the animated RGB from LampProps.localScale, scaled by
        // brightness. Alpha is 1; the shader's emission ignores it.
        Color drivenColour = new Color(colour.x, colour.y, colour.z, 1f) * brightness;

        _propBlock.SetColor("_EmissionColor", drivenColour);
        HeadRenderer.SetPropertyBlock(_propBlock);

        // Mirror brightness-modulated colour, animated intensity, and spread
        // onto the beam shaft. Spread is symmetric (X = Z) for a square cone.
        if (BeamRenderer != null)
        {
            // See note above: guard the toggle so we don't re-activate every frame.
            if (!BeamRenderer.gameObject.activeSelf)
                BeamRenderer.gameObject.SetActive(true);
            _beamPropBlock.SetColor("_Color", drivenColour);
            _beamPropBlock.SetFloat("_BeamIntensity", beamIntensity);
            _beamPropBlock.SetFloat("_SpreadX", spread);

            // Round fixtures use the BeamRound shader, which reads only _SpreadX.
            // Rect fixtures need _SpreadZ too (independent per-axis spread; here
            // mirrored from _SpreadX for a symmetric square cone).
            if (!SymmetricBeam)
                _beamPropBlock.SetFloat("_SpreadZ", spread);

            BeamRenderer.SetPropertyBlock(_beamPropBlock);
        }
    }
}
