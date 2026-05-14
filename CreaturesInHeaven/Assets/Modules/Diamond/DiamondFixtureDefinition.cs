using UnityEngine;

//  Attach to every fixture prefab root alongside FixtureDriver.
//
//   1. Holds fixture metadata (DisplayName, FixtureProfile) for the fixture map tool.
//
//   2. Mirrors FixtureDriver's material application in edit mode so brightness and
//      spread changes on PropsTransform are visible in the scene.
//
//   3. Exposes friendly controls in the inspector that alias to PropsTransform.localScale
//      and Head.localEulerAngles. Which controls appear is determined by the FixtureProfile.
//      When animated, those underlying properties are what gets keyframed.

[ExecuteAlways]
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

    private DiamondFixtureDriver _driver;
#if UNITY_EDITOR
    private MaterialPropertyBlock _propBlock;
#endif

    private void OnEnable()
    {
        _driver = GetComponent<DiamondFixtureDriver>();

        // Resolve emission colour: blackbody overrides the RGB picker.
        Color emission = EmissionColor;
        if (Colour == ColourMode.Blackbody)
            emission = BlackbodyToRGB(ColourTemperature);

        // Keep FixtureDriver in sync so values are correct at runtime.
        if (_driver != null)
            _driver.EmissionColor = emission;

#if UNITY_EDITOR
        _propBlock = new MaterialPropertyBlock();
#endif
    }

#if UNITY_EDITOR
    private void Update()
    {
        if (Application.isPlaying) return;
        if (_driver == null || _driver.PropsTransform == null || _driver.HeadRenderer == null) return;

        // Resolve emission colour: blackbody overrides the RGB picker.
        Color emission = EmissionColor;
        if (Colour == ColourMode.Blackbody)
            emission = BlackbodyToRGB(ColourTemperature);

        if (!_driver.PropsTransform.gameObject.activeSelf)
        {
            _propBlock.SetColor("_EmissionColor", Color.black);
            _driver.HeadRenderer.SetPropertyBlock(_propBlock);
            return;
        }

        float linearBrightness = _driver.PropsTransform.localScale.x;
        float spread           = _driver.PropsTransform.localScale.y;

        _propBlock.SetColor("_EmissionColor", emission * linearBrightness);
        _propBlock.SetFloat("_Spread", spread);
        _driver.HeadRenderer.SetPropertyBlock(_propBlock);
    }
#endif

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
