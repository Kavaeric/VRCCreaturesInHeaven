using UnityEditor;
using UnityEngine;

// Custom inspector driven by FixtureProfile. Only shows controls the fixture type supports.
[CanEditMultipleObjects]
[CustomEditor(typeof(DiamondFixtureDefinition))]
public class DiamondEInsFixtureDefinition : Editor
{
    // serializedObject covers all selected FixtureDefinition components.
    // For child-transform controls (LampProps, BeamProps, Head) we iterate targets manually.

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

            if (profile.Shape == DiamondFixtureProfile.BeamShape.Round)
            {
                if (profile.FixtureWidth > 0)
                    EditorGUILayout.LabelField("Light surface", $"{profile.FixtureWidth:0.0##} m ⌀");
            }
            else if (profile.FixtureHeight > 0 && profile.FixtureWidth > 0)
            {
                EditorGUILayout.LabelField("Light surface", $"{profile.FixtureHeight:0.0##} × {profile.FixtureWidth:0.0##} m");
            }
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Fixture", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("DisplayName"), new GUIContent("Display name"));

        // --- Object graph references ---------------------------------
        // The per-fixture references the manager bakes from. These live on
        // DiamondFixtureDefinition now (not the driver), so they're drawn here
        // and wired per-prefab. Kept above the driver/profile early-outs below so
        // they're always visible for wiring, even before a profile is assigned.
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("References", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("Head"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("LampProps"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("BeamProps"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("HeadRenderer"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("BeamRenderer"));
        serializedObject.ApplyModifiedProperties();

        // Bail early if any selected fixture is missing a profile. (The driver is
        // retired -- the object graph the controls edit lives on Definition now,
        // so there's no driver to require.)
        bool anyMissingProfile = false;
        foreach (var t in targets)
        {
            var d = (DiamondFixtureDefinition)t;
            if (d.Profile == null) anyMissingProfile = true;
        }

        if (anyMissingProfile)
        {
            EditorGUILayout.HelpBox("Assign a Fixture Profile to enable controls.", MessageType.Info);
            serializedObject.ApplyModifiedProperties();
            return;
        }

        // Bail if selected fixtures use different profiles, as controls would be ambiguous.
        DiamondFixtureProfile commonProfile = ((DiamondFixtureDefinition)targets[0]).Profile;
        foreach (var t in targets)
        {
            if (((DiamondFixtureDefinition)t).Profile != commonProfile)
            {
                EditorGUILayout.HelpBox("Selected fixtures use different profiles. Edit them individually.", MessageType.Info);
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

        // Reset-to-defaults button. Records Undo per target so it's a single
        // reversible operation across multi-select.
        if (GUILayout.Button("Reset to Profile Defaults"))
        {
            foreach (var t in targets)
            {
                var d = (DiamondFixtureDefinition)t;
                if (d.LampProps != null) Undo.RecordObject(d.LampProps, "Reset to Profile Defaults");
                if (d.BeamProps != null) Undo.RecordObject(d.BeamProps, "Reset to Profile Defaults");
                if (d.Head      != null) Undo.RecordObject(d.Head,      "Reset to Profile Defaults");
                d.ApplyProfileDefaults();
            }
        }

        // On/Off toggle. Show mixed-value indicator if not unanimous.
        bool firstOn = ((DiamondFixtureDefinition)targets[0]).LampProps.gameObject.activeSelf;
        bool mixedOn = false;
        foreach (var t in targets)
        {
            var lp = ((DiamondFixtureDefinition)t).LampProps;
            if (lp == null) { mixedOn = true; break; }
            if (lp.gameObject.activeSelf != firstOn) { mixedOn = true; break; }
        }

        EditorGUI.showMixedValue = mixedOn;
        EditorGUI.BeginChangeCheck();
        bool on = EditorGUILayout.Toggle("On", firstOn);
        EditorGUI.showMixedValue = false;
        if (EditorGUI.EndChangeCheck())
        {
            foreach (var t in targets)
            {
                var lp = ((DiamondFixtureDefinition)t).LampProps;
                if (lp == null) continue;
                Undo.RecordObject(lp.gameObject, "Fixture On/Off");
                lp.gameObject.SetActive(on);
            }
        }

        // Brightness: display in gamma space, store linear in LampProps.localPosition.y.
        var firstDef = (DiamondFixtureDefinition)targets[0];
        float firstBrightnessGamma = firstDef.LampProps != null
            ? Mathf.LinearToGammaSpace(firstDef.LampProps.localPosition.y) : 0f;

        bool mixedBrightness = false;
        foreach (var t in targets)
        {
            var lp = ((DiamondFixtureDefinition)t).LampProps;
            if (lp == null) { mixedBrightness = true; break; }
            if (!Mathf.Approximately(Mathf.LinearToGammaSpace(lp.localPosition.y), firstBrightnessGamma))
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
                var lp = ((DiamondFixtureDefinition)t).LampProps;
                if (lp == null) continue;
                Undo.RecordObject(lp, "Fixture Brightness");
                var pos = lp.localPosition; pos.y = newLinear; lp.localPosition = pos;
            }
        }

        if (p.HasSpread)
        {
            // Spread is stored as tan(half-angle) on BeamProps.localEulerAngles.x
            // (rotation, not scale, so it doesn't bundle with beam intensity in
            // the animator) but presented as full cone degrees in the UI.
            float firstSpreadTan     = firstDef.BeamProps != null ? firstDef.BeamProps.localEulerAngles.x : 0f;
            float firstSpreadDegrees = DiamondFixtureDefinition.SpreadTanToDegrees(firstSpreadTan);
            bool mixedSpread = false;
            foreach (var t in targets)
            {
                var bp = ((DiamondFixtureDefinition)t).BeamProps;
                if (bp == null) { mixedSpread = true; break; }
                if (!Mathf.Approximately(bp.localEulerAngles.x, firstSpreadTan)) mixedSpread = true;
            }

            EditorGUI.showMixedValue = mixedSpread;
            EditorGUI.BeginChangeCheck();
            float newDegrees = EditorGUILayout.Slider("Spread (degrees)", firstSpreadDegrees,
                p.SpreadMinDegrees, p.SpreadMaxDegrees);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck())
            {
                float newTan = DiamondFixtureDefinition.SpreadDegreesToTan(newDegrees);
                foreach (var t in targets)
                {
                    var bp = ((DiamondFixtureDefinition)t).BeamProps;
                    if (bp == null) continue;
                    Undo.RecordObject(bp, "Fixture Spread");
                    var euler = bp.localEulerAngles; euler.x = newTan; bp.localEulerAngles = euler;
                }
            }
        }

        if (p.HasBeam)
        {
            // Beam Intensity lives on BeamProps.localScale.y. No clamp -- this
            // represents air/fog density (haze multiplier), not a fixture-internal
            // value.
            float firstBeam = firstDef.BeamProps != null ? firstDef.BeamProps.localScale.y : 0f;
            bool mixedBeam = false;
            foreach (var t in targets)
            {
                var bp = ((DiamondFixtureDefinition)t).BeamProps;
                if (bp == null) { mixedBeam = true; break; }
                if (!Mathf.Approximately(bp.localScale.y, firstBeam)) mixedBeam = true;
            }

            EditorGUI.showMixedValue = mixedBeam;
            EditorGUI.BeginChangeCheck();
            float newBeam = EditorGUILayout.FloatField("Beam Intensity", firstBeam);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck())
            {
                foreach (var t in targets)
                {
                    var bp = ((DiamondFixtureDefinition)t).BeamProps;
                    if (bp == null) continue;
                    Undo.RecordObject(bp, "Fixture Beam Intensity");
                    var s = bp.localScale; s.y = newBeam; bp.localScale = s;
                }
            }
        }

        // --- Rotation (child-transform, iterate targets) -------------
        if (p.AxisX.Enabled || p.AxisY.Enabled || p.AxisZ.Enabled)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Rotation", EditorStyles.boldLabel);

            DrawAxisSlider("X (tilt)", p.AxisX, 0, targets);
            DrawAxisSlider("Y (roll)",  p.AxisY, 1, targets);
            DrawAxisSlider("Z (pan)", p.AxisZ, 2, targets);
        }
    }

    // Draws a single rotation-axis slider across all targets, with mixed-value support.
    private static void DrawAxisSlider(string label, DiamondFixtureProfile.RotationAxis axis, int component, Object[] targets)
    {
        if (!axis.Enabled) return;

        var firstHead = ((DiamondFixtureDefinition)targets[0]).Head;
        if (firstHead == null)
        {
            EditorGUILayout.HelpBox("Head not assigned on the fixture.", MessageType.Warning);
            return;
        }

        float firstVal = NormaliseAngle(firstHead.localEulerAngles[component]);
        bool mixed = false;
        foreach (var t in targets)
        {
            var h = ((DiamondFixtureDefinition)t).Head;
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
                var h = ((DiamondFixtureDefinition)t).Head;
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
