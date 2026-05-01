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
[CanEditMultipleObjects]
[CustomEditor(typeof(FixtureDefinition))]
public class FixtureDefinitionEditor : Editor
{
    // serializedObject covers all selected FixtureDefinition components.
    // For child-transform controls (PropsTransform, Head) we iterate targets manually.

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        var def = (FixtureDefinition)target;

        // --- Profile / metadata --------------------------------------
        EditorGUILayout.PropertyField(serializedObject.FindProperty("Profile"));

        var profile = def.Profile;
        if (profile != null)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Manufacturer", profile.FixtureMake != "" ? profile.FixtureMake : "Found lying by the road");
            EditorGUILayout.LabelField("Model", profile.FixtureModel != "" ? profile.FixtureModel : "—");

            if (profile.FixtureHeight > 0 && profile.FixtureWidth > 0)
                EditorGUILayout.LabelField("Light surface", $"{profile.FixtureHeight:0.0##} × {profile.FixtureWidth:0.0##} m");

        }

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Fixture", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("DisplayName"), new GUIContent("Display name"));

        // Bail early if any selected fixture is missing a driver or profile.
        bool anyMissingDriver  = false;
        bool anyMissingProfile = false;
        foreach (var t in targets)
        {
            var d = (FixtureDefinition)t;
            if (d.GetComponent<FixtureDriver>() == null) anyMissingDriver  = true;
            if (d.Profile == null)                       anyMissingProfile = true;
        }

        if (anyMissingDriver)
        {
            EditorGUILayout.HelpBox("One or more selected fixtures have no FixtureDriver.", MessageType.Warning);
            serializedObject.ApplyModifiedProperties();
            return;
        }

        if (anyMissingProfile)
        {
            EditorGUILayout.HelpBox("Assign a Fixture Profile to enable controls.", MessageType.Info);
            serializedObject.ApplyModifiedProperties();
            return;
        }

        // Bail if selected fixtures use different profiles — controls would be ambiguous.
        FixtureProfile commonProfile = ((FixtureDefinition)targets[0]).Profile;
        foreach (var t in targets)
        {
            if (((FixtureDefinition)t).Profile != commonProfile)
            {
                EditorGUILayout.HelpBox("Selected fixtures use different profiles — edit them individually.", MessageType.Info);
                serializedObject.ApplyModifiedProperties();
                return;
            }
        }

        var p = commonProfile;

        // --- Colour --------------------------------------------------
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Colour", EditorStyles.boldLabel);

        EditorGUILayout.PropertyField(serializedObject.FindProperty("Colour"), new GUIContent("Mode"));

        // Colour-mode-specific controls: read from the primary target for display,
        // serializedObject.ApplyModifiedProperties() propagates to all targets.
        if (def.Colour == FixtureDefinition.ColourMode.Blackbody)
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ColourTemperature"), new GUIContent("Temperature (K)"));
            // Preview swatch (read-only, driven by primary target)
            Color preview = FixtureDefinition.BlackbodyToRGB(def.ColourTemperature);
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.ColorField(new GUIContent("Preview"), preview, false, false, false);
            EditorGUI.EndDisabledGroup();
        }
        else
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("EmissionColor"), new GUIContent("Colour"));
        }

        serializedObject.ApplyModifiedProperties();

        // --- Material channels (child-transform, iterate targets) ----
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Material", EditorStyles.boldLabel);

        // On/Off toggle — show mixed-value indicator if not unanimous.
        bool firstOn = ((FixtureDefinition)targets[0]).GetComponent<FixtureDriver>().PropsTransform.gameObject.activeSelf;
        bool mixedOn = false;
        foreach (var t in targets)
        {
            var pt = ((FixtureDefinition)t).GetComponent<FixtureDriver>().PropsTransform;
            if (pt == null) { mixedOn = true; break; }
            if (pt.gameObject.activeSelf != firstOn) { mixedOn = true; break; }
        }

        EditorGUI.showMixedValue = mixedOn;
        EditorGUI.BeginChangeCheck();
        bool on = EditorGUILayout.Toggle("On", firstOn);
        EditorGUI.showMixedValue = false;
        if (EditorGUI.EndChangeCheck())
        {
            foreach (var t in targets)
            {
                var pt = ((FixtureDefinition)t).GetComponent<FixtureDriver>().PropsTransform;
                if (pt == null) continue;
                Undo.RecordObject(pt.gameObject, "Fixture On/Off");
                pt.gameObject.SetActive(on);
            }
        }

        // Brightness — display in gamma space; store linear in PropsTransform.localScale.x.
        var firstDriver = ((FixtureDefinition)targets[0]).GetComponent<FixtureDriver>();
        float firstBrightnessGamma = firstDriver.PropsTransform != null
            ? Mathf.LinearToGammaSpace(firstDriver.PropsTransform.localScale.x) : 0f;

        bool mixedBrightness = false;
        foreach (var t in targets)
        {
            var pt = ((FixtureDefinition)t).GetComponent<FixtureDriver>().PropsTransform;
            if (pt == null) { mixedBrightness = true; break; }
            if (!Mathf.Approximately(Mathf.LinearToGammaSpace(pt.localScale.x), firstBrightnessGamma))
                mixedBrightness = true;
        }

        EditorGUI.showMixedValue = mixedBrightness;
        EditorGUI.BeginChangeCheck();
        float newGamma = EditorGUILayout.Slider("Brightness", firstBrightnessGamma, p.BrightnessMin, p.BrightnessMax);
        EditorGUI.showMixedValue = false;
        if (EditorGUI.EndChangeCheck())
        {
            float newLinear = Mathf.GammaToLinearSpace(newGamma);
            foreach (var t in targets)
            {
                var pt = ((FixtureDefinition)t).GetComponent<FixtureDriver>().PropsTransform;
                if (pt == null) continue;
                Undo.RecordObject(pt, "Fixture Brightness");
                var s = pt.localScale; s.x = newLinear; pt.localScale = s;
            }
        }

        if (p.HasSpread)
        {
            float firstSpread = firstDriver.PropsTransform != null ? firstDriver.PropsTransform.localScale.y : 0f;
            bool mixedSpread = false;
            foreach (var t in targets)
            {
                var pt = ((FixtureDefinition)t).GetComponent<FixtureDriver>().PropsTransform;
                if (pt == null) { mixedSpread = true; break; }
                if (!Mathf.Approximately(pt.localScale.y, firstSpread)) mixedSpread = true;
            }

            EditorGUI.showMixedValue = mixedSpread;
            EditorGUI.BeginChangeCheck();
            float newSpread = EditorGUILayout.Slider("Spread", firstSpread, 0f, 1f);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck())
            {
                foreach (var t in targets)
                {
                    var pt = ((FixtureDefinition)t).GetComponent<FixtureDriver>().PropsTransform;
                    if (pt == null) continue;
                    Undo.RecordObject(pt, "Fixture Spread");
                    var s = pt.localScale; s.y = newSpread; pt.localScale = s;
                }
            }
        }

        // --- Rotation (child-transform, iterate targets) -------------
        if (p.AxisX.Enabled || p.AxisY.Enabled || p.AxisZ.Enabled)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Rotation", EditorStyles.boldLabel);

            DrawAxisSlider("X (tilt)", p.AxisX, 0, targets);
            DrawAxisSlider("Y (pan)",  p.AxisY, 1, targets);
            DrawAxisSlider("Z (roll)", p.AxisZ, 2, targets);
        }
    }

    // Draws a single rotation-axis slider across all targets, with mixed-value support.
    private static void DrawAxisSlider(string label, FixtureProfile.RotationAxis axis, int component, Object[] targets)
    {
        if (!axis.Enabled) return;

        var firstHead = ((FixtureDefinition)targets[0]).GetComponent<FixtureDriver>().Head;
        if (firstHead == null)
        {
            EditorGUILayout.HelpBox("Head not assigned on FixtureDriver.", MessageType.Warning);
            return;
        }

        float firstVal = NormaliseAngle(firstHead.localEulerAngles[component]);
        bool mixed = false;
        foreach (var t in targets)
        {
            var h = ((FixtureDefinition)t).GetComponent<FixtureDriver>().Head;
            if (h == null) { mixed = true; break; }
            if (!Mathf.Approximately(NormaliseAngle(h.localEulerAngles[component]), firstVal)) mixed = true;
        }

        EditorGUI.showMixedValue = mixed;
        EditorGUI.BeginChangeCheck();
        float newVal = EditorGUILayout.Slider(label, firstVal, axis.Min, axis.Max);
        EditorGUI.showMixedValue = false;
        if (EditorGUI.EndChangeCheck())
        {
            foreach (var t in targets)
            {
                var h = ((FixtureDefinition)t).GetComponent<FixtureDriver>().Head;
                if (h == null) continue;
                Undo.RecordObject(h, "Fixture Rotation");
                var euler = h.localEulerAngles;
                euler[component] = newVal;
                h.localEulerAngles = euler;
            }
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
