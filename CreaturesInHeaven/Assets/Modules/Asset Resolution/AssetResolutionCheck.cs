
using UnityEngine;
using UnityEditor;


// AssetResolutionCheck
// Editor-only script that calculates texel density falloff from a given position and headset spec.
// Draws concentric rings marking density falloff over distance.
public class AssetResolutionCheck : MonoBehaviour
{
#if UNITY_EDITOR

    // Shared with AssetResolutionProbe: HeadsetPreset/HeadsetSpec live in HeadsetSpecs.cs,
    // the density math in TexelDensity.cs, and the gizmo helpers in ResolutionGizmos.cs.

    // How each density ring is drawn: a flat circle on the XZ plane, or a full wire sphere.
    enum RingShape { Circle, Sphere }

    [SerializeField] private HeadsetPreset headset = HeadsetPreset.ValveIndex;

    [SerializeField] private int customResX = 1440;
    [SerializeField] private int customResY = 1600;
    [SerializeField] private float customFovH = 108f;
    [SerializeField] private float customFovV = 104f;

    // Density thresholds (px/m). One ring drawn per entry.
    [SerializeField] private int[] densityThresholds = { 512, 256, 128, 64 };

    // A known density/radius pair used as the anchor; all rings are offset so this density lands at this radius
    [SerializeField] private int referenceDensity = 512;
    [SerializeField] private float referenceRadius = 0f;

    [SerializeField] private RingShape ringShape = RingShape.Circle;
    // When true, the rings inherit the object's rotation, so the circle plane follows the transform.
    [SerializeField] private bool orientToTransform = true;
    [SerializeField] private int circleSegments = 64;
    [SerializeField] private Color gizmosColor = Color.red;

    HeadsetSpec GetSpec()
    {
        return HeadsetSpecs.Get(headset, customResX, customResY, customFovH, customFovV);
    }

    void OnDrawGizmos()
    {
        HeadsetSpec hs = GetSpec();

        // Drive the gizmo space off the transform. When oriented, rings inherit the object's rotation;
        // otherwise they stay axis-aligned at the object's position. Everything below is drawn in this
        // local space, so the center is the origin.
        Quaternion rot = orientToTransform ? transform.rotation : Quaternion.identity;
        Gizmos.matrix = Matrix4x4.TRS(transform.position, rot, Vector3.one);
        Vector3 center = Vector3.zero;

        float anchorOffset = referenceRadius - TexelDensity.DensityToDistance(referenceDensity, hs);

        int ringsInside = 0, ringsOutside = 0;
        foreach (int t in densityThresholds)
        {
            if (t > referenceDensity) ringsInside++;
            else if (t < referenceDensity) ringsOutside++;
        }

        int insideIdx = 0, outsideIdx = 0;
        for (int i = 0; i < densityThresholds.Length; i++)
        {
            float density = densityThresholds[i];
            float radius = TexelDensity.DensityToDistance(density, hs) + anchorOffset;

            if (density == referenceDensity)
            {
                Gizmos.color = Color.white;
            }
            else if (density > referenceDensity)
            {
                Gizmos.color = ResolutionGizmos.FadeAlpha(gizmosColor, insideIdx, ringsInside, 1f, 0.2f);
                insideIdx++;
            }
            else
            {
                Gizmos.color = ResolutionGizmos.FadeAlpha(gizmosColor, outsideIdx, ringsOutside, 0.8f, 0.15f);
                outsideIdx++;
            }

            if (ringShape == RingShape.Sphere)
                Gizmos.DrawWireSphere(center, radius);
            else
                ResolutionGizmos.DrawCircle(center, radius, circleSegments);

            ResolutionGizmos.DrawLabel(center + new Vector3(radius, 0f, 0f), $"{density:0} px/m\n{radius:0.##}m");
        }

        Gizmos.matrix = Matrix4x4.identity;
    }

#endif
}

#if UNITY_EDITOR
[CustomEditor(typeof(AssetResolutionCheck))]
public class AssetResolutionCheckEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        var headsetProp = serializedObject.FindProperty("headset");

        // --- Headset ----------------------------------------------------------------
        EditorGUILayout.LabelField("Headset", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(headsetProp,
            new GUIContent("Preset", "The headset whose display specs are used to calculate texel density at distance."));

        // Compare against the enum value rather than a hardcoded index, so adding presets
        // to HeadsetPreset can't silently break the custom-field toggle.
        bool isCustom = headsetProp.enumValueIndex == (int)HeadsetPreset.Custom;
        EditorGUI.indentLevel++;
        EditorGUI.BeginDisabledGroup(!isCustom);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("customResX"), new GUIContent("Horiz. resolution", "Horizontal display resolution in pixels."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("customResY"), new GUIContent("Vert. resolution",  "Vertical display resolution in pixels."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("customFovH"),  new GUIContent("Horiz. FOV",        "Horizontal field of view in degrees."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("customFovV"),  new GUIContent("Vert. FOV",         "Vertical field of view in degrees."));
        EditorGUI.EndDisabledGroup();
        EditorGUI.indentLevel--;

        EditorGUILayout.Space(8f);

        // --- Reference --------------------------------------------------------------
        EditorGUILayout.LabelField("Reference", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("referenceDensity"),
            new GUIContent("Reference density", "The texel density (px/m) that anchors the ring scale. Drawn in white."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("referenceRadius"),
            new GUIContent("Reference radius", "The radius (m) at which the reference density ring is placed. All other rings shift by the same offset."));

        float refRadius = serializedObject.FindProperty("referenceRadius").floatValue;
        if (refRadius > 0f)
            EditorGUILayout.HelpBox($"The reference density ring is pinned to {refRadius:0.##}m. Rings for higher densities will sit inside that radius; lower densities outside.", MessageType.None);

        EditorGUILayout.Space(8f);

        // --- Density rings ----------------------------------------------------------
        EditorGUILayout.LabelField("Density rings", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("densityThresholds"),
            new GUIContent("Thresholds", "List of texel densities (px/m) to draw rings for. The entry matching the reference density is drawn in white; others fade with distance from it."));

        EditorGUILayout.Space(8f);

        // --- Display ----------------------------------------------------------------
        EditorGUILayout.LabelField("Display", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("gizmosColor"),
            new GUIContent("Colour", "Base colour for non-reference rings. Alpha is scaled per ring."));

        var ringShapeProp = serializedObject.FindProperty("ringShape");
        EditorGUILayout.PropertyField(ringShapeProp,
            new GUIContent("Ring shape", "Draw each ring as a flat circle on the XZ plane, or as a full wire sphere."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("orientToTransform"),
            new GUIContent("Orient to transform", "When enabled, the rings inherit the object's rotation. Useful for checking texel density on other planes/axes. When disabled, rings stay axis-aligned."));

        bool isCircle = ringShapeProp.enumValueIndex == 0; // RingShape.Circle
        EditorGUI.BeginDisabledGroup(!isCircle);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("circleSegments"),
            new GUIContent("Circle segments", "Number of line segments used to draw each circle. Higher values are smoother but slower to render. Unused for wire spheres."));
        EditorGUI.EndDisabledGroup();

        serializedObject.ApplyModifiedProperties();
    }
}
#endif
