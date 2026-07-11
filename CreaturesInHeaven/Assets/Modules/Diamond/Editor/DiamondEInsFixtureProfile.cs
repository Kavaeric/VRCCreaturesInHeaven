using UnityEditor;
using UnityEngine;

// Custom inspector for the FixtureProfile asset. The default inspector shows
// every field flat, which is misleading once Shape exists: a Round fixture has
// no separate height (FixtureWidth is its emitter diameter) and a symmetric
// cone, so the rect-only fields shouldn't be presented as if they applied.
// This inspector relabels/hides fields per shape so authoring a profile is
// unambiguous.
[CustomEditor(typeof(DiamondFixtureProfile))]
public class DiamondEInsFixtureProfile : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        var p = (DiamondFixtureProfile)target;

        // --- Identity ------------------------------------------------
        EditorGUILayout.LabelField("Identity", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("FixtureMake"),        new GUIContent("Make"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("FixtureModel"),       new GUIContent("Model"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("FixtureType"),        new GUIContent("Type"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("FixtureDescription"), new GUIContent("Description"));

        // --- Shape + emitter -----------------------------------------
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Shape", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("Shape"));

        bool round = p.Shape == DiamondFixtureProfile.BeamShape.Round;

        if (round)
        {
            // Round: FixtureWidth is the emitter diameter; height is unused.
            EditorGUILayout.PropertyField(serializedObject.FindProperty("FixtureWidth"),
                new GUIContent("Emitter Diameter (m)", "Circular emitter diameter. Radius is half this."));
            EditorGUILayout.HelpBox(
                "Round fixtures use a symmetric cone (single zoom) and the Diamond/BeamRound shader. " +
                "Height and Z-zoom are ignored.", MessageType.None);
        }
        else
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("FixtureWidth"),  new GUIContent("Emitter Width (m)"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("FixtureHeight"), new GUIContent("Emitter Height (m)"));
        }

        // --- Rotation axes -------------------------------------------
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Rotation Axes", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("AxisX"), new GUIContent("X (tilt)"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("AxisY"), new GUIContent("Y (roll)"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("AxisZ"), new GUIContent("Z (pan)"));

        // --- Brightness ----------------------------------------------
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Brightness", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("BrightnessMin"), new GUIContent("Min"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("BrightnessMax"), new GUIContent("Max"));

        // --- Zoom ----------------------------------------------------
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Beam", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("HasZoom"), new GUIContent("Has Zoom"));
        if (p.HasZoom)
        {
            // Zoom is a full cone angle in degrees for both shapes; for round
            // it's the symmetric cone, for rect it's the per-axis half-angle
            // convention the driver expands to a square cone.
            string zoomLabel = round ? "Cone Angle (deg)" : "Zoom (deg)";
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ZoomMinDegrees"),     new GUIContent($"{zoomLabel} Min"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ZoomMaxDegrees"),     new GUIContent($"{zoomLabel} Max"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ZoomDefaultDegrees"), new GUIContent($"{zoomLabel} Default"));
        }

        EditorGUILayout.PropertyField(serializedObject.FindProperty("HasBeam"),
            new GUIContent("Has Beam", "Whether this fixture has a visible volumetric beam shaft."));

        EditorGUILayout.PropertyField(serializedObject.FindProperty("HasFocus"),
            new GUIContent("Has Focus", "Whether this fixture's beam has a programmable focus control."));
        if (p.HasFocus)
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("FocusDefault"),
                new GUIContent("Focus Default", "1 = fully collimated (crisp). 0 = fully defocused."));
        }

#if BAKERY_INCLUDED
        // --- Bakery --------------------------------------------------
        // The backing fields only exist when the Bakery package is present
        // (wrapped in #if BAKERY_INCLUDED on the profile). The whole block --
        // including the DiamondBakeryLightType casts -- compiles out with them.
        var bakeryType = serializedObject.FindProperty("BakeryLightType");
        if (bakeryType != null)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Bakery (ALV)", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(bakeryType, new GUIContent("Light Type"));

            var lightType = (DiamondBakeryLightType)bakeryType.enumValueIndex;

            if (round && lightType != DiamondBakeryLightType.Spot)
            {
                EditorGUILayout.HelpBox(
                    "Round fixtures usually bake as a Spot (cone) light. Other types will still bake, " +
                    "but won't match the round beam's shape.", MessageType.Info);
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("BakeryBrightnessScale"), new GUIContent("Brightness Scale"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("BakeryLightOffset"),     new GUIContent("Light Offset"));

            // Mesh size only matters for the Mesh light type.
            if (lightType == DiamondBakeryLightType.Mesh)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("BakeryMeshLightSize"), new GUIContent("Mesh Light Size"));
        }
#endif

        serializedObject.ApplyModifiedProperties();
    }
}
