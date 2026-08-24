
using System;
using UnityEngine;
using UnityEditor;

// ImposterThreshold
// Editor-only tool: answers "how far away can an object be before a 2D imposter is
// indistinguishable from the real geometry?"
//
// An object stops needing real depth once a moving viewer can no longer see its parts
// shift against each other. Two limits decide that, and the nearer one wins:
//   - acuity: the parallax shift drops below one display pixel
//   - Weber:  the shift drops below the fraction of nearby feature spacing the eye can judge
// Both live in TexelDensity.cs.
//
// Holds a list of objects, each with its own depth and detail spacing, all measured against
// one shared viewer. Comparing them against a common wander distance and headset is the point:
// the answer for a building is only useful next to the answer for the railing in front of it.
//
// Self-contained: the view axis is this object's own forward, so nothing else in the scene
// needs setting up. All inputs are typed into the inspector.
//
// Shared with the other Asset Resolution tools: HeadsetPreset/HeadsetSpec live in
// HeadsetSpecs.cs and the gizmo helpers in ResolutionGizmos.cs.
public class ImposterThreshold : MonoBehaviour
{
#if UNITY_EDITOR

    // One object under test. Only the two properties that vary per object live here; the
    // viewer and headset are shared across the whole list.
    [Serializable]
    public class Entry
    {
        [Tooltip("Label for this object, shown on its gizmo and in the readout.")]
        public string name = "Object";

        // Effective depth along the view axis (m). This is the object's own front-to-back
        // extent, not its distance from the viewer: it is the depth that parallax reveals.
        [Tooltip("The object's front-to-back extent along the view axis, in metres. This is the depth parallax reveals, not the distance to the viewer.")]
        public float depth = 1f;

        // Transverse spacing between the nearest pair of comparable silhouette features (m).
        // The parallax shift is judged against this spacing, so widely spaced features make
        // the same shift proportionally smaller and easier to hide: coarse detail lets the
        // imposter start nearer, fine detail pushes it further out.
        [Tooltip("Transverse spacing between the nearest pair of comparable silhouette features, in metres. The parallax shift is judged against this spacing, so coarse detail lets the imposter start nearer and fine detail pushes it further out.")]
        public float detailSeparation = 0.25f;

        // Drawn in the shared gizmo colour when left fully transparent, so entries only need
        // a colour when telling them apart in a crowded scene actually matters.
        [Tooltip("Gizmo colour for this entry. Leave the alpha at zero to use the shared gizmo colour.")]
        public Color color = new Color(1f, 1f, 1f, 0f);

        [Tooltip("Whether this entry's threshold line is drawn in the scene view.")]
        public bool draw = true;
    }

    [SerializeField] private HeadsetPreset headset = HeadsetPreset.ValveIndex;

    [SerializeField] private int customResX = 1440;
    [SerializeField] private int customResY = 1600;
    [SerializeField] private float customFovH = 108f;
    [SerializeField] private float customFovV = 104f;

    // How far the viewer can move sideways while observing the object (m). A viewer standing
    // still still has two eyes, so the floor here is interpupillary distance, ~0.064 m.
    [SerializeField] private float wanderDistance = 0.064f;

    // Where the object origin sits along the wander span, as a fraction of it. 0.5 centres the
    // span on the origin; 0 puts the origin at the -X end, 1 at the +X end. Only moves the
    // gizmo: the imposter distance depends on how far the viewer travels, not on where that
    // travel sits relative to the object.
    [Range(0f, 1f)]
    [SerializeField] private float wanderOffset = 0.5f;

    // Weber fraction for visual discrimination (unitless). 0.05 is the usual assumption.
    [SerializeField] private float weberFraction = 0.05f;

    // The objects under test, each measured against the shared viewer above.
    [SerializeField] private Entry[] entries = { new Entry() };

    [SerializeField] private Color gizmosColor = Color.white;

    HeadsetSpec GetSpec()
    {
        return HeadsetSpecs.Get(headset, customResX, customResY, customFovH, customFovV);
    }

    // The three distances the tool reports. Bundled so the inspector and the gizmo agree
    // without either recomputing the other's numbers.
    public struct Result
    {
        public float acuity;     // acuity-limited distance (m)
        public float weber;      // Weber-limited distance (m)
        public float governing;  // the smaller of the two (m)
        public bool weberGoverns;
    }

    public Result Evaluate(Entry e)
    {
        Result r = new Result();
        if (e == null) return r;

        float acuityRadians = TexelDensity.AngularResolution(GetSpec());

        r.acuity = TexelDensity.ImposterDistanceAcuity(wanderDistance, e.depth, acuityRadians);
        r.weber = TexelDensity.ImposterDistanceWeber(wanderDistance, e.depth, weberFraction, e.detailSeparation);
        r.governing = Mathf.Min(r.acuity, r.weber);
        r.weberGoverns = r.weber < r.acuity;
        return r;
    }

    // The entry list, for the custom inspector's readout table.
    public Entry[] GetEntries()
    {
        return entries;
    }

    // An entry's gizmo colour, falling back to the shared colour when the entry leaves its
    // own fully transparent.
    Color ResolveColor(Entry e)
    {
        return e.color.a > 0f ? e.color : gizmosColor;
    }

    // Exposed for the inspector readout, which reports acuity in arcminutes.
    public float GetAngularResolution()
    {
        return TexelDensity.AngularResolution(GetSpec());
    }

    void OnDrawGizmos()
    {
        if (entries == null || entries.Length == 0) return;

        // Draw in the object's local space: forward is the view axis, right is the wander
        // axis. Everything below is therefore expressed relative to the origin.
        Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);

        // The wander line is shared by every entry, so it is drawn once rather than restated
        // per object.
        //
        // wanderOffset places the object origin along that span: at 0.5 the span straddles the
        // origin, at 0 it runs entirely in +X, at 1 entirely in -X.
        float halfWander = wanderDistance * 0.5f;
        float wanderMin = -wanderDistance * wanderOffset;
        Vector3 wanderA = Vector3.right * wanderMin;
        Vector3 wanderB = Vector3.right * (wanderMin + wanderDistance);

        // Cap size keyed off the furthest threshold drawn, so it stays legible whether the
        // list spans metres or kilometres.
        float furthest = 0f;
        foreach (Entry e in entries)
        {
            if (e == null || !e.draw) continue;
            float d = Evaluate(e).governing;
            if (!float.IsInfinity(d) && !float.IsNaN(d) && d > furthest) furthest = d;
        }

        float capSize = Mathf.Max(wanderDistance * 0.1f, furthest * 0.005f);

        Gizmos.color = gizmosColor;
        Gizmos.DrawLine(wanderA, wanderB);
        Gizmos.DrawLine(wanderA - Vector3.forward * capSize, wanderA + Vector3.forward * capSize);
        Gizmos.DrawLine(wanderB - Vector3.forward * capSize, wanderB + Vector3.forward * capSize);
        ResolutionGizmos.DrawLabel(wanderB + Vector3.right * capSize,
            $"wander {ResolutionGizmos.FormatLength(wanderDistance)}");

        // One threshold line per entry.
        for (int i = 0; i < entries.Length; i++)
        {
            Entry e = entries[i];
            if (e == null || !e.draw) continue;

            Result r = Evaluate(e);
            if (r.governing <= 0f || float.IsInfinity(r.governing) || float.IsNaN(r.governing)) continue;

            Gizmos.color = ResolveColor(e);

            // The offset line: parallel to the wander line, at the imposter distance along the
            // view axis. Matching its width to the wander line would make it invisible at
            // range, so it is drawn wide enough to read against the connector.
            Vector3 offsetCenter = Vector3.forward * r.governing;
            float halfOffset = Mathf.Max(halfWander, r.governing * 0.08f);
            Vector3 offsetA = offsetCenter + Vector3.left * halfOffset;
            Vector3 offsetB = offsetCenter + Vector3.right * halfOffset;
            Gizmos.DrawLine(offsetA, offsetB);

            // Connector along the view axis, so the line reads as a distance from the object
            // rather than as a free-floating bar.
            Gizmos.DrawLine(Vector3.zero, offsetCenter);

            string limitName = r.weberGoverns ? "Weber" : "acuity";
            string title = string.IsNullOrEmpty(e.name) ? $"Entry {i}" : e.name;
            ResolutionGizmos.DrawLabel(offsetB + Vector3.right * (capSize * 2f),
                $"{title}\nimposter beyond {ResolutionGizmos.FormatLength(r.governing)}\n({limitName}-limited)");
        }
    }

#endif
}

#if UNITY_EDITOR
[CustomEditor(typeof(ImposterThreshold))]
public class ImposterThresholdEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        var tool = (ImposterThreshold)this.target;
        var headsetProp = serializedObject.FindProperty("headset");

        // --- Headset ----------------------------------------------------------------
        EditorGUILayout.LabelField("Headset", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(headsetProp,
            new GUIContent("Preset", "The headset whose display specs set the angular resolution used for the acuity limit."));

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

        // --- Viewer -----------------------------------------------------------------
        EditorGUILayout.LabelField("Viewer", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("wanderDistance"),
            new GUIContent("Wander distance (m)", "How far the viewer can move sideways while observing the object. A stationary viewer still has two eyes, so the floor is interpupillary distance (~0.064 m)."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("wanderOffset"),
            new GUIContent("Wander offset", "Where the object origin sits along the wander span. 0.5 centres it; 0 puts the origin at the -X end, 1 at the +X end. Affects the gizmo only, not the calculated distance."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("weberFraction"),
            new GUIContent("Weber fraction", "Fraction of a feature's spacing by which a shift must change before it is discriminable. 0.05 is the usual assumption."));

        EditorGUILayout.Space(8f);

        // --- Objects ----------------------------------------------------------------
        EditorGUILayout.LabelField("Objects", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("entries"),
            new GUIContent("Entries", "The objects under test. Each has its own depth and detail spacing; the viewer and headset above are shared across all of them."), true);

        serializedObject.ApplyModifiedProperties();

        EditorGUILayout.Space(8f);

        // --- Readout ----------------------------------------------------------------
        EditorGUILayout.LabelField("Readout", EditorStyles.boldLabel);

        var labelStyle = new GUIStyle(EditorStyles.label) { richText = true };
        var entries = tool.GetEntries();

        // Arcminutes read better than radians for acuity; 1 arcmin is the usual human limit.
        float acuityArcmin = tool.GetAngularResolution() * Mathf.Rad2Deg * 60f;
        EditorGUILayout.LabelField($"Display angular resolution:  {acuityArcmin:0.##}′", labelStyle);
        EditorGUILayout.Space(2f);

        if (entries == null || entries.Length == 0)
        {
            EditorGUILayout.HelpBox("Add an entry to see its imposter distance.", MessageType.None);
        }
        else
        {
            // One row per entry, sharing the viewer above. The governing limit is named on
            // each row: which of the two bites is the useful part of the answer, since it
            // says whether adding detail or changing headset would move the number.
            for (int i = 0; i < entries.Length; i++)
            {
                var e = entries[i];
                if (e == null) continue;

                var result = tool.Evaluate(e);
                string title = string.IsNullOrEmpty(e.name) ? $"Entry {i}" : e.name;

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                if (result.governing <= 0f)
                {
                    EditorGUILayout.LabelField($"<b>{title}</b>  —  set a non-zero wander distance and depth", labelStyle);
                }
                else
                {
                    string limitName = result.weberGoverns ? "Weber" : "acuity";
                    EditorGUILayout.LabelField($"<b>{title}</b>", labelStyle);
                    EditorGUILayout.LabelField($"Imposter beyond:  <b>{FormatDistance(result.governing)}</b>   ({limitName}-limited)", labelStyle);
                    EditorGUILayout.LabelField($"Acuity {FormatDistance(result.acuity)}    Weber {FormatDistance(result.weber)}", labelStyle);
                }

                EditorGUILayout.EndVertical();
            }
        }

        EditorGUILayout.Space(8f);

        // --- Display ----------------------------------------------------------------
        EditorGUILayout.LabelField("Display", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("gizmosColor"),
            new GUIContent("Gizmo colour", "Colour of the wander line, and of any entry that leaves its own colour fully transparent."));

        serializedObject.ApplyModifiedProperties();

        // Force repaint whenever the scene view changes, so the gizmo tracks inspector edits.
        if (Event.current.type == EventType.Repaint)
            SceneView.RepaintAll();
    }

    // Distances here span millimetres to kilometres, and an unreachable limit is a real
    // answer worth showing rather than a blank.
    static string FormatDistance(float meters)
    {
        if (float.IsInfinity(meters)) return "unbounded";
        if (float.IsNaN(meters)) return "-";
        if (meters >= 1000f) return $"{meters / 1000f:0.##} km";
        return ResolutionGizmos.FormatLength(meters);
    }
}
#endif
