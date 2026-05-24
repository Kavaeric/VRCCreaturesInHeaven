using UnityEditor;
using UnityEngine.UIElements;

// State of a single snapshot cell in the timeline.
public enum FlipbookCellState { Unbaked, Baked, Queued, Active }

// A single snapshot cell in the flipbook timeline.
// The Button is a zero-margin hit target; all visual styling lives on the inner element.
// Selection is managed by MomentFlipbookTimeline.
public class MomentFlipbookCell : Button
{
    public new class UxmlFactory : UxmlFactory<MomentFlipbookCell> {}

    static readonly string UssPath =
        $"{MomentAssetPaths.ScriptDir()}/Components/MomentFlipbookCell.uss";

    static readonly string[] StateClasses =
    {
        "",                           // Unbaked — inner base class only
        "flipbook-cell__inner--baked",
        "flipbook-cell__inner--queued",
        "flipbook-cell__inner--active",
    };

    readonly VisualElement _inner;
    FlipbookCellState _state = FlipbookCellState.Unbaked;

    public MomentFlipbookCell()
    {
        var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
        if (uss != null) styleSheets.Add(uss);

        AddToClassList("flipbook-cell");

        _inner = new VisualElement();
        _inner.AddToClassList("flipbook-cell__inner");
        Add(_inner);
    }

    public void SetState(FlipbookCellState state)
    {
        if (state == _state) return;

        string oldClass = StateClasses[(int)_state];
        if (oldClass != "") _inner.RemoveFromClassList(oldClass);

        string newClass = StateClasses[(int)state];
        if (newClass != "") _inner.AddToClassList(newClass);

        _state = state;
    }

    public void SetSelected(bool selected) =>
        _inner.EnableInClassList("flipbook-cell__inner--selected", selected);
}
