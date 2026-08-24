
using UnityEngine;
using UnityEditor;

// AssetResolutionProbe
// Editor-only tool: attach to an asset, point vantagePoint at a camera/empty in the scene,
// and the inspector shows the max resolvable texel density from that viewpoint.
//
// Shared with AssetResolutionCheck: HeadsetPreset/HeadsetSpec live in HeadsetSpecs.cs and
// the density math in TexelDensity.cs.
public class AssetResolutionProbe : MonoBehaviour
{
#if UNITY_EDITOR

    [SerializeField] private HeadsetPreset headset = HeadsetPreset.ValveIndex;

    [SerializeField] private int customResX = 1440;
    [SerializeField] private int customResY = 1600;
    [SerializeField] private float customFovH = 108f;
    [SerializeField] private float customFovV = 104f;

    // The viewpoint to measure distance from
    [SerializeField] private Transform vantagePoint;

    // Square asset side length (m) used to estimate required texture resolution
    [SerializeField] private float assetSize = 1f;

    [SerializeField] private Color gizmosColor = Color.cyan;

    // Exposed so the custom editor can resolve the spec without duplicating the preset table.
    public HeadsetSpec GetSpec()
    {
        return HeadsetSpecs.Get(headset, customResX, customResY, customFovH, customFovV);
    }

    void OnDrawGizmos()
    {
        if (vantagePoint == null) return;

        Gizmos.color = gizmosColor;
        Gizmos.DrawLine(transform.position, vantagePoint.position);
        Gizmos.DrawSphere(vantagePoint.position, 0.05f);

        HeadsetSpec hs = GetSpec();
        float dist = Vector3.Distance(transform.position, vantagePoint.position);
        if (dist <= 0f) return;

        float density = TexelDensity.DistanceToDensity(dist, hs);
        string label = $"{density:0.##} px/m";

        if (assetSize > 0f)
        {
            float naivePx = density * assetSize;
            label += $"\n{naivePx:0} px to cover {assetSize:0.##} m";
        }

        Handles.Label(transform.position, label);
    }

#endif
}

#if UNITY_EDITOR
[CustomEditor(typeof(AssetResolutionProbe))]
public class AssetResolutionProbeEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        var probe = (AssetResolutionProbe)this.target;
        var headsetProp = serializedObject.FindProperty("headset");

        // --- Headset ----------------------------------------------------------------
        EditorGUILayout.LabelField("Headset", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(headsetProp,
            new GUIContent("Preset", "The headset whose display specs are used to calculate texel density."));

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

        // --- Vantage point ----------------------------------------------------------
        EditorGUILayout.LabelField("Vantage point", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("vantagePoint"),
            new GUIContent("Vantage point", "A GameObject in the scene representing the viewer's eye position. The distance between this object and the vantage point is used for all calculations."));

        EditorGUILayout.Space(8f);

        // --- Readout ----------------------------------------------------------------
        EditorGUILayout.LabelField("Readout", EditorStyles.boldLabel);

        var vantagePointProp = serializedObject.FindProperty("vantagePoint");
        Transform vp = vantagePointProp.objectReferenceValue as Transform;

        if (vp == null)
        {
            EditorGUILayout.HelpBox("Assign a vantage point to see the density readout.", MessageType.None);
        }
        else
        {
            float dist = Vector3.Distance(probe.transform.position, vp.position);

            // Apply any pending edits to the preset/custom fields before reading the spec back
            // off the instance, so the readout reflects this frame's inspector values.
            serializedObject.ApplyModifiedProperties();
            HeadsetSpec hs = probe.GetSpec();

            float density = TexelDensity.DistanceToDensity(dist, hs);
            float minDetailMeters = TexelDensity.DistanceToMinDetail(dist, hs);
            float minDetailMm = minDetailMeters * 1000f;

            var labelStyle = new GUIStyle(EditorStyles.label) { richText = true };

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField($"Distance:  <b>{dist:0.##} m</b>", labelStyle);
            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField($"Max texel density:  <b>{density:0.##} px/m</b>  ({density / 100f:0.##} px/cm)", labelStyle);
            EditorGUILayout.LabelField($"Min resolvable detail:  <b>{minDetailMm:0.##} mm</b>  ({minDetailMeters:0.##} m)", labelStyle);

            EditorGUILayout.EndVertical();

            // --- Texture size estimate --------------------------------------------------
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Texture size estimate", EditorStyles.boldLabel);

            var assetSizeProp = serializedObject.FindProperty("assetSize");
            EditorGUILayout.PropertyField(assetSizeProp, new GUIContent("Asset size (m)", "Square side length of the asset in metres. Used to estimate the naive texture resolution needed to hit the max resolvable density."));

            float assetSize = assetSizeProp.floatValue;
            if (assetSize > 0f && !float.IsInfinity(density))
            {
                float naivePx = density * assetSize;
                // Round up to the next power of two
                int pot = 1;
                while (pot < (int)naivePx) pot *= 2;

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField($"Naive resolution:  <b>{naivePx:0} px</b>  ({assetSize:0.##} m × {density:0.##} px/m)", labelStyle);
                EditorGUILayout.LabelField($"Next power of two:  <b>{pot} px</b>", labelStyle);
                EditorGUILayout.EndVertical();
            }

            // Nudge Unity to repaint while the scene is live so the readout stays fresh
            if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                Repaint();
        }

        EditorGUILayout.Space(8f);

        // --- Display ----------------------------------------------------------------
        EditorGUILayout.LabelField("Display", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("gizmosColor"),
            new GUIContent("Gizmo colour", "Colour of the line drawn between this object and the vantage point."));

        serializedObject.ApplyModifiedProperties();

        // Force repaint whenever the scene view changes (vantage point moves etc.)
        if (Event.current.type == EventType.Repaint)
            SceneView.RepaintAll();
    }
}
#endif
