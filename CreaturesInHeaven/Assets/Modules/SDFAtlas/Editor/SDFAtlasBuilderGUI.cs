using UnityEngine;
using UnityEditor;

// Layout controls shared by the SDF and MSDF atlas builder windows.
//
// The two windows are deliberately separate (different source types, different encoding
// settings, non-interchangeable outputs), but cell geometry means exactly the same thing in
// both. Keeping these here means the permitted sizes and the aspect advice cannot drift
// apart between the two.
public static class SDFAtlasBuilderGUI
{
    // Powers of two only. Non-power-of-two cells would still pack and address correctly --
    // the arithmetic never assumes otherwise -- but they push the atlas texture itself
    // off powers of two, which costs mip support on some hardware.
    static readonly int[] CellSizes = { 16, 32, 64, 128, 256, 512, 1024, 2048 };

    static readonly GUIContent[] CellSizeLabels =
    {
        new GUIContent("16"), new GUIContent("32"), new GUIContent("64"),
        new GUIContent("128"), new GUIContent("256"), new GUIContent("512"),
        new GUIContent("1024"), new GUIContent("2048"),
    };

    // Draws the cell width/height pair, plus a note on what the chosen aspect implies.
    public static void CellSizeFields(ref int cellWidth, ref int cellHeight)
    {
        cellWidth = EditorGUILayout.IntPopup(
            new GUIContent("Cell width", "Cell width in texels, including padding."),
            cellWidth, CellSizeLabels, CellSizes);

        cellHeight = EditorGUILayout.IntPopup(
            new GUIContent("Cell height", "Cell height in texels, including padding."),
            cellHeight, CellSizeLabels, CellSizes);

        if (cellWidth == cellHeight) return;

        // Artwork is framed preserving aspect, so a non-square cell only pays off when the
        // graphics going into it share that aspect. Say so, since the failure mode is silent:
        // a square graphic in a 4:1 cell still renders correctly, just letterboxed into the
        // middle quarter with three quarters of the cell's texels spent on empty margin.
        EditorGUILayout.HelpBox(
            $"Non-square cells ({AspectLabel(cellWidth, cellHeight)}). Artwork is framed " +
            "preserving aspect, so graphics matching this ratio fill the cell and anything " +
            "else is letterboxed inside it. Worth it when the graphics are consistently " +
            "this shape; wasteful otherwise.",
            MessageType.Info);
    }

    // Reduced aspect ratio, e.g. "2:1" rather than "128:64".
    static string AspectLabel(int width, int height)
    {
        int divisor = GreatestCommonDivisor(width, height);
        return $"{width / divisor}:{height / divisor}";
    }

    static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }

    // Deepest mip level whose texels still average only within a cell.
    //
    // Mirrors SDFAtlasInfo.SafeMipLevel, which cannot be used directly here because the
    // builder reports this while the settings are still being edited, before any manifest
    // exists to ask.
    public static int SafeMipLevel(int padding)
    {
        int level = 0;
        while ((1 << (level + 1)) <= Mathf.Max(padding, 1)) level++;
        return level;
    }
}
