using System;
using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;

// UITK component for a single selection group item in the fixture map list.
// Supports two modes: view (clickable label + count) and edit (text field + confirm/cancel).
public class SelectionGroupItem : VisualElement
{
    public new class UxmlFactory : UxmlFactory<SelectionGroupItem> { }

    private Label _countLabel;
    private Button _itemButton;
    private TextField _renameField;
    private Button _groupReplaceSelectionBtn, _groupAddSelectionBtn, _groupRemoveSelectionBtn, _confirmRenameBtn, _cancelRenameBtn;
    private VisualElement _viewContainer, _editContainer;

    private EventCallback<KeyDownEvent> _renameKeyHandler;
    public int GroupIndex { get; set; } = -1;

    public SelectionGroupItem()
    {
        // Load component-specific styles
        var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/Editor/SelectionGroupItem.uss");
        if (styleSheet != null)
            styleSheets.Add(styleSheet);

        AddToClassList("sg-item");
        AddToClassList("row");

        // View mode container
        _viewContainer = new VisualElement();
        _viewContainer.AddToClassList("row");
        _viewContainer.AddToClassList("grow");
        _viewContainer.AddToClassList("items-center");

        _itemButton = new Button();
        _itemButton.AddToClassList("sg-item-btn");
        _itemButton.AddToClassList("grow");

        // Group size label
        _countLabel = new Label();
        _countLabel.AddToClassList("sg-item-count");
        _countLabel.AddToClassList("text-sm");
        _countLabel.AddToClassList("text-muted");

        // Selection control
        _groupReplaceSelectionBtn = new Button();
        _groupReplaceSelectionBtn.style.backgroundImage = new StyleBackground(AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Editor/Icons/Select.png"));
        _groupReplaceSelectionBtn.AddToClassList("btn-icon-sm");
        _groupReplaceSelectionBtn.AddToClassList("btn-tertiary");
        _groupReplaceSelectionBtn.tooltip = "Select fixtures in this group";

        _groupAddSelectionBtn = new Button();
        _groupAddSelectionBtn.style.backgroundImage = new StyleBackground(AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Editor/Icons/Select add.png"));
        _groupAddSelectionBtn.AddToClassList("btn-icon-sm");
        _groupAddSelectionBtn.AddToClassList("btn-tertiary");
        _groupAddSelectionBtn.tooltip = "Add this group's fixtures to current selection";

        _groupRemoveSelectionBtn = new Button();
        _groupRemoveSelectionBtn.style.backgroundImage = new StyleBackground(AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Editor/Icons/Select remove.png"));
        _groupRemoveSelectionBtn.AddToClassList("btn-icon-sm");
        _groupRemoveSelectionBtn.AddToClassList("btn-tertiary");
        _groupRemoveSelectionBtn.tooltip = "Subtract this group's fixtures from current selection";

        _viewContainer.Add(_itemButton);
        _viewContainer.Add(_countLabel);
        _viewContainer.Add(_groupReplaceSelectionBtn);
        _viewContainer.Add(_groupAddSelectionBtn);
        _viewContainer.Add(_groupRemoveSelectionBtn);

        // Edit mode container
        _editContainer = new VisualElement();
        _editContainer.AddToClassList("row");
        _editContainer.AddToClassList("grow");
        _editContainer.AddToClassList("items-center");
        _editContainer.style.display = DisplayStyle.None;

        _renameField = new TextField();
        _renameField.AddToClassList("grow");
        _renameField.AddToClassList("sg-rename-field");

        _confirmRenameBtn = new Button();
        _confirmRenameBtn.text = "✓";
        _confirmRenameBtn.AddToClassList("btn-icon-sm");
        _confirmRenameBtn.AddToClassList("btn-tertiary");

        _cancelRenameBtn = new Button();
        _cancelRenameBtn.text = "✕";
        _cancelRenameBtn.AddToClassList("btn-icon-sm");
        _cancelRenameBtn.AddToClassList("btn-tertiary");

        _editContainer.Add(_renameField);
        _editContainer.Add(_confirmRenameBtn);
        _editContainer.Add(_cancelRenameBtn);

        Add(_viewContainer);
        Add(_editContainer);
    }

    // Set up the item in view mode with callbacks for selection, interaction, and group fixture management.
    public void SetViewMode(string name, int fixtureCount, Action onClick, Action onReplaceSelection, Action onAddSelection, Action onRemoveSelection)
    {
        _itemButton.text = name;
        _countLabel.text = fixtureCount.ToString();
        _itemButton.clicked -= onClick;
        _itemButton.clicked += onClick;

        _groupReplaceSelectionBtn.clicked -= onReplaceSelection;
        _groupReplaceSelectionBtn.clicked += onReplaceSelection;

        _groupAddSelectionBtn.clicked -= onAddSelection;
        _groupAddSelectionBtn.clicked += onAddSelection;

        _groupRemoveSelectionBtn.clicked -= onRemoveSelection;
        _groupRemoveSelectionBtn.clicked += onRemoveSelection;

        _viewContainer.style.display = DisplayStyle.Flex;
        _editContainer.style.display = DisplayStyle.None;
    }

    // Set up the item in edit mode with the current name and callbacks.
    public void SetEditMode(string currentName, Action<string> onConfirm, Action onCancel)
    {
        _renameField.value = currentName;

        _confirmRenameBtn.clicked += () => onConfirm(_renameField.value);
        _cancelRenameBtn.clicked += onCancel;

        // Create a handler for keyboard events (allows unsubscribe in future calls)
        _renameKeyHandler = e =>
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                onConfirm(_renameField.value);
                e.StopPropagation();
            }
            else if (e.keyCode == KeyCode.Escape)
            {
                onCancel();
                e.StopPropagation();
            }
        };
        _renameField.RegisterCallback(_renameKeyHandler);

        _viewContainer.style.display = DisplayStyle.None;
        _editContainer.style.display = DisplayStyle.Flex;

        // Focus the field on the next frame
        schedule.Execute(() => _renameField.Focus()).StartingIn(0);
    }

    // Set whether this item appears selected.
    public void SetSelected(bool selected)
    {
        if (selected)
            AddToClassList("sg-selected");
        else
            RemoveFromClassList("sg-selected");
    }

    // Update the background icon based on how many fixtures in the group are selected.
    public void SetSelectionState(int selectedCount, int totalCount)
    {
        // string iconPath;
        if (selectedCount == 0)
        {
            _itemButton.RemoveFromClassList("sg-item-btn--selected");
            _itemButton.RemoveFromClassList("sg-item-btn--partial");
            _itemButton.AddToClassList("sg-item-btn--none");
        }
        else if (selectedCount == totalCount)
        {
            _itemButton.RemoveFromClassList("sg-item-btn--selected");
            _itemButton.RemoveFromClassList("sg-item-btn--none");
            _itemButton.AddToClassList("sg-item-btn--selected");
        }
        else
        {
            _itemButton.RemoveFromClassList("sg-item-btn--selected");
            _itemButton.RemoveFromClassList("sg-item-btn--none");
            _itemButton.AddToClassList("sg-item-btn--partial");
        }
    }
}
