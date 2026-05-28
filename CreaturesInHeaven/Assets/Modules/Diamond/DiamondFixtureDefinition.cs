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
        SyncEmitterSize();
        SyncDriverColour();
    }

    private void OnValidate()
    {
        SyncEmitterSize();
        SyncDriverColour();
    }

    public void SyncDriverColour()
    {
        var driver = GetComponent<DiamondFixtureDriver>();
        if (driver == null) return;

        // Resolve emission colour: blackbody overrides the RGB picker.
        driver.EmissionColor = Colour == ColourMode.Blackbody
            ? BlackbodyToRGB(ColourTemperature)
            : EmissionColor;
    }

    public void SyncEmitterSize()
    {
        var driver = GetComponent<DiamondFixtureDriver>();
        if (driver == null || Profile == null) return;

        // Set the correct emitter size based on the assigned profile, then push
        // it onto the beam renderer's property block so the change shows up in
        // edit mode (not just at runtime via Start).
        driver.EmitterSize = new Vector2(Profile.FixtureWidth, Profile.FixtureHeight);
        driver.ApplyBeamEmitterSize();
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
