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

    readonly List<int> _selectedIndices = new();
    int _anchorIndex = -1; // last non-shift click; range selections extend from here

    public IReadOnlyList<int> SelectedIndices => _selectedIndices;

    public MomentFlipbookTimeline()
    {
        var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
        if (uss != null) styleSheets.Add(uss);

        AddToClassList("flipbook-timeline");

        // Clicking the timeline background (not a cell) clears selection.
        RegisterCallback<ClickEvent>(e =>
        {
            if (e.target == this) ClearSelection();
        });
    }

    // Updates cell states without rebuilding the grid (preserves selection).
    // Falls back to Populate if the cell count has changed.
    public void UpdateStates(int count, FlipbookCellState[] states)
    {
        if (childCount != count) { Populate(count, states); return; }

        for (int i = 0; i < count; i++)
        {
            var state = states != null && i < states.Length ? states[i] : FlipbookCellState.Unbaked;
            (ElementAt(i) as MomentFlipbookCell)?.SetState(state);
        }
    }

    // Rebuilds the grid and clears selection. Use only when cell count changes.
    public void Populate(int count, FlipbookCellState[] states)
    {
        Clear();
        _selectedIndices.Clear();
        _anchorIndex = -1;

        for (int i = 0; i < count; i++)
        {
            var state = states != null && i < states.Length ? states[i] : FlipbookCellState.Unbaked;
            var cell = new MomentFlipbookCell();
            cell.SetState(state);

                int index = i; // capture for closure
            cell.RegisterCallback<ClickEvent>(e => OnCellClicked(e, cell, index));

            Add(cell);
        }
    }

    void OnCellClicked(ClickEvent e, MomentFlipbookCell cell, int index)
    {
        bool shift = (e.modifiers & EventModifiers.Shift)   != 0;
        bool ctrl  = (e.modifiers & (EventModifiers.Control | EventModifiers.Command)) != 0;

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
        }
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
        }
        else
        {
            // Single click: clear all, select only this one (or deselect if already sole selection).
            bool wasOnlySelection = _selectedIndices.Count == 1 && _selectedIndices.Contains(index);
            ClearSelection();
            if (!wasOnlySelection)
            {
                _selectedIndices.Add(index);
                cell.SetSelected(true);
            }
            _anchorIndex = wasOnlySelection ? -1 : index;
        }

        FireSelectionChanged();
    }

    void ClearSelection()
    {
        foreach (int i in _selectedIndices)
            (ElementAt(i) as MomentFlipbookCell)?.SetSelected(false);
        _selectedIndices.Clear();
        _anchorIndex = -1;
    }

    void FireSelectionChanged()
    {
        _selectedIndices.Sort();
        OnSelectionChanged?.Invoke(_selectedIndices);
    }
}
