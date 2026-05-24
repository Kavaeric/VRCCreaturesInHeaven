using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

// A wrapping grid of MomentFlipbookCell elements representing each snapshot
// in an ALV flipbook. Call Populate() to rebuild the grid from sidecar data.
public class MomentFlipbookTimeline : VisualElement
{
    public new class UxmlFactory : UxmlFactory<MomentFlipbookTimeline> {}

    static readonly string UssPath =
        $"{MomentAssetPaths.ScriptDir()}/Components/MomentFlipbookTimeline.uss";

    // Fired whenever the selection changes. Passed list is sorted ascending.
    public event Action<IReadOnlyList<int>> OnSelectionChanged;

    // Fired when the hovered cell index changes (-1 = none).
    public event Action<int> OnHoverChanged;

    // Fired when the focused cell index changes (-1 = none).
    public event Action<int> OnFocusChanged;

    readonly List<int> _selectedIndices = new();
    int _anchorIndex = -1; // last non-shift click; range selections extend from here

    public IReadOnlyList<int> SelectedIndices => _selectedIndices;

    // When true, clicks are ignored and selection is cleared. Hover and focus still work normally.
    public bool Locked
    {
        get => _locked;
        set { _locked = value; if (_locked) ClearSelection(); }
    }
    bool _locked;

    // Index of the cell that was most recently clicked (including the range endpoint on shift-click).
    // -1 when nothing is selected. Use this to determine which cell to scrub the animation to.
    public int LastClickedIndex { get; private set; } = -1;

    // Index of the cell currently under the mouse cursor. -1 when none.
    public int HoveredIndex { get; private set; } = -1;

    // Index of the cell that currently holds keyboard focus. -1 when none.
    public int FocusedIndex { get; private set; } = -1;

    public MomentFlipbookTimeline()
    {
        var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
        if (uss != null) styleSheets.Add(uss);

        AddToClassList("flipbook-timeline");

        // Clicking the timeline background (not a cell) clears selection.
        RegisterCallback<ClickEvent>(e =>
        {
            if (!_locked && e.target == this) ClearSelection();
        });

    }

    public struct CellState
    {
        public bool Baked;
        public FlipbookCellOverlay Overlay;
    }

    // Updates cell states without rebuilding the grid (preserves selection).
    // Falls back to Populate if the cell count has changed.
    public void UpdateStates(int count, CellState[] states)
    {
        if (childCount != count) { Populate(count, states); return; }

        for (int i = 0; i < count; i++)
        {
            var state = states != null && i < states.Length ? states[i] : default;
            var cell = ElementAt(i) as MomentFlipbookCell;
            cell?.SetBaked(state.Baked);
            cell?.SetOverlay(state.Overlay);
        }
    }

    // Rebuilds the grid and clears selection. Use only when cell count changes.
    public void Populate(int count, CellState[] states)
    {
        Clear();
        _selectedIndices.Clear();
        _anchorIndex = -1;
        HoveredIndex = -1;
        FocusedIndex = -1;

        for (int i = 0; i < count; i++)
        {
            var state = states != null && i < states.Length ? states[i] : default;
            var cell = new MomentFlipbookCell();
            cell.SetBaked(state.Baked);
            cell.SetOverlay(state.Overlay);

            int index = i; // capture for closure
            cell.RegisterCallback<ClickEvent>(e => OnCellClicked(e, cell, index));
            cell.RegisterCallback<MouseEnterEvent>(_ =>
            {
                HoveredIndex = index;
                OnHoverChanged?.Invoke(HoveredIndex);
            });
            cell.RegisterCallback<MouseLeaveEvent>(_ =>
            {
                if (HoveredIndex == index)
                {
                    HoveredIndex = -1;
                    OnHoverChanged?.Invoke(HoveredIndex);
                }
            });
            cell.RegisterCallback<FocusInEvent>(_ =>
            {
                FocusedIndex = index;
                OnFocusChanged?.Invoke(FocusedIndex);
            });
            cell.RegisterCallback<FocusOutEvent>(_ =>
            {
                if (FocusedIndex == index)
                {
                    FocusedIndex = -1;
                    OnFocusChanged?.Invoke(FocusedIndex);
                }
            });

            Add(cell);
        }
    }

    void OnCellClicked(ClickEvent e, MomentFlipbookCell cell, int index)
    {
        if (_locked) return;

        bool shift = (e.modifiers & EventModifiers.Shift)   != 0;
        bool ctrl  = (e.modifiers & (EventModifiers.Control | EventModifiers.Command)) != 0;

        // Shift-click selects the range between the clicked cell and the previously selected cell.
        if (shift && _anchorIndex >= 0)
        {
            int anchor = _anchorIndex;
            int lo = Mathf.Min(anchor, index);
            int hi = Mathf.Max(anchor, index);

            if (ctrl)
            {
                // Ctrl+Shift: add range to existing selection without clearing it.
                for (int i = lo; i <= hi; i++)
                {
                    if (_selectedIndices.Contains(i)) continue;
                    _selectedIndices.Add(i);
                    (ElementAt(i) as MomentFlipbookCell)?.SetSelected(true);
                }
            }
            else
            {
                // Shift only: replace selection with range from anchor to here.
                // Anchor stays put so subsequent shift-clicks extend from the same origin.
                ClearSelection();
                _anchorIndex = anchor;
                for (int i = lo; i <= hi; i++)
                {
                    _selectedIndices.Add(i);
                    (ElementAt(i) as MomentFlipbookCell)?.SetSelected(true);
                }
            }
            LastClickedIndex = index;
        }
        // Ctrl-click or cmd-click: doesn't clear existing selection and toggles the clicked cell.
        else if (ctrl)
        {
            // Toggle this cell without touching others; update anchor.
            if (_selectedIndices.Contains(index))
            {
                _selectedIndices.Remove(index);
                cell.SetSelected(false);
            }
            else
            {
                _selectedIndices.Add(index);
                cell.SetSelected(true);
            }
            _anchorIndex = index;
            LastClickedIndex = index;
        }
        else
        {
            // Single click: clear all, then select the clicked cell.
            ClearSelection();
            _selectedIndices.Add(index);
            cell.SetSelected(true);

            _anchorIndex = index;
            LastClickedIndex = index;
        }

        UpdateSelection();
    }

    // Moves keyboard focus to the cell at index without changing the selection.
    // Pass -1 to remove focus from all cells.
    public void FocusCell(int index)
    {
        for (int i = 0; i < childCount; i++)
        {
            var cell = ElementAt(i) as MomentFlipbookCell;
            if (cell == null) continue;
            if (i == index) cell.Focus();
        }
    }

    void ClearSelection()
    {
        foreach (int i in _selectedIndices)
            (ElementAt(i) as MomentFlipbookCell)?.SetSelected(false);
        _selectedIndices.Clear();
        _anchorIndex     = -1;
        LastClickedIndex = -1;
    }

    void UpdateSelection()
    {
        _selectedIndices.Sort();
        OnSelectionChanged?.Invoke(_selectedIndices);
    }
}
