using UnityEngine;

//  Attach to every fixture prefab root alongside FixtureDriver.
//
//   1. Holds fixture metadata (DisplayName, FixtureProfile) for the fixture map tool.
//
//   2. In edit mode, DiamondFixtureMapPreview (editor library) drives material preview
//      so brightness, spread, and beam-intensity changes on LampProps/BeamProps are
//      visible in the scene.
//
//   3. Exposes friendly controls in the inspector that alias to LampProps.localPosition.y
//      (brightness), BeamProps.localEulerAngles.x (spread), BeamProps.localScale.y
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

    // Proxy transform: localEulerAngles.x = spread (tan half-angle),
    // localScale.y = beam intensity. Null for beamless fixtures.
    public Transform BeamProps;

    // Renderer whose _EmissionColor is driven by brightness*colour.
    public Renderer HeadRenderer;

    // Renderer on the volumetric beam cube. Null for beamless fixtures.
    public Renderer BeamRenderer;

    // Emission colour for this fixture. Written to FixtureDriver.EmissionColor so
    // it is available at runtime without FixtureDefinition being present.
    [ColorUsage(showAlpha: false, hdr: true)]
    public Color EmissionColor = Color.white;

    public enum ColourMode { RGB, Blackbody }
    public ColourMode Colour = ColourMode.RGB;

    // Colour temperature in Kelvin. Only used when Colour == Blackbody;
    // the resulting RGB is written to EmissionColor and synced to FixtureDriver.
    public float ColourTemperature = 6500f;

    private void OnEnable()
    {
        SyncFixtureEmitterSize();
        SyncDriverColour();
    }

    private void OnValidate()
    {
        SyncFixtureEmitterSize();
        SyncDriverColour();
    }

    public void SyncDriverColour()
    {
        var driver = GetComponent<DiamondFixtureDriver>();
        if (driver == null) return;

        // Resolve emission colour: blackbody overrides the RGB picker. Blackbody
        // is resolved to RGB here, at edit time, so the runtime is Kelvin-agnostic.
        Color resolved = Colour == ColourMode.Blackbody
            ? BlackbodyToRGB(ColourTemperature)
            : EmissionColor;

        // Seed the inspector field (edit-time record of the authored colour).
        driver.EmissionColor = resolved;

        // Seed the runtime colour source: LampProps.localScale carries the RGB
        // the driver reads each frame. This sets the fixture's rest colour;
        // animation clips key localScale to override it at runtime. Written as
        // the raw RGB (HDR components pass through localScale unclamped).
        if (driver.LampProps != null)
        {
            driver.LampProps.localScale = new Vector3(resolved.r, resolved.g, resolved.b);
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
    // reads only _SpreadX. The manager uses this to skip the unused _SpreadZ write.
    public bool SymmetricBeam =>
        Profile != null && Profile.Shape == DiamondFixtureProfile.BeamShape.Round;

    // Computes and writes the beam renderer's worst-case culling bounds, reading
    // the sizing scalars straight from the profile and beam material. This is the
    // driver's old ApplyBeamRendererBounds, sourced from profile/material rather
    // than mirrored driver fields, so it needs no driver. Safe in edit mode.
    // Unlike a property block, Renderer.bounds is serialized, so this persists.
    public void ComputeBeamBounds()
    {
        if (BeamRenderer == null) return;

        // Worst-case sizing scalars. Spread comes from the profile (max cone);
        // the rest come from the beam material, matching what SyncEmitterSize
        // mirrored onto the driver. Defaults match the shader/driver fallbacks.
        float maxSpreadTan     = (Profile != null && Profile.HasSpread)
            ? SpreadDegreesToTan(Profile.SpreadMaxDegrees) : 1f;
        float maxBeamLength    = 50f;
        float maxHazeDensity   = 0.05f;
        float maxScatterStr    = 1f;
        float maxShearX        = 0f;
        float maxShearZ        = 0f;
        Vector3 cubeLocalScale = Vector3.one * 0.1f;

        var beamMat = BeamRenderer.sharedMaterial;
        if (beamMat != null)
        {
            if (beamMat.HasProperty("_BeamLengthMax"))   maxBeamLength  = beamMat.GetFloat("_BeamLengthMax");
            if (beamMat.HasProperty("_HazeDensity"))     maxHazeDensity = beamMat.GetFloat("_HazeDensity");
            if (beamMat.HasProperty("_ScatterStrength")) maxScatterStr  = beamMat.GetFloat("_ScatterStrength");
            if (beamMat.HasProperty("_ShearX"))          maxShearX      = beamMat.GetFloat("_ShearX");
            if (beamMat.HasProperty("_ShearZ"))          maxShearZ      = beamMat.GetFloat("_ShearZ");
            if (beamMat.HasProperty("_CubeLocalScale"))
            {
                Vector4 v = beamMat.GetVector("_CubeLocalScale");
                cubeLocalScale = new Vector3(v.x, v.y, v.z);
            }
        }

        Vector2 emitter = FixtureEmitterSize;

        // Lateral half-extent at the far cap (mirror of the vertex shader's box
        // inflation, via DiamondBeamMath.LateralHalfExtent). Spread is symmetric
        // (X = Z); shear is genuinely per-axis (round leaves both 0).
        float halfLateralX = DiamondBeamMath.LateralHalfExtent(
            emitter.x * 0.5f, maxSpreadTan, maxShearX,
            maxHazeDensity, maxScatterStr, maxBeamLength);
        float halfLateralZ = DiamondBeamMath.LateralHalfExtent(
            emitter.y * 0.5f, maxSpreadTan, maxShearZ,
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

    public void SyncFixtureEmitterSize()
    {
        var driver = GetComponent<DiamondFixtureDriver>();
        if (driver == null || Profile == null) return;

        // Set the correct emitter size based on the assigned profile, then push
        // it onto the beam renderer's property block so the change shows up in
        // edit mode (not just at runtime via Start).
        driver.EmitterSize = new Vector2(Profile.FixtureWidth, Profile.FixtureHeight);
        driver.ApplyBeamEmitterSize();

        // Mirror the beam shape so the runtime driver knows whether the beam is
        // symmetric (round shader reads only _SpreadX) and can skip the unused
        // _SpreadZ write. BeamShape is editor-only; the driver carries a bool.
        driver.SymmetricBeam = Profile.Shape == DiamondFixtureProfile.BeamShape.Round;

        // Push worst-case spread from the profile so the driver can size the
        // beam renderer's bounds correctly. Use the profile's max spread in
        // degrees converted to tan(half-angle) -- same convention as the
        // animated spread storage.
        if (Profile.HasSpread)
        {
            driver.MaxSpreadTan = SpreadDegreesToTan(Profile.SpreadMaxDegrees);
        }

        // Mirror the material-level values that widen the beam's lateral spill
        // (and its length cap) so the renderer bounds enclose the vertex shader's
        // expanded box. Same pattern as DiamondBakeryDriver: read the live
        // material floats so the bounds track material edits. sharedMaterial in
        // edit mode to avoid leaking a material instance.
        var beamMat = driver.BeamRenderer != null ? driver.BeamRenderer.sharedMaterial : null;
        if (beamMat != null)
        {
            if (beamMat.HasProperty("_BeamLengthMax"))
                driver.MaxBeamLength = beamMat.GetFloat("_BeamLengthMax");
            if (beamMat.HasProperty("_HazeDensity"))
                driver.MaxHazeDensity = beamMat.GetFloat("_HazeDensity");
            if (beamMat.HasProperty("_ScatterStrength"))
                driver.MaxScatterStrength = beamMat.GetFloat("_ScatterStrength");
            // Per-axis shear lean. Only the rect shader declares these; round has
            // no shear, so HasProperty leaves the driver's values at 0 for round.
            if (beamMat.HasProperty("_ShearX"))
                driver.MaxShearX = beamMat.GetFloat("_ShearX");
            if (beamMat.HasProperty("_ShearZ"))
                driver.MaxShearZ = beamMat.GetFloat("_ShearZ");
            if (beamMat.HasProperty("_CubeLocalScale"))
            {
                // Beam-space -> object-space counter-scale. The bounds math divides
                // by this to match DiamondBeamVert, so it must track the material.
                Vector4 v = beamMat.GetVector("_CubeLocalScale");
                driver.CubeLocalScale = new Vector3(v.x, v.y, v.z);
            }
        }
        driver.ApplyBeamRendererBounds();
    }

    // Writes the profile's default values to every programmable channel on the
    // fixture (brightness, spread, beam intensity, head rotation). Intended to
    // be invoked manually via the inspector "Reset to Profile Defaults" button
    // -- never auto-fires, so animator-authored curves never get clobbered.
    //
    // Where the profile doesn't have an explicit default field yet (brightness,
    // beam intensity, rotation), we stub sensible values inline. Add proper
    // *Default fields to FixtureProfile later if/when this matters.
    public void ApplyProfileDefaults()
    {
        var driver = GetComponent<DiamondFixtureDriver>();
        if (driver == null || Profile == null) return;

        // Brightness: stub at BrightnessMax (no BrightnessDefault field yet).
        if (driver.LampProps != null)
        {
            var pos = driver.LampProps.localPosition;
            pos.y = Profile.BrightnessMax;
            driver.LampProps.localPosition = pos;
        }

        // Spread: convert the profile's default degrees to tan(half-angle).
        if (Profile.HasSpread && driver.BeamProps != null)
        {
            var euler = driver.BeamProps.localEulerAngles;
            euler.x = SpreadDegreesToTan(Profile.SpreadDefaultDegrees);
            driver.BeamProps.localEulerAngles = euler;
        }

        // Beam intensity: stub at 1.0 (no BeamIntensityDefault field yet).
        if (Profile.HasBeam && driver.BeamProps != null)
        {
            var s = driver.BeamProps.localScale;
            s.y = 1f;
            driver.BeamProps.localScale = s;
        }

        // Head rotation: stub each enabled axis at the midpoint of its range
        // (no AxisDefault field yet). Disabled axes are left untouched.
        if (driver.Head != null)
        {
            var euler = driver.Head.localEulerAngles;
            if (Profile.AxisX.Enabled) euler.x = 0.5f * (Profile.AxisX.Min + Profile.AxisX.Max);
            if (Profile.AxisY.Enabled) euler.y = 0.5f * (Profile.AxisY.Min + Profile.AxisY.Max);
            if (Profile.AxisZ.Enabled) euler.z = 0.5f * (Profile.AxisZ.Min + Profile.AxisZ.Max);
            driver.Head.localEulerAngles = euler;
        }
    }

    // --- Spread conversion ------------------------------------------------
    //
    // Spread is stored on BeamProps.localEulerAngles.x as tan(half-angle) -- the
    // value the beam shader's _SpreadX/_SpreadZ use directly. The user-facing
    // value is the FULL cone angle in degrees (stage-lighting convention: a
    // "30 degree fixture" is 30 degrees tip-to-tip across the cone). These
    // helpers convert at the UI boundary so the per-frame path does no trig.

    public static float SpreadDegreesToTan(float fullConeDegrees)
    {
        return Mathf.Tan(fullConeDegrees * 0.5f * Mathf.Deg2Rad);
    }

    public static float SpreadTanToDegrees(float tan)
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
