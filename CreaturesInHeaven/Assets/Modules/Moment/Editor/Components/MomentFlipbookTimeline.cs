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
        bool multiSelect = (e.modifiers & (EventModifiers.Control | EventModifiers.Command)) != 0;

        if (multiSelect)
        {
            // Toggle this cell without touching others.
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
        }

        FireSelectionChanged();
    }

    void ClearSelection()
    {
        foreach (int i in _selectedIndices)
            (ElementAt(i) as MomentFlipbookCell)?.SetSelected(false);
        _selectedIndices.Clear();
    }

    void FireSelectionChanged()
    {
        _selectedIndices.Sort();
        OnSelectionChanged?.Invoke(_selectedIndices);
    }
}
