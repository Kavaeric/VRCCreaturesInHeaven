using UnityEditor;
using UnityEngine;

// Custom inspector driven by FixtureProfile — only shows controls the fixture type supports.
[CanEditMultipleObjects]
[CustomEditor(typeof(DiamondFixtureDefinition))]
public class DiamondEUIFixtureDefinition : Editor
{
    // serializedObject covers all selected FixtureDefinition components.
    // For child-transform controls (PropsTransform, Head) we iterate targets manually.

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        var def = (DiamondFixtureDefinition)target;

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
            var d = (DiamondFixtureDefinition)t;
            if (d.GetComponent<DiamondFixtureDriver>() == null) anyMissingDriver  = true;
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
        DiamondFixtureProfile commonProfile = ((DiamondFixtureDefinition)targets[0]).Profile;
        foreach (var t in targets)
        {
            if (((DiamondFixtureDefinition)t).Profile != commonProfile)
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
        if (def.Colour == DiamondFixtureDefinition.ColourMode.Blackbody)
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ColourTemperature"), new GUIContent("Temperature (K)"));
            // Preview swatch (read-only, driven by primary target)
            Color preview = DiamondFixtureDefinition.BlackbodyToRGB(def.ColourTemperature);
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
        bool firstOn = ((DiamondFixtureDefinition)targets[0]).GetComponent<DiamondFixtureDriver>().PropsTransform.gameObject.activeSelf;
        bool mixedOn = false;
        foreach (var t in targets)
        {
            var pt = ((DiamondFixtureDefinition)t).GetComponent<DiamondFixtureDriver>().PropsTransform;
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
                var pt = ((DiamondFixtureDefinition)t).GetComponent<DiamondFixtureDriver>().PropsTransform;
                if (pt == null) continue;
                Undo.RecordObject(pt.gameObject, "Fixture On/Off");
                pt.gameObject.SetActive(on);
            }
        }

        // Brightness — display in gamma space; store linear in PropsTransform.localScale.x.
        var firstDriver = ((DiamondFixtureDefinition)targets[0]).GetComponent<DiamondFixtureDriver>();
        float firstBrightnessGamma = firstDriver.PropsTransform != null
            ? Mathf.LinearToGammaSpace(firstDriver.PropsTransform.localScale.x) : 0f;

        bool mixedBrightness = false;
        foreach (var t in targets)
        {
            var pt = ((DiamondFixtureDefinition)t).GetComponent<DiamondFixtureDriver>().PropsTransform;
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
                var pt = ((DiamondFixtureDefinition)t).GetComponent<DiamondFixtureDriver>().PropsTransform;
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
                var pt = ((DiamondFixtureDefinition)t).GetComponent<DiamondFixtureDriver>().PropsTransform;
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
                    var pt = ((DiamondFixtureDefinition)t).GetComponent<DiamondFixtureDriver>().PropsTransform;
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
    private static void DrawAxisSlider(string label, DiamondFixtureProfile.RotationAxis axis, int component, Object[] targets)
    {
        if (!axis.Enabled) return;

        var firstHead = ((DiamondFixtureDefinition)targets[0]).GetComponent<DiamondFixtureDriver>().Head;
        if (firstHead == null)
        {
            EditorGUILayout.HelpBox("Head not assigned on FixtureDriver.", MessageType.Warning);
            return;
        }

        float firstVal = NormaliseAngle(firstHead.localEulerAngles[component]);
        bool mixed = false;
        foreach (var t in targets)
        {
            var h = ((DiamondFixtureDefinition)t).GetComponent<DiamondFixtureDriver>().Head;
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
                var h = ((DiamondFixtureDefinition)t).GetComponent<DiamondFixtureDriver>().Head;
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
