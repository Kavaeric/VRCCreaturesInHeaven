using UnityEditor;
using UnityEngine.UIElements;

// Transient overlay state. layered on top of the baked flag.
public enum FlipbookCellOverlay { None, Queued, Active }

// A single snapshot cell in the flipbook timeline.
// The Button is a zero-margin hit target; all visual styling lives on the inner element.
// Selection is managed by MomentFlipbookTimeline.
public class MomentFlipbookCell : Button
{
    public new class UxmlFactory : UxmlFactory<MomentFlipbookCell> {}

    static readonly string UssPath =
        $"{MomentAssetPaths.ScriptDir()}/Components/MomentFlipbookCell.uss";

    readonly VisualElement _inner;
    readonly Label _label;
    bool _baked = false;
    FlipbookCellOverlay _overlay = FlipbookCellOverlay.None;

    public MomentFlipbookCell()
    {
        var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
        if (uss != null) styleSheets.Add(uss);

        AddToClassList("flipbook-cell");

        _inner = new VisualElement();
        _inner.AddToClassList("flipbook-cell__inner");
        Add(_inner);

        _label = new Label("○");
        _label.AddToClassList("flipbook-cell__label");
        _inner.Add(_label);
    }

    public void SetBaked(bool baked)
    {
        if (baked == _baked) return;
        _baked = baked;
        _inner.EnableInClassList("flipbook-cell__inner--baked", baked);
        _label.EnableInClassList("flipbook-cell__label--baked", baked);
    }

    public void SetOverlay(FlipbookCellOverlay overlay)
    {
        if (overlay == _overlay) return;
        _inner.EnableInClassList("flipbook-cell__inner--queued", overlay == FlipbookCellOverlay.Queued);
        _inner.EnableInClassList("flipbook-cell__inner--active", overlay == FlipbookCellOverlay.Active);
        _label.EnableInClassList("flipbook-cell__label--queued", overlay == FlipbookCellOverlay.Queued);
        _label.EnableInClassList("flipbook-cell__label--active", overlay == FlipbookCellOverlay.Active);

        _overlay = overlay;

        // Change the indicator depending on if the snapshot is unqueued,
        // queued, or currently baking.
        _label.text = overlay switch
        {
            FlipbookCellOverlay.None => "○",
            FlipbookCellOverlay.Queued => "●",
            FlipbookCellOverlay.Active => "…",
            _ => "⨯",
        };
    }

    public void SetSelected(bool selected)
    {
        _inner.EnableInClassList("flipbook-cell__inner--selected", selected);
        _label.EnableInClassList("flipbook-cell__label--selected", selected);
    }
}
