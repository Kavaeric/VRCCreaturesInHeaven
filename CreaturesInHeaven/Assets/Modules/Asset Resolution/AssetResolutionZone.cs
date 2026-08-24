
using UnityEngine;
using UnityEditor;

// AssetResolutionZone
// Editor-only script that calculates texel density falloff from a given position and headset spec.
// Defines a zone that the player is expected to move around in and displays texel density falloff
// radiating from the perimeter of the zone.
public class AssetResolutionZone : MonoBehaviour
{
#if UNITY_EDITOR

    // Shared with AssetResolutionCheck: HeadsetPreset/HeadsetSpec live in HeadsetSpecs.cs,
    // the density math in TexelDensity.cs, and the gizmo helpers in ResolutionGizmos.cs.

    [SerializeField] private HeadsetPreset headset = HeadsetPreset.ValveIndex;

    [SerializeField] private int customResX = 1440;
    [SerializeField] private int customResY = 1600;
    [SerializeField] private float customFovH = 108f;
    [SerializeField] private float customFovV = 104f;

    // Size (m) of the walkable zone. X and Z are lateral; Y is the vertical extent the viewer
    // can occupy, measured up from the object, so the box spans 0 to zoneSize.y in local space.
    [SerializeField] private Vector3 zoneSize = new Vector3(4f, 2f, 2f);

    // Density thresholds (px/m). One ring drawn per entry.
    [SerializeField] private int[] densityThresholds = { 512, 256, 128, 64 };

    // The threshold drawn in white, as the one being designed against. Other rings fade
    // relative to it. Unlike AssetResolutionCheck there is no radius to pin it to: the zone
    // perimeter is the anchor, so a density's ring always sits its own falloff distance out.
    [SerializeField] private int referenceDensity = 512;

    // Optional second line on each ring label: the smallest triangle edge worth authoring at
    // that ring's density, as a rule of thumb against quad overdraw. See
    // TexelDensity.DensityToMinTriangleEdge for what the number means.
    [SerializeField] private bool showMinTriangleEdge = false;

    // Pixel count the triangle-edge rule targets. Six is the default: comfortably above the
    // 2x2 quad the GPU rasterises in, without being so strict it rules out ordinary geometry.
    [SerializeField] private float triangleEdgePixels = 6f;

    // When true, the zone inherits the object's rotation. Otherwise it stays axis-aligned.
    [SerializeField] private bool orientToTransform = true;

    // Slice positions (m, relative to the object) for each of the three planes. A slice is the
    // zone's cross-section and its rings drawn flat on that plane, so an XZ slice at 0 is the
    // floor and one at 1 is a metre up. Stacking several is a way to read the falloff at
    // several places at once without a wire box cluttering the view.
    //
    // Each list is positioned along the axis its plane does not span: XZ slices along Y, XY
    // slices along Z, YZ slices along X. A slice beyond the zone's extent on that axis has its
    // rings shrunk, because part of the viewing distance is spent covering the gap. See DrawSlice.
    [SerializeField] private float[] sliceHeightsXZ = { 0f };
    [SerializeField] private float[] slicePositionsXY = { };
    [SerializeField] private float[] slicePositionsYZ = { };

    // Per-plane show/hide, so one set of slices can be read without the others in the way.
    [SerializeField] private bool showSlicesXZ = true;
    [SerializeField] private bool showSlicesXY = true;
    [SerializeField] private bool showSlicesYZ = true;

    // Line segments per quarter-arc on the rounded corners.
    [SerializeField] private int cornerSegments = 4;

    [SerializeField] private Color zoneColor = Color.white;

    // Ring colour per plane, so slices on different planes stay tellable apart where they cross.
    [SerializeField] private Color ringColorXZ = Color.magenta;
    [SerializeField] private Color ringColorXY = Color.yellow;
    [SerializeField] private Color ringColorYZ = Color.cyan;

    // Where each plane's labels sit, as a fraction of the way around a ring's perimeter.
    // Measured by arc length from the corner nearest +A/+B, so 0.125 is the middle of the
    // first side on a square zone. Per-plane so labels on crossing planes can be moved apart.
    [SerializeField, Range(0f, 1f)] private float labelPositionXZ = 0.125f;
    [SerializeField, Range(0f, 1f)] private float labelPositionXY = 0.125f;
    [SerializeField, Range(0f, 1f)] private float labelPositionYZ = 0.125f;

    // Alpha multiplier on the zone colour for the volume box, so it sits behind the slices
    // rather than competing with them.
    [SerializeField, Range(0f, 1f)] private float zoneBoxOpacity = 0.25f;

    HeadsetSpec GetSpec()
    {
        return HeadsetSpecs.Get(headset, customResX, customResY, customFovH, customFovV);
    }

    void OnDrawGizmos()
    {
        HeadsetSpec hs = GetSpec();

        // Zone's floor centre is the origin and a slice height is just a local Y.
        Quaternion rot = orientToTransform ? transform.rotation : Quaternion.identity;
        Gizmos.matrix = Matrix4x4.TRS(transform.position, rot, Vector3.one);

        // The zone volume itself.
        Gizmos.color = new Color(zoneColor.r, zoneColor.g, zoneColor.b, zoneColor.a * zoneBoxOpacity);
        ResolutionGizmos.DrawFloorBox(Vector3.zero, zoneSize);

        float midHeight = zoneSize.y * 0.5f;

        if (showSlicesXZ && sliceHeightsXZ != null)
        {
            foreach (float pos in sliceHeightsXZ)
                DrawSlice(pos, hs, Vector3.right, Vector3.forward, Vector3.up,
                          zoneSize.x * 0.5f, zoneSize.z * 0.5f, 0f, 0f, zoneSize.y, ringColorXZ, labelPositionXZ);
        }

        if (showSlicesXY && slicePositionsXY != null)
        {
            foreach (float pos in slicePositionsXY)
                DrawSlice(pos, hs, Vector3.right, Vector3.up, Vector3.forward,
                          zoneSize.x * 0.5f, midHeight, midHeight, -zoneSize.z * 0.5f, zoneSize.z * 0.5f, ringColorXY, labelPositionXY);
        }

        if (showSlicesYZ && slicePositionsYZ != null)
        {
            foreach (float pos in slicePositionsYZ)
                DrawSlice(pos, hs, Vector3.forward, Vector3.up, Vector3.right,
                          zoneSize.z * 0.5f, midHeight, midHeight, -zoneSize.x * 0.5f, zoneSize.x * 0.5f, ringColorYZ, labelPositionYZ);
        }

        Gizmos.matrix = Matrix4x4.identity;
    }

    // Draws one slice: the zone's cross-section plus a ring per density threshold, all flat on
    // the plane spanned by axisA and axisB, positioned at pos along normalAxis.
    //
    // halfA and halfB are the zone's half-extents along axisA and axisB, and offsetB is where
    // the zone's centre sits along axisB. spanMin and spanMax are the zone's extent along
    // normalAxis, which the slice position is measured against.
    //
    // The offset and the span are both there for the same reason: the box is floor-anchored,
    // so it runs 0 to height on Y but is symmetric on X and Z. A plane with Y in it is centred
    // at mid-height rather than on the floor, and a plane positioned along Y measures its gap
    // against 0..height. Passed in rather than assumed, so this stays plane-agnostic.
    //
    // Expects Gizmos.matrix to already be set to the zone's space.
    void DrawSlice(float pos, HeadsetSpec hs, Vector3 axisA, Vector3 axisB, Vector3 normalAxis,
                   float halfA, float halfB, float offsetB, float spanMin, float spanMax, Color ringColor,
                   float labelPosition)
    {
        Vector3 center = normalAxis * pos + axisB * offsetB;
        Vector2 halfExtents = new Vector2(halfA, halfB);

        // Distance from this slice to the nearest point of the zone box along the slice's
        // normal. Zero while the slice cuts through the box.
        float gap = 0f;
        if (pos < spanMin) gap = spanMin - pos;
        else if (pos > spanMax) gap = pos - spanMax;

        // The zone's cross-section, drawn at every slice position whether or not the viewer can
        // stand there. Doubles as the ring for any threshold whose falloff is zero.
        Gizmos.color = zoneColor;
        ResolutionGizmos.DrawRoundedRect(center, halfExtents, 0f, cornerSegments, axisA, axisB);

        // Split the thresholds either side of the reference so each side fades independently,
        // same as AssetResolutionCheck. "Inside" here means a higher density, whose ring sits
        // closer to the zone.
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

            // Distance from the zone at which density falls to this threshold. Every point on
            // the ring is exactly this far from the zone, measured in 3D.
            float dist = TexelDensity.DensityToDistance(density, hs);
            if (float.IsInfinity(dist)) continue;

            // Spend the gap out of that budget first: the ring on this slice is where a sphere
            // of radius dist around the zone edge cuts the slice plane, so the in-plane radius
            // left over is sqrt(dist^2 - gap^2). Once the gap uses up the whole budget the
            // threshold is unreachable from this slice and the ring disappears.
            if (gap >= dist) continue;
            float offset = Mathf.Sqrt(dist * dist - gap * gap);

            if (density == referenceDensity)
            {
                Gizmos.color = zoneColor;
            }
            else if (density > referenceDensity)
            {
                Gizmos.color = ResolutionGizmos.FadeAlpha(ringColor, insideIdx, ringsInside, 1f, 0.2f);
                insideIdx++;
            }
            else
            {
                Gizmos.color = ResolutionGizmos.FadeAlpha(ringColor, outsideIdx, ringsOutside, 0.8f, 0.15f);
                outsideIdx++;
            }

            ResolutionGizmos.DrawRoundedRect(center, halfExtents, offset, cornerSegments, axisA, axisB);
            Vector3 labelPos = ResolutionGizmos.PointOnRoundedRect(center, halfExtents, offset, labelPosition, axisA, axisB);

            // The triangle-edge figure follows from the density alone, so it is the same on
            // every slice and every ring of this threshold, unlike the offset above it.
            string label = $"{density:0} px/m\n{offset:0.##}m";
            if (showMinTriangleEdge)
            {
                float minEdge = TexelDensity.DensityToMinTriangleEdge(density, triangleEdgePixels);
                label += $"\n>{ResolutionGizmos.FormatLength(minEdge)} tris";
            }
            ResolutionGizmos.DrawLabel(labelPos, label);
        }
    }

#endif
}

#if UNITY_EDITOR
[CustomEditor(typeof(AssetResolutionZone))]
public class AssetResolutionZoneEditor : Editor
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

        // --- Zone -------------------------------------------------------------------
        EditorGUILayout.LabelField("Zone", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("zoneSize"),
            new GUIContent("Zone size", "Size (m) of the space the player can move around in. X and Z are lateral; Y is the vertical extent, measured up from this object, so the box sits on the object's floor. Rings are measured outward from the box, so the worst case is a viewer at the nearest point of it."));

        EditorGUILayout.Space(8f);

        // --- Slices -----------------------------------------------------------------
        EditorGUILayout.LabelField("Slices", EditorStyles.boldLabel);

        DrawSlicePlane("XZ (horizontal)", "showSlicesXZ", "sliceHeightsXZ", "ringColorXZ", "labelPositionXZ",
            "Positions are heights (m) above this object, so 0 is the floor.");
        DrawSlicePlane("XY (facing Z)", "showSlicesXY", "slicePositionsXY", "ringColorXY", "labelPositionXY",
            "Positions are offsets (m) along Z from the zone's centre.");
        DrawSlicePlane("YZ (facing X)", "showSlicesYZ", "slicePositionsYZ", "ringColorYZ", "labelPositionYZ",
            "Positions are offsets (m) along X from the zone's centre.");

        EditorGUILayout.Space(8f);

        // --- Density rings ----------------------------------------------------------
        EditorGUILayout.LabelField("Density rings", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("referenceDensity"),
            new GUIContent("Reference density", "The texel density (px/m) being designed against. Drawn in the zone colour; other rings fade relative to it."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("densityThresholds"),
            new GUIContent("Thresholds", "List of texel densities (px/m) to draw rings for. The entry matching the reference density is drawn in the zone colour; others fade with distance from it."));

        var showEdge = serializedObject.FindProperty("showMinTriangleEdge");
        EditorGUILayout.PropertyField(showEdge,
            new GUIContent("Min triangle edge", "Add a second readout to each ring label: the smallest triangle edge worth authoring at that ring's density. A rule of thumb against quad overdraw, since the GPU rasterises in 2x2 pixel quads and a triangle smaller than that still costs a full quad to shade."));

        EditorGUI.indentLevel++;
        EditorGUI.BeginDisabledGroup(!showEdge.boolValue);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("triangleEdgePixels"),
            new GUIContent("Pixel floor", "How many pixels across a triangle should stay at minimum. The readout is this many pixels converted to a world-space length at each ring's density. Six is a reasonable default: above the 2x2 rasterisation quad, without being strict enough to rule out ordinary geometry."));
        EditorGUI.EndDisabledGroup();
        EditorGUI.indentLevel--;

        EditorGUILayout.Space(8f);

        // --- Display ----------------------------------------------------------------
        EditorGUILayout.LabelField("Display", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("zoneColor"),
            new GUIContent("Zone colour", "Colour of the zone box, the per-slice zone rectangle, and the reference density ring."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("zoneBoxOpacity"),
            new GUIContent("Zone box opacity", "Alpha multiplier on the zone colour for the volume box. Lower values keep it as background context behind the slices."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("orientToTransform"),
            new GUIContent("Orient to transform", "When enabled, the zone inherits the object's rotation. When disabled, it stays axis-aligned."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("cornerSegments"),
            new GUIContent("Corner segments", "Number of line segments per quarter-arc on the rounded corners. Higher values are smoother but slower to render."));

        serializedObject.ApplyModifiedProperties();
    }

    // Draws the inspector block for one slice plane.
    void DrawSlicePlane(string label, string showProp, string positionsProp, string colorProp, string labelPosProp, string positionHelp)
    {
        var show = serializedObject.FindProperty(showProp);
        var positions = serializedObject.FindProperty(positionsProp);

        EditorGUILayout.PropertyField(show, new GUIContent(label, $"Draw the {label} slices. Turn off to hide them without losing the positions you have set up."));

        EditorGUI.indentLevel++;
        EditorGUI.BeginDisabledGroup(!show.boolValue);

        EditorGUILayout.PropertyField(positions,
            new GUIContent("Positions", $"{positionHelp} A slice beyond the zone's extent on that axis has smaller rings, because part of the viewing distance is spent covering the gap; a ring vanishes entirely once the gap is longer than that threshold's falloff distance."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty(colorProp),
            new GUIContent("Ring colour", "Base colour for this plane's non-reference rings. Alpha is scaled per ring."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty(labelPosProp),
            new GUIContent("Label position", "Where this plane's labels sit, as a fraction of the way around each ring. Measured by distance travelled, so it moves at a steady pace; 0 is a corner and 0.125 is the middle of the first side on a square zone. Move the planes' labels apart when they overlap."));

        EditorGUI.EndDisabledGroup();
        EditorGUI.indentLevel--;

        EditorGUILayout.Space(4f);
    }
}
#endif
