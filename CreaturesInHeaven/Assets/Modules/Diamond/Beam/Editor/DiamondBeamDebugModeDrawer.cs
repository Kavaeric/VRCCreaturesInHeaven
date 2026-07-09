using UnityEngine;
using UnityEditor;

// Material property drawer for DiamondBeam's _DebugMode float. Unity's built-in
// [Enum] drawer only supports enums backed by a type with <= 7 named values, and
// _DebugMode has more than that, so it stays a plain Float property in the
// Properties block. This drawer gives it a named dropdown plus a help box
// describing the selected mode, so the inspector doesn't just show a bare number.
//
// Usage in a shader's Properties block:
//   [DiamondBeamDebugMode] _DebugMode ("Debug Mode", Float) = 0
//
// Keep Names/Descriptions in sync with the debug dispatch chain in the shader's
// frag function (see DiamondBeamRound.shader / DiamondBeam.shader).
public class DiamondBeamDebugModeDrawer : MaterialPropertyDrawer
{
    static readonly string[] Names =
    {
        "Normal",
        "Raymarch Depth",
        "Geometric Falloff",
        "Haze Extinction",
        "Far Cap Fade",
        "D-Axis Integral",
        "Lateral U",
        "Lateral Edge",
        "Vertex Bounds",
        "HG Phase",
    };

    static readonly string[] Descriptions =
    {
        "Final composited beam colour (normal render).",
        "Camera-ray segment length inside the beam volume, as grayscale.",
        "Geometric inverse-square falloff term (0..1), sampled at the ray's entry point.",
        "Haze/Beer-Lambert extinction term (0..1), sampled at the ray's entry point.",
        "Far-cap fade term (0..1): fades the beam out over the last _FarFade fraction of its length.",
        "Combined falloff x extinction x fade, numerically integrated along the ray's chord.",
        "Normalized lateral coordinate at the surface hit: 0 on axis, 1 at the cone wall, up to 2 under full defocus.",
        "Lateral edge profile at the surface hit: one soft edge whose blur combines focus and haze scatter as metric spills added to the wall (straight envelope). 1 = lit core, 0 = past the edge.",
        "Vertex-displacement bounds: faint red over every fragment of the expanded bounding cube, before any beam discard. Shows the full box (incl. empty margin) to check it isn't clipping or oversized.",
        "Henyey-Greenstein phase p(theta) at the surface hit (raw, grayscale). View-dependent scatter toward the eye: brighter looking toward the emitter (forward scatter), dimmer across/behind. Sweep _Anisotropy (g).",
    };

    static readonly int[] Values = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };

    public override float GetPropertyHeight(MaterialProperty prop, string label, MaterialEditor editor)
    {
        float baseHeight = base.GetPropertyHeight(prop, label, editor);
        string description = Descriptions[ClampedIndex(prop)];
        float helpHeight = EditorStyles.helpBox.CalcHeight(
            new GUIContent(description), EditorGUIUtility.currentViewWidth - 19f);
        return baseHeight + EditorGUIUtility.standardVerticalSpacing + helpHeight;
    }

    public override void OnGUI(Rect position, MaterialProperty prop, string label, MaterialEditor editor)
    {
        float lineHeight = EditorGUIUtility.singleLineHeight;
        Rect popupRect = new Rect(position.x, position.y, position.width, lineHeight);
        Rect helpRect = new Rect(position.x,
            position.y + lineHeight + EditorGUIUtility.standardVerticalSpacing,
            position.width,
            position.height - lineHeight - EditorGUIUtility.standardVerticalSpacing);

        int index = ClampedIndex(prop);

        EditorGUI.BeginChangeCheck();
        int newIndex = EditorGUI.IntPopup(popupRect, label, index, Names, Values);
        if (EditorGUI.EndChangeCheck())
            prop.floatValue = newIndex;

        EditorGUI.HelpBox(helpRect, Descriptions[index], MessageType.Info);
    }

    static int ClampedIndex(MaterialProperty prop)
    {
        return Mathf.Clamp(Mathf.RoundToInt(prop.floatValue), 0, Names.Length - 1);
    }
}
