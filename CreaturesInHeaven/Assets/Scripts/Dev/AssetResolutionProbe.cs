
using UnityEngine;
using UnityEditor;

// Editor-only tool: attach to an asset, point vantagePoint at a camera/empty in the scene,
// and the inspector shows the max resolvable texel density from that viewpoint.
public class AssetTexelDensityProbe : MonoBehaviour
{
#if UNITY_EDITOR

    enum HeadsetPreset { ValveIndex, QuestPro, Beyond2E, SteamFrame, Custom }

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

    struct HeadsetSpec { public int resX, resY; public float fovH, fovV; }

    HeadsetSpec GetSpec()
    {
        if (headset == HeadsetPreset.ValveIndex)  return new HeadsetSpec { resX = 1440, resY = 1600, fovH = 108f,  fovV = 104f   };
        if (headset == HeadsetPreset.QuestPro)    return new HeadsetSpec { resX = 1800, resY = 1920, fovH = 106f,  fovV = 95.57f };
        if (headset == HeadsetPreset.Beyond2E)    return new HeadsetSpec { resX = 2560, resY = 2560, fovH = 110f,  fovV = 97f    };
        if (headset == HeadsetPreset.SteamFrame)  return new HeadsetSpec { resX = 2160, resY = 2160, fovH = 110f,  fovV = 110f   };
        return new HeadsetSpec { resX = customResX, resY = customResY, fovH = customFovH, fovV = customFovV };
    }

    // Inverts DensityToDistance: given a distance, returns the max resolvable texel density (px/m).
    // texelDensity = 1 / (2 * dist * tan(π / (ppd * 360)))
    static float DistanceToDensity(float dist, HeadsetSpec hs)
    {
        float ppdH = hs.resX / hs.fovH;
        float ppdV = hs.resY / hs.fovV;
        float ppdMin = Mathf.Min(ppdH, ppdV);
        float tanHalfPixel = Mathf.Tan(Mathf.PI / (ppdMin * 360f));
        return 1f / (2f * dist * tanHalfPixel);
    }

    // Returns the minimum detail size (mm) the headset can resolve at the given distance.
    static float DistanceToMinDetail(float dist, HeadsetSpec hs)
    {
        float density = DistanceToDensity(dist, hs);
        return (1f / density) * 1000f; // convert m to mm
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

        float density = DistanceToDensity(dist, hs);
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
[CustomEditor(typeof(AssetTexelDensityProbe))]
public class AssetTexelDensityProbeEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        var target = (AssetTexelDensityProbe)this.target;
        var headsetProp = serializedObject.FindProperty("headset");

        // --- Headset ----------------------------------------------------------------
        EditorGUILayout.LabelField("Headset", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(headsetProp,
            new GUIContent("Preset", "The headset whose display specs are used to calculate texel density."));

        bool isCustom = headsetProp.enumValueIndex == 4; // HeadsetPreset.Custom
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
            float dist = Vector3.Distance(target.transform.position, vp.position);

            // Use reflection to call the private methods on the MonoBehaviour instance.
            // Since the struct and methods are private, we duplicate the math here inline.
            var headsetIndex = headsetProp.enumValueIndex;
            int resX, resY; float fovH, fovV;
            switch (headsetIndex)
            {
                case 0: resX = 1440; resY = 1600; fovH = 108f;  fovV = 104f;   break; // ValveIndex
                case 1: resX = 1800; resY = 1920; fovH = 106f;  fovV = 95.57f; break; // QuestPro
                case 2: resX = 2560; resY = 2560; fovH = 110f;  fovV = 97f;    break; // Beyond2E
                case 3: resX = 2160; resY = 2160; fovH = 110f;  fovV = 110f;   break; // SteamFrame
                default:
                    resX = serializedObject.FindProperty("customResX").intValue;
                    resY = serializedObject.FindProperty("customResY").intValue;
                    fovH = serializedObject.FindProperty("customFovH").floatValue;
                    fovV = serializedObject.FindProperty("customFovV").floatValue;
                    break;
            }

            float ppdH = resX / fovH;
            float ppdV = resY / fovV;
            float ppdMin = Mathf.Min(ppdH, ppdV);
            float tanHalfPixel = Mathf.Tan(Mathf.PI / (ppdMin * 360f));
            float density = dist > 0f ? 1f / (2f * dist * tanHalfPixel) : float.PositiveInfinity;
            float minDetailMm = dist > 0f ? (1f / density) * 1000f : 0f;
            float minDetailMeters = minDetailMm / 1000f;

            // Draw readout as a styled box
            var boxStyle = new GUIStyle(EditorStyles.helpBox) { richText = true, fontSize = 12 };
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
