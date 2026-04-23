
using UnityEngine;
using UnityEditor;

public class AssetResolutionCheck : MonoBehaviour
{
#if UNITY_EDITOR

    enum HeadsetPreset { ValveIndex, QuestPro, Beyond2E, SteamFrame, Custom }

    [SerializeField] private HeadsetPreset headset = HeadsetPreset.ValveIndex;

    [SerializeField] private int customResX = 1440;
    [SerializeField] private int customResY = 1600;
    [SerializeField] private float customFovH = 108f;
    [SerializeField] private float customFovV = 104f;

    // Density thresholds (px/m) — one ring drawn per entry
    [SerializeField] private int[] densityThresholds = { 512, 256, 128, 64 };

    // A known density/radius pair used as the anchor; all rings are offset so this density lands at this radius
    [SerializeField] private int referenceDensity = 512;
    [SerializeField] private float referenceRadius = 0f;

    [SerializeField] private int circleSegments = 64;
    [SerializeField] private Color gizmosColor = Color.red;

    struct HeadsetSpec { public int resX, resY; public float fovH, fovV; }

    HeadsetSpec GetSpec()
    {
        if (headset == HeadsetPreset.ValveIndex)  return new HeadsetSpec { resX = 1440, resY = 1600, fovH = 108f,  fovV = 104f   };
        if (headset == HeadsetPreset.QuestPro)    return new HeadsetSpec { resX = 1800, resY = 1920, fovH = 106f,  fovV = 95.57f };
        if (headset == HeadsetPreset.Beyond2E)    return new HeadsetSpec { resX = 2560, resY = 2560, fovH = 110f,  fovV = 97f    };
        if (headset == HeadsetPreset.SteamFrame)  return new HeadsetSpec { resX = 2160, resY = 2160, fovH = 110f,  fovV = 110f   };
        return new HeadsetSpec { resX = customResX, resY = customResY, fovH = customFovH, fovV = customFovV };
    }

    // Returns the distance (m) at which texel density equals targetDensity px/m for the given headset.
    // texelDensity = 1 / minDetail, minDetail = 2 * dist * tan(π / (ppd * 360))
    // => dist = minDetail / (2 * tan(...))
    static float DensityToDistance(float targetDensity, HeadsetSpec hs)
    {
        float ppdH = hs.resX / hs.fovH;
        float ppdV = hs.resY / hs.fovV;
        float ppdMin = Mathf.Min(ppdH, ppdV); // worst axis → largest detail → most conservative
        float minDetail = 1f / targetDensity;
        float tanHalfPixel = Mathf.Tan(Mathf.PI / (ppdMin * 360f));
        return minDetail / (2f * tanHalfPixel);
    }

    static void DrawCircle(Vector3 center, float radius, int segments)
    {
        float step = 2f * Mathf.PI / segments;
        Vector3 prev = center + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float angle = i * step;
            Vector3 next = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }

    void OnDrawGizmos()
    {
        HeadsetSpec hs = GetSpec();
        Vector3 center = transform.position;

        float anchorOffset = referenceRadius - DensityToDistance(referenceDensity, hs);

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
            float radius = DensityToDistance(density, hs) + anchorOffset;

            if (density == referenceDensity)
            {
                Gizmos.color = Color.white;
            }
            else if (density > referenceDensity)
            {
                float t = ringsInside > 1 ? (float)insideIdx / (ringsInside - 1) : 0f;
                Gizmos.color = new Color(gizmosColor.r, gizmosColor.g, gizmosColor.b, gizmosColor.a * Mathf.Lerp(1f, 0.2f, t));
                insideIdx++;
            }
            else
            {
                float t = ringsOutside > 1 ? (float)outsideIdx / (ringsOutside - 1) : 0f;
                Gizmos.color = new Color(gizmosColor.r, gizmosColor.g, gizmosColor.b, gizmosColor.a * Mathf.Lerp(0.8f, 0.15f, t));
                outsideIdx++;
            }

            DrawCircle(center, radius, circleSegments);

            Vector3 labelPos = center + new Vector3(radius, 0f, 0f);
            Handles.Label(labelPos, $"{density:0} px/m\n{radius:0.##}m");
        }
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
        EditorGUILayout.PropertyField(serializedObject.FindProperty("circleSegments"),
            new GUIContent("Circle segments", "Number of line segments used to draw each circle. Higher values are smoother but slower to render."));

        serializedObject.ApplyModifiedProperties();
    }
}
#endif
