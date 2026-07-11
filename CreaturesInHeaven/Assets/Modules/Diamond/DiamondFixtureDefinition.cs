using UnityEngine;

//  Attach to every fixture prefab root. This is the fixture's edit-time authoring
//  component and the single owner of its object graph (Head, LampProps, BeamProps,
//  HeadRenderer, BeamRenderer) -- the manager bakes those refs into its arrays, and
//  all edit-time tooling reads them here. (The old per-fixture DiamondFixtureDriver
//  is retired; nothing in the authoring path goes through it anymore.)
//
//   1. Holds fixture metadata (DisplayName, FixtureProfile) for the fixture map tool.
//
//   2. In edit mode, DiamondFixtureMapPreview (editor library) drives material preview
//      so brightness, zoom, and beam-intensity changes on LampProps/BeamProps are
//      visible in the scene.
//
//   3. Exposes friendly controls in the inspector that alias to LampProps.localPosition.y
//      (brightness), BeamProps.localEulerAngles.x (zoom), BeamProps.localScale.y
//      (beam intensity), and Head.localEulerAngles (rotation). Which controls appear
//      is determined by the FixtureProfile. When animated, those underlying properties
//      are what gets keyframed.

public class DiamondFixtureDefinition : MonoBehaviour
{
    // --- Metadata ----------------------------------------------------

    // Display label shown on the fixture map node.
    public string DisplayName;

    // Profile asset describing this fixture type's capabilities and limits.
    public DiamondFixtureProfile Profile;

    // --- Fixture object graph ----------------------------------------
    // The per-fixture references the manager bakes into its arrays. These used
    // to live only on DiamondFixtureDriver; they live here now so the bake (and
    // edit-time tooling) can read the object graph without a runtime driver.
    // Populated per-prefab in the inspector.

    // The moving-head child. The animator keys its localRotation directly.
    public Transform Head;

    // Proxy transform: localPosition.y = brightness, localScale.xyz = emission
    // colour (RGB), gameObject.activeSelf = on/off.
    public Transform LampProps;

    // Proxy transform: localEulerAngles.x = zoom (tan half-angle),
    // localPosition.y = focus (0-1, direct pass-through -- no conversion,
    // unlike zoom's tan/degrees split; kept off the zoom Vector3 so the two can
    // be keyed independently), localScale.y = beam intensity. Null for beamless
    // fixtures.
    public Transform BeamProps;

    // Renderer whose _EmissionColor is driven by brightness*colour.
    public Renderer HeadRenderer;

    // Renderer on the volumetric beam cube. Null for beamless fixtures.
    public Renderer BeamRenderer;

    // Emission colour for this fixture. The resolved RGB is seeded into
    // LampProps.localScale (the runtime colour source the manager reads).
    [ColorUsage(showAlpha: false, hdr: true)]
    public Color EmissionColor = Color.white;

    public enum ColourMode { RGB, Blackbody }
    public ColourMode Colour = ColourMode.RGB;

    // Colour temperature in Kelvin. Only used when Colour == Blackbody;
    // the resulting RGB is written to EmissionColor and seeded onto LampProps.
    public float ColourTemperature = 6500f;

    private void OnEnable()
    {
        SyncFixtureEmitterSize();
        SyncColour();
    }

    private void OnValidate()
    {
        SyncFixtureEmitterSize();
        SyncColour();
    }

    // Seeds the fixture's rest colour onto LampProps.localScale, the RGB channel
    // the manager reads each frame. Formerly wrote through DiamondFixtureDriver;
    // it writes the Definition's own LampProps now that the driver is retired.
    public void SyncColour()
    {
        // Resolve emission colour: blackbody overrides the RGB picker. Blackbody
        // is resolved to RGB here, at edit time, so the runtime is Kelvin-agnostic.
        // We deliberately do NOT write `resolved` back onto EmissionColor: in
        // blackbody mode the RGB picker holds the user's last manual choice, and
        // stomping it every OnValidate would lose it when they switch back to RGB.
        Color resolved = Colour == ColourMode.Blackbody
            ? BlackbodyToRGB(ColourTemperature)
            : EmissionColor;

        // Seed the runtime colour source: LampProps.localScale carries the RGB the
        // manager reads each frame. This sets the fixture's rest colour; animation
        // clips key localScale to override it at runtime. Written as the raw RGB
        // (HDR components pass through localScale unclamped).
        if (LampProps != null)
        {
            LampProps.localScale = new Vector3(resolved.r, resolved.g, resolved.b);
        }
    }

    // --- Bake-facing derived values ----------------------------------
    // These are what the manager bakes into its arrays. They derive from the
    // profile and beam material -- the same sources SyncEmitterSize mirrored onto
    // the driver -- so the bake can read them straight off Definition, no driver.

    // Emitter size for the beam (_EmitterWidth / _EmitterHeight), from the profile.
    public Vector2 FixtureEmitterSize =>
        Profile != null ? new Vector2(Profile.FixtureWidth, Profile.FixtureHeight) : Vector2.one;

    // True for round (symmetric-cone) fixtures using the BeamRound shader, which
    // reads only _ZoomX. The manager uses this to skip the unused _ZoomZ write.
    public bool SymmetricBeam =>
        Profile != null && Profile.Shape == DiamondFixtureProfile.BeamShape.Round;

    // --- Worst-case bounds scalars -----------------------------------
    // The scalars that size the beam's culling AABB. These used to be mirrored
    // fields on DiamondFixtureDriver (MaxZoomTan, MaxShear*, CubeLocalScale, ...);
    // they're derived here straight from the profile and beam material instead,
    // so both ComputeBeamBounds (the bake) and DiamondFixtureBoundsGizmo (the
    // scene-view gizmo) read one source and can't disagree. Defaults match the
    // shader/driver fallbacks for a fixture with no beam material assigned yet.

    // Worst-case zoom (tan half-angle), from the profile's max cone.
    public float MaxZoomTan =>
        (Profile != null && Profile.HasZoom) ? ZoomDegreesToTan(Profile.ZoomMaxDegrees) : 1f;

    // Worst-case beam length (metres). From the beam material's _BeamLengthMax.
    public float MaxBeamLength => BeamMatFloat("_BeamLengthMax", 50f);

    // Material-level values that widen the beam's lateral spill.
    public float MaxHazeDensity     => BeamMatFloat("_HazeDensity",     0.05f);
    public float MaxScatterStrength => BeamMatFloat("_ScatterStrength", 1f);

    // Per-axis shear lean. Only the rect shader declares these; round has no
    // shear, so the fallback leaves both 0 for round fixtures.
    public float MaxShearX => BeamMatFloat("_ShearX", 0f);
    public float MaxShearZ => BeamMatFloat("_ShearZ", 0f);

    // Beam-space -> object-space counter-scale (material _CubeLocalScale). The
    // bounds math divides by this to match DiamondBeamVert. Defaults to 0.1.
    public Vector3 CubeLocalScale
    {
        get
        {
            var beamMat = BeamRenderer != null ? BeamRenderer.sharedMaterial : null;
            if (beamMat != null && beamMat.HasProperty("_CubeLocalScale"))
            {
                Vector4 v = beamMat.GetVector("_CubeLocalScale");
                return new Vector3(v.x, v.y, v.z);
            }
            return Vector3.one * 0.1f;
        }
    }

    // CubeLocalScale with any zero/near-zero component replaced by 1, so the
    // beam-space -> object-space divide can't blow up. Public so the gizmo can
    // reproduce the exact same box the bake writes.
    public Vector3 SafeCubeLocalScale() => SafeScale(CubeLocalScale);

    // Reads a float off the beam renderer's shared material, or returns the
    // fallback if there's no beam material or the property isn't declared.
    // sharedMaterial (not material) in edit mode to avoid leaking an instance.
    private float BeamMatFloat(string prop, float fallback)
    {
        var beamMat = BeamRenderer != null ? BeamRenderer.sharedMaterial : null;
        return (beamMat != null && beamMat.HasProperty(prop)) ? beamMat.GetFloat(prop) : fallback;
    }

    // Computes and writes the beam renderer's worst-case culling bounds, reading
    // the sizing scalars straight from the profile and beam material. This is the
    // driver's old ApplyBeamRendererBounds, sourced from profile/material rather
    // than mirrored driver fields, so it needs no driver. Safe in edit mode.
    // Unlike a property block, Renderer.bounds is serialized, so this persists.
    //
    // hazeCeiling / scatterStrengthCeiling override the material-sourced max haze
    // and scatter when >= 0. The manager passes its animated-parameter ceilings so
    // the AABB is sized to the runtime max, not the material's static value (which
    // is only a starting point once the parameter animates). Pass a negative value
    // (the default) to use the material -- correct for a static parameter.
    public void ComputeBeamBounds(float hazeCeiling = -1f, float scatterStrengthCeiling = -1f)
    {
        if (BeamRenderer == null) return;

        // Worst-case sizing scalars, read from the shared bounds-scalar properties
        // above so the bake and the gizmo can never diverge. Haze and scatter take
        // the manager's ceiling instead when one is supplied (>= 0), so animated
        // atmosphere sizes the AABB to its runtime max rather than the material.
        float maxZoomTan       = MaxZoomTan;
        float maxBeamLength    = MaxBeamLength;
        float maxHazeDensity   = hazeCeiling           >= 0f ? hazeCeiling           : MaxHazeDensity;
        float maxScatterStr    = scatterStrengthCeiling >= 0f ? scatterStrengthCeiling : MaxScatterStrength;
        float maxShearX        = MaxShearX;
        float maxShearZ        = MaxShearZ;
        Vector3 cubeLocalScale = CubeLocalScale;

        Vector2 emitter = FixtureEmitterSize;

        // Lateral half-extent at the far cap (mirror of the vertex shader's box
        // inflation, via DiamondBeamMath.LateralHalfExtent). Zoom is symmetric
        // (X = Z); shear is genuinely per-axis (round leaves both 0).
        float halfLateralX = DiamondBeamMath.LateralHalfExtent(
            emitter.x * 0.5f, maxZoomTan, maxShearX,
            maxHazeDensity, maxScatterStr, maxBeamLength);
        float halfLateralZ = DiamondBeamMath.LateralHalfExtent(
            emitter.y * 0.5f, maxZoomTan, maxShearZ,
            maxHazeDensity, maxScatterStr, maxBeamLength);

        // Beam-space AABB (world metres): beam fires along +Y, 0..maxBeamLength.
        Vector3 beamCenter = new Vector3(0f, maxBeamLength * 0.5f, 0f);
        Vector3 beamSize   = new Vector3(halfLateralX * 2f, maxBeamLength, halfLateralZ * 2f);

        // Beam space -> object space by dividing out the cube's counter-scale,
        // exactly as DiamondBeamVert does; localToWorld re-applies it, cancelling.
        Vector3 cs = SafeScale(cubeLocalScale);
        Vector3 center = new Vector3(beamCenter.x / cs.x, beamCenter.y / cs.y, beamCenter.z / cs.z);
        Vector3 size   = new Vector3(beamSize.x   / cs.x, beamSize.y   / cs.y, beamSize.z   / cs.z);
        Bounds localBounds = new Bounds(center, size);

        // Renderer.bounds is world space; transform through the beam renderer's
        // own transform (the mesh's), not the fixture root.
        var t = BeamRenderer.transform;
        Vector3 worldCenter  = t.TransformPoint(localBounds.center);
        Vector3 worldExtents = t.TransformVector(localBounds.extents);
        worldExtents = new Vector3(Mathf.Abs(worldExtents.x), Mathf.Abs(worldExtents.y), Mathf.Abs(worldExtents.z));

        BeamRenderer.bounds = new Bounds(worldCenter, worldExtents * 2f);
    }

    // Scale with any zero/near-zero component replaced by 1, so the beam-space ->
    // object-space divide can't blow up. A zero counter-scale is a misconfig;
    // treating it as 1 fails safe (box stays beam-sized) rather than infinite.
    private static Vector3 SafeScale(Vector3 s)
    {
        return new Vector3(
            Mathf.Abs(s.x) < 1e-6f ? 1f : s.x,
            Mathf.Abs(s.y) < 1e-6f ? 1f : s.y,
            Mathf.Abs(s.z) < 1e-6f ? 1f : s.z);
    }

    // Property block reused for the edit-time emitter-size push, so repeated
    // OnValidate calls don't allocate a fresh block each time.
    private MaterialPropertyBlock _emitterBlock;

    public void SyncFixtureEmitterSize()
    {
        if (Profile == null || BeamRenderer == null) return;

        // Push the profile's emitter size onto the beam renderer's property block
        // so the change shows up in edit mode (not just at runtime via the
        // manager's Start). Merge with the existing block so this doesn't clobber
        // other entries (e.g. a preview helper's _Color). Formerly the driver's
        // ApplyBeamEmitterSize; inlined here now that the driver is retired.
        if (_emitterBlock == null) _emitterBlock = new MaterialPropertyBlock();
        Vector2 emitter = FixtureEmitterSize;
        BeamRenderer.GetPropertyBlock(_emitterBlock);
        _emitterBlock.SetFloat("_EmitterWidth",  emitter.x);
        _emitterBlock.SetFloat("_EmitterHeight", emitter.y);
        BeamRenderer.SetPropertyBlock(_emitterBlock);

        // Recompute the culling bounds from the profile/material. The worst-case
        // zoom, shear, haze, and cube-scale scalars are all read inside
        // ComputeBeamBounds off the shared bounds-scalar properties, so there's
        // nothing to mirror onto a driver anymore.
        ComputeBeamBounds();
    }

    // Writes the profile's default values to every programmable channel on the
    // fixture (brightness, zoom, beam intensity, head rotation). Intended to
    // be invoked manually via the inspector "Reset to Profile Defaults" button
    // -- never auto-fires, so animator-authored curves never get clobbered.
    //
    // Where the profile doesn't have an explicit default field yet (brightness,
    // beam intensity, rotation), we stub sensible values inline. Add proper
    // *Default fields to FixtureProfile later if/when this matters.
    public void ApplyProfileDefaults()
    {
        if (Profile == null) return;

        // Writes the profile defaults straight to the fixture's own proxy
        // transforms. Formerly went through DiamondFixtureDriver's mirrored refs;
        // reads the Definition's own LampProps/BeamProps/Head now.

        // Brightness: stub at BrightnessMax (no BrightnessDefault field yet).
        if (LampProps != null)
        {
            var pos = LampProps.localPosition;
            pos.y = Profile.BrightnessMax;
            LampProps.localPosition = pos;
        }

        // Zoom: convert the profile's default degrees to tan(half-angle).
        if (Profile.HasZoom && BeamProps != null)
        {
            var euler = BeamProps.localEulerAngles;
            euler.x = ZoomDegreesToTan(Profile.ZoomDefaultDegrees);
            BeamProps.localEulerAngles = euler;
        }

        // Beam intensity: stub at 1.0 (no BeamIntensityDefault field yet).
        if (Profile.HasBeam && BeamProps != null)
        {
            var s = BeamProps.localScale;
            s.y = 1f;
            BeamProps.localScale = s;
        }

        // Focus: reset to the profile's default. Carried on localPosition.y
        // (its own Vector3), so it can be keyed independently of zoom.
        if (Profile.HasFocus && BeamProps != null)
        {
            var pos = BeamProps.localPosition;
            pos.y = Profile.FocusDefault;
            BeamProps.localPosition = pos;
        }

        // Head rotation: stub each enabled axis at the midpoint of its range
        // (no AxisDefault field yet). Disabled axes are left untouched.
        if (Head != null)
        {
            var euler = Head.localEulerAngles;
            if (Profile.AxisX.Enabled) euler.x = 0.5f * (Profile.AxisX.Min + Profile.AxisX.Max);
            if (Profile.AxisY.Enabled) euler.y = 0.5f * (Profile.AxisY.Min + Profile.AxisY.Max);
            if (Profile.AxisZ.Enabled) euler.z = 0.5f * (Profile.AxisZ.Min + Profile.AxisZ.Max);
            Head.localEulerAngles = euler;
        }
    }

    // --- Zoom conversion ------------------------------------------------
    //
    // Zoom is stored on BeamProps.localEulerAngles.x as tan(half-angle) -- the
    // value the beam shader's _ZoomX/_ZoomZ use directly. The user-facing
    // value is the FULL cone angle in degrees (stage-lighting convention: a
    // "30 degree fixture" is 30 degrees tip-to-tip across the cone). These
    // helpers convert at the UI boundary so the per-frame path does no trig.

    public static float ZoomDegreesToTan(float fullConeDegrees)
    {
        return Mathf.Tan(fullConeDegrees * 0.5f * Mathf.Deg2Rad);
    }

    public static float ZoomTanToDegrees(float tan)
    {
        return Mathf.Atan(tan) * 2f * Mathf.Rad2Deg;
    }

    // Converts a colour temperature in Kelvin to a linear RGB approximation.
    // Based on Tanner Helland's algorithm, valid over roughly 1000K–40000K.
    public static Color BlackbodyToRGB(float kelvin)
    {
        float t = Mathf.Clamp(kelvin, 1000f, 40000f) / 100f;
        float r, g, b;

        r = t <= 66f
            ? 1f
            : Mathf.Clamp01(1.2929362f * Mathf.Pow(t - 60f, -0.1332048f));

        if (t <= 66f)
            g = Mathf.Clamp01(0.3900816f * Mathf.Log(t) - 0.6318415f);
        else
            g = Mathf.Clamp01(1.1298909f * Mathf.Pow(t - 60f, -0.0755148f));

        b = t >= 66f
            ? 1f
            : t <= 19f
                ? 0f
                : Mathf.Clamp01(0.5432067f * Mathf.Log(t - 10f) - 1.1962541f);

        // Convert from sRGB to linear for correct material application.
        return new Color(
            Mathf.GammaToLinearSpace(r),
            Mathf.GammaToLinearSpace(g),
            Mathf.GammaToLinearSpace(b),
            1f
        );
    }
}
