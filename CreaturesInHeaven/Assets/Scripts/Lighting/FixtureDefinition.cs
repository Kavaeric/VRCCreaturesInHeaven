using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

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
public class FixtureDefinition : MonoBehaviour
{
    // --- Metadata ----------------------------------------------------

    // Display label shown on the fixture map node.
    public string DisplayName;

    // Profile asset describing this fixture type's capabilities and limits.
    public FixtureProfile Profile;

    // Emission colour for this fixture. Written to FixtureDriver.EmissionColor so
    // it is available at runtime without FixtureDefinition being present.
    [ColorUsage(showAlpha: false, hdr: true)]
    public Color EmissionColor = Color.white;

    public enum ColourMode { RGB, Blackbody }
    public ColourMode Colour = ColourMode.RGB;

    // Colour temperature in Kelvin. Only used when Colour == Blackbody;
    // the resulting RGB is written to EmissionColor and synced to FixtureDriver.
    public float ColourTemperature = 6500f;

    // --- Editor preview ----------------------------------------------

#if UNITY_EDITOR
    private FixtureDriver _driver;
    private MaterialPropertyBlock _propBlock;

    private void OnEnable()
    {
        _driver = GetComponent<FixtureDriver>();
        _propBlock = new MaterialPropertyBlock();
    }

    private void Update()
    {
        if (Application.isPlaying) return;
        if (_driver == null || _driver.PropsTransform == null || _driver.HeadRenderer == null) return;

        if (!_driver.PropsTransform.gameObject.activeSelf)
        {
            _propBlock.SetColor("_EmissionColor", Color.black);
            _driver.HeadRenderer.SetPropertyBlock(_propBlock);
            return;
        }

        // Resolve emission colour: blackbody overrides the RGB picker.
        Color emission = EmissionColor;
        if (Colour == ColourMode.Blackbody)
            emission = BlackbodyToRGB(ColourTemperature);

        // Keep FixtureDriver in sync so values are correct at runtime.
        _driver.EmissionColor = emission;

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

#if UNITY_EDITOR
// Custom inspector driven by FixtureProfile — only shows controls the fixture type supports.
[CustomEditor(typeof(FixtureDefinition))]
public class FixtureDefinitionEditor : Editor
{
    private FixtureDriver _driver;

    private void OnEnable()
    {
        _driver = ((FixtureDefinition)target).GetComponent<FixtureDriver>();
    }

    public override void OnInspectorGUI()
    {
        var def = (FixtureDefinition)target;
        var profile = (FixtureProfile)EditorGUILayout.ObjectField("Profile", def.Profile, typeof(FixtureProfile), false);

        EditorGUILayout.Space(8);

        EditorGUILayout.LabelField("Manufacturer", profile.FixtureMake != "" ? profile.FixtureMake : "Found lying by the road");
        EditorGUILayout.LabelField("Model", profile.FixtureModel != "" ? profile.FixtureModel : "—");

        EditorGUILayout.Space(8);

        // --- Metadata ------------------------------------------------
        EditorGUILayout.LabelField("Fixture", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        string displayName = EditorGUILayout.TextField("Display name", def.DisplayName);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(def, "Fixture definition");
            def.DisplayName = displayName;
            def.Profile = profile;
        }

        if (_driver == null)
        {
            EditorGUILayout.HelpBox("No FixtureDriver found on this GameObject.", MessageType.Warning);
            return;
        }

        if (def.Profile == null)
        {
            EditorGUILayout.HelpBox("Assign a Fixture Profile to enable controls.", MessageType.Info);
            return;
        }

        var p = def.Profile;

        // --- Colour --------------------------------------------------
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Colour", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        var colourMode = (FixtureDefinition.ColourMode)EditorGUILayout.EnumPopup("Mode", def.Colour);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(def, "Fixture controls");
            def.Colour = colourMode;
        }

        EditorGUI.BeginChangeCheck();
        if (def.Colour == FixtureDefinition.ColourMode.Blackbody)
        {
            float temp = EditorGUILayout.Slider("Temperature (K)", def.ColourTemperature, 1000f, 12000f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(def, "Fixture controls");
                def.ColourTemperature = temp;
            }
            // Preview swatch (read-only)
            Color preview = FixtureDefinition.BlackbodyToRGB(def.ColourTemperature);
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.ColorField(new GUIContent("Preview"), preview, false, false, false);
            EditorGUI.EndDisabledGroup();
        }
        else
        {
            Color newColor = EditorGUILayout.ColorField(new GUIContent("Colour"), def.EmissionColor, true, false, true);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(def, "Fixture cpntrols");
                def.EmissionColor = newColor;
            }
        }

        // --- Material channels ---------------------------------------
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Material", EditorStyles.boldLabel);

        if (_driver.PropsTransform != null)
        {
            Vector3 scale = _driver.PropsTransform.localScale;
            bool changed = false;

            EditorGUI.BeginChangeCheck();
            bool on = EditorGUILayout.Toggle("On", _driver.PropsTransform.gameObject.activeSelf);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_driver.PropsTransform.gameObject, "Fixture On/Off");
                _driver.PropsTransform.gameObject.SetActive(on);
            }

            EditorGUI.BeginChangeCheck();
            float brightnessGamma = Mathf.LinearToGammaSpace(scale.x);
            float newGamma = EditorGUILayout.Slider("Brightness", brightnessGamma, p.BrightnessMin, p.BrightnessMax);
            if (EditorGUI.EndChangeCheck()) { scale.x = Mathf.GammaToLinearSpace(newGamma); changed = true; }

            if (p.HasSpread)
            {
                EditorGUI.BeginChangeCheck();
                float newSpread = EditorGUILayout.Slider("Spread", scale.y, 0f, 1f);
                if (EditorGUI.EndChangeCheck()) { scale.y = newSpread; changed = true; }
            }

            if (changed)
            {
                Undo.RecordObject(_driver.PropsTransform, "Fixture controls");
                _driver.PropsTransform.localScale = scale;
            }
        }
        else
        {
            EditorGUILayout.HelpBox("PropsTransform not assigned on FixtureDriver.", MessageType.Warning);
        }

        // --- Rotation ------------------------------------------------
        if (p.AxisX.Enabled || p.AxisY.Enabled || p.AxisZ.Enabled)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Rotation", EditorStyles.boldLabel);
        }

        if (_driver.Head != null)
        {
            Vector3 euler = _driver.Head.localEulerAngles;
            bool changed = false;

            if (p.AxisX.Enabled)
            {
                EditorGUI.BeginChangeCheck();
                float val = EditorGUILayout.Slider("X (tilt)", NormaliseAngle(euler.x), p.AxisX.Min, p.AxisX.Max);
                if (EditorGUI.EndChangeCheck()) { euler.x = val; changed = true; }
            }

            if (p.AxisY.Enabled)
            {
                EditorGUI.BeginChangeCheck();
                float val = EditorGUILayout.Slider("Y (pPan)", NormaliseAngle(euler.y), p.AxisY.Min, p.AxisY.Max);
                if (EditorGUI.EndChangeCheck()) { euler.y = val; changed = true; }
            }

            if (p.AxisZ.Enabled)
            {
                EditorGUI.BeginChangeCheck();
                float val = EditorGUILayout.Slider("Z (roll)", NormaliseAngle(euler.z), p.AxisZ.Min, p.AxisZ.Max);
                if (EditorGUI.EndChangeCheck()) { euler.z = val; changed = true; }
            }

            if (changed)
            {
                Undo.RecordObject(_driver.Head, "Fixture controls");
                _driver.Head.localEulerAngles = euler;
            }
        }
        else
        {
            EditorGUILayout.HelpBox("Head not assigned on FixtureDriver.", MessageType.Warning);
        }
    }

    // Remaps a Unity euler angle (0..360) to the -180..180 range for display.
    private static float NormaliseAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        return angle;
    }
}
#endif
