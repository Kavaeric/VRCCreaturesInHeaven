using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

// Editor window for editing the currently selected fixture's animation properties.
// Displays and edits FixtureDefinition's keyframeable properties without requiring
// multi-select of parent and children.
//
// Open via: Tools > Fixture Properties
public class EditorFixtureProperties : EditorWindow
{
    private FixtureDefinition _selectedDef;
    private FixtureDriver _selectedDriver;
    private FixtureProfile _selectedProfile;

    // UI elements
    private VisualElement _noSelectionHelp;
    private VisualElement _errorHelp;
    private Label _errorLabel;
    private VisualElement _panelInfo;
    private VisualElement _panelColour;
    private VisualElement _panelIllumination;
    private VisualElement _panelTransform;
    private Label _fixtureName;
    private Toggle _onToggle;
    private Label _colourPlaceholder;
    private EnumField _colourModeField;
    private VisualElement _colourTemperatureControl;
    private FloatField _colourTemperatureField;
    private ColorField _colourPreviewField;
    private VisualElement _colourRGBControl;
    private ColorField _emissionColourField;
    private FloatField _brightnessFloatField;
    private Slider _brightnessSlider;
    private Label _spreadPlaceholder;
    private FloatField _spreadFloatField;
    private Slider _spreadSlider;
    private Label _rotationPlaceholder;
    private VisualElement _axisXControl;
    private FloatField _axisXFloatField;
    private Slider _axisXSlider;
    private VisualElement _axisYControl;
    private FloatField _axisYFloatField;
    private Slider _axisYSlider;
    private VisualElement _axisZControl;
    private FloatField _axisZFloatField;
    private Slider _axisZSlider;

    [MenuItem("Tools/Fixture Properties")]
    private static void Open() => GetWindow<EditorFixtureProperties>("Fixture Properties");

    private void OnEnable()
    {
        Selection.selectionChanged += OnSelectionChanged;
        EditorApplication.update += OnEditorUpdate;
    }

    private void OnDisable()
    {
        Selection.selectionChanged -= OnSelectionChanged;
        EditorApplication.update -= OnEditorUpdate;
    }

    public void CreateGUI()
    {
        var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Editor/EditorFixtureProperties.uxml");
        uxml.CloneTree(rootVisualElement);

        _noSelectionHelp = rootVisualElement.Q<VisualElement>("no-selection-help");
        _errorHelp = rootVisualElement.Q<VisualElement>("error-help");
        _errorLabel = rootVisualElement.Q<Label>("error-label");
        _panelInfo = rootVisualElement.Q<VisualElement>("panel-info");
        _panelColour = rootVisualElement.Q<VisualElement>("panel-colour");
        _panelIllumination = rootVisualElement.Q<VisualElement>("panel-illumination");
        _panelTransform = rootVisualElement.Q<VisualElement>("panel-transform");

        _fixtureName = rootVisualElement.Q<Label>("fixture-name");
        _onToggle = rootVisualElement.Q<Toggle>("on-toggle");
        _colourPlaceholder = rootVisualElement.Q<Label>("colour-placeholder");
        _colourModeField = rootVisualElement.Q<EnumField>("colour-mode-field");
        _colourModeField.Init(FixtureDefinition.ColourMode.Blackbody);
        _colourTemperatureControl = rootVisualElement.Q<VisualElement>("colour-temperature-control");
        _colourTemperatureField = rootVisualElement.Q<FloatField>("colour-temperature-field");
        _colourPreviewField = rootVisualElement.Q<ColorField>("colour-preview-field");
        _colourRGBControl = rootVisualElement.Q<VisualElement>("colour-rgb-control");
        _emissionColourField = rootVisualElement.Q<ColorField>("emission-colour-field");
        _brightnessFloatField = rootVisualElement.Q<FloatField>("brightness-float-field");
        _brightnessSlider = rootVisualElement.Q<Slider>("brightness-slider");
        _spreadPlaceholder = rootVisualElement.Q<Label>("spread-placeholder");
        _spreadFloatField = rootVisualElement.Q<FloatField>("spread-float-field");
        _spreadSlider = rootVisualElement.Q<Slider>("spread-slider");
        _rotationPlaceholder = rootVisualElement.Q<Label>("rotation-placeholder");
        _axisXControl = rootVisualElement.Q<VisualElement>("axis-x-control");
        _axisXFloatField = rootVisualElement.Q<FloatField>("axis-x-float-field");
        _axisXSlider = rootVisualElement.Q<Slider>("axis-x-slider");
        _axisYControl = rootVisualElement.Q<VisualElement>("axis-y-control");
        _axisYFloatField = rootVisualElement.Q<FloatField>("axis-y-float-field");
        _axisYSlider = rootVisualElement.Q<Slider>("axis-y-slider");
        _axisZControl = rootVisualElement.Q<VisualElement>("axis-z-control");
        _axisZFloatField = rootVisualElement.Q<FloatField>("axis-z-float-field");
        _axisZSlider = rootVisualElement.Q<Slider>("axis-z-slider");

        WireUpCallbacks();
        RefreshSelection();
    }

    private void WireUpCallbacks()
    {
        _onToggle.RegisterValueChangedCallback(e =>
        {
            Undo.RecordObject(_selectedDriver.PropsTransform.gameObject, "Fixture On/Off");
            _selectedDriver.PropsTransform.gameObject.SetActive(e.newValue);
        });

        _colourModeField.RegisterValueChangedCallback(e =>
        {
            Undo.RecordObject(_selectedDef, "Fixture Colour Mode");
            _selectedDef.Colour = (FixtureDefinition.ColourMode)e.newValue;
            RefreshColourUI();
        });

        _colourTemperatureField.RegisterValueChangedCallback(e =>
        {
            Undo.RecordObject(_selectedDef, "Fixture Colour Temperature");
            _selectedDef.ColourTemperature = e.newValue;
            RefreshColourUI();
        });

        _emissionColourField.RegisterCallback<ChangeEvent<Color>>(e =>
        {
            Undo.RecordObject(_selectedDef, "Fixture Emission Colour");
            _selectedDef.EmissionColor = e.newValue;
        });

        _brightnessFloatField.RegisterValueChangedCallback(e =>
        {
            _brightnessSlider.value = e.newValue;
            float newLinear = Mathf.GammaToLinearSpace(e.newValue);
            Undo.RecordObject(_selectedDriver.PropsTransform, "Fixture Brightness");
            var scale = _selectedDriver.PropsTransform.localScale;
            scale.x = newLinear;
            _selectedDriver.PropsTransform.localScale = scale;
        });

        _brightnessSlider.RegisterValueChangedCallback(e =>
        {
            _brightnessFloatField.value = e.newValue;
            float newLinear = Mathf.GammaToLinearSpace(e.newValue);
            Undo.RecordObject(_selectedDriver.PropsTransform, "Fixture Brightness");
            var scale = _selectedDriver.PropsTransform.localScale;
            scale.x = newLinear;
            _selectedDriver.PropsTransform.localScale = scale;
        });

        _spreadFloatField.RegisterValueChangedCallback(e =>
        {
            _spreadSlider.value = e.newValue;
            Undo.RecordObject(_selectedDriver.PropsTransform, "Fixture Spread");
            var scale = _selectedDriver.PropsTransform.localScale;
            scale.y = e.newValue;
            _selectedDriver.PropsTransform.localScale = scale;
        });

        _spreadSlider.RegisterValueChangedCallback(e =>
        {
            _spreadFloatField.value = e.newValue;
            Undo.RecordObject(_selectedDriver.PropsTransform, "Fixture Spread");
            var scale = _selectedDriver.PropsTransform.localScale;
            scale.y = e.newValue;
            _selectedDriver.PropsTransform.localScale = scale;
        });

        _axisXFloatField.RegisterValueChangedCallback(e =>
        {
            _axisXSlider.value = e.newValue;
            SetAxisRotation(0, e.newValue);
        });
        _axisXSlider.RegisterValueChangedCallback(e =>
        {
            _axisXFloatField.value = e.newValue;
            SetAxisRotation(0, e.newValue);
        });

        _axisYFloatField.RegisterValueChangedCallback(e =>
        {
            _axisYSlider.value = e.newValue;
            SetAxisRotation(1, e.newValue);
        });
        _axisYSlider.RegisterValueChangedCallback(e =>
        {
            _axisYFloatField.value = e.newValue;
            SetAxisRotation(1, e.newValue);
        });

        _axisZFloatField.RegisterValueChangedCallback(e =>
        {
            _axisZSlider.value = e.newValue;
            SetAxisRotation(2, e.newValue);
        });
        _axisZSlider.RegisterValueChangedCallback(e =>
        {
            _axisZFloatField.value = e.newValue;
            SetAxisRotation(2, e.newValue);
        });
    }

    private void RefreshColourUI()
    {
        if (_selectedDef.Colour == FixtureDefinition.ColourMode.Blackbody)
        {
            _colourTemperatureControl.style.display = DisplayStyle.Flex;
            _colourRGBControl.style.display = DisplayStyle.None;
            _colourPlaceholder.style.display = DisplayStyle.None;

            _colourTemperatureField.value = _selectedDef.ColourTemperature;
            Color preview = FixtureDefinition.BlackbodyToRGB(_selectedDef.ColourTemperature);
            _colourPreviewField.value = preview;
        }
        else
        {
            _colourTemperatureControl.style.display = DisplayStyle.None;
            _colourRGBControl.style.display = DisplayStyle.Flex;
            _colourPlaceholder.style.display = DisplayStyle.None;

            _emissionColourField.value = _selectedDef.EmissionColor;
        }
    }

    private void SetAxisRotation(int component, float value)
    {
        var head = _selectedDriver.Head;
        if (head == null) return;
        Undo.RecordObject(head, "Fixture Rotation");
        var euler = head.localEulerAngles;
        euler[component] = value;
        head.localEulerAngles = euler;
    }

    private void OnEditorUpdate()
    {
        if (_selectedDef != null && Selection.activeGameObject == _selectedDef.gameObject)
            Repaint();
    }

    private void OnSelectionChanged()
    {
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        _selectedDef = null;
        _selectedDriver = null;
        _selectedProfile = null;

        var active = Selection.activeGameObject;
        if (active == null)
        {
            ShowNoSelection();
            return;
        }

        _selectedDef = active.GetComponent<FixtureDefinition>();
        if (_selectedDef == null)
        {
            ShowNoSelection();
            return;
        }

        _selectedDriver = _selectedDef.GetComponent<FixtureDriver>();
        _selectedProfile = _selectedDef.Profile;

        if (_selectedDriver == null)
        {
            ShowError("Selected fixture has no FixtureDriver.");
            return;
        }

        if (_selectedProfile == null)
        {
            ShowError("Fixture has no Profile assigned.");
            return;
        }

        ShowProperties();
    }

    private void HideAllPanels()
    {
        _panelInfo.style.display = DisplayStyle.None;
        _panelColour.style.display = DisplayStyle.None;
        _panelIllumination.style.display = DisplayStyle.None;
        _panelTransform.style.display = DisplayStyle.None;
    }

    private void ShowNoSelection()
    {
        _noSelectionHelp.style.display = DisplayStyle.Flex;
        _errorHelp.style.display = DisplayStyle.None;
        HideAllPanels();
    }

    private void ShowError(string message)
    {
        _errorLabel.text = message;
        _noSelectionHelp.style.display = DisplayStyle.None;
        _errorHelp.style.display = DisplayStyle.Flex;
        HideAllPanels();
    }

    private void ShowProperties()
    {
        _noSelectionHelp.style.display = DisplayStyle.None;
        _errorHelp.style.display = DisplayStyle.None;
        _panelInfo.style.display = DisplayStyle.Flex;
        _panelColour.style.display = DisplayStyle.Flex;
        _panelIllumination.style.display = DisplayStyle.Flex;

        // Update fixture name
        string displayName = string.IsNullOrEmpty(_selectedDef.DisplayName) ? _selectedDef.gameObject.name : _selectedDef.DisplayName;
        _fixtureName.text = displayName;

        // Update on toggle
        _onToggle.value = _selectedDriver.PropsTransform.gameObject.activeSelf;

        // Update colour mode and controls
        _colourModeField.value = _selectedDef.Colour;
        RefreshColourUI();

        // Brightness
        _brightnessFloatField.style.display = DisplayStyle.Flex;
        _brightnessSlider.lowValue = _selectedProfile.BrightnessMin;
        _brightnessSlider.highValue = _selectedProfile.BrightnessMax;
        float currentLinear = _selectedDriver.PropsTransform.localScale.x;
        _brightnessSlider.value = Mathf.LinearToGammaSpace(currentLinear);
        _brightnessFloatField.value = _brightnessSlider.value;

        // Spread
        if (_selectedProfile.HasSpread)
        {
            _spreadPlaceholder.style.display = DisplayStyle.None;
            _spreadFloatField.style.display = DisplayStyle.Flex;
            _spreadSlider.style.display = DisplayStyle.Flex;
            _spreadSlider.value = _selectedDriver.PropsTransform.localScale.y;
            _spreadFloatField.value = _spreadSlider.value;
        }
        else
        {
            _spreadPlaceholder.style.display = DisplayStyle.Flex;
            _spreadFloatField.style.display = DisplayStyle.None;
            _spreadSlider.style.display = DisplayStyle.None;
        }

        // Rotations
        bool hasRotation = _selectedProfile.AxisX.Enabled || _selectedProfile.AxisY.Enabled || _selectedProfile.AxisZ.Enabled;
        _panelTransform.style.display = hasRotation ? DisplayStyle.Flex : DisplayStyle.None;
        _rotationPlaceholder.style.display = hasRotation ? DisplayStyle.None : DisplayStyle.Flex;

        if (_selectedProfile.AxisX.Enabled)
        {
            _axisXControl.style.display = DisplayStyle.Flex;
            _axisXSlider.lowValue = _selectedProfile.AxisX.Min;
            _axisXSlider.highValue = _selectedProfile.AxisX.Max;
            var axisXVal = NormalizeAngle(_selectedDriver.Head.localEulerAngles[0]);
            _axisXSlider.value = axisXVal;
            _axisXFloatField.value = axisXVal;
        }
        else
        {
            _axisXControl.style.display = DisplayStyle.None;
        }

        if (_selectedProfile.AxisY.Enabled)
        {
            _axisYControl.style.display = DisplayStyle.Flex;
            _axisYSlider.lowValue = _selectedProfile.AxisY.Min;
            _axisYSlider.highValue = _selectedProfile.AxisY.Max;
            var axisYVal = NormalizeAngle(_selectedDriver.Head.localEulerAngles[1]);
            _axisYSlider.value = axisYVal;
            _axisYFloatField.value = axisYVal;
        }
        else
        {
            _axisYControl.style.display = DisplayStyle.None;
        }

        if (_selectedProfile.AxisZ.Enabled)
        {
            _axisZControl.style.display = DisplayStyle.Flex;
            _axisZSlider.lowValue = _selectedProfile.AxisZ.Min;
            _axisZSlider.highValue = _selectedProfile.AxisZ.Max;
            var axisZVal = NormalizeAngle(_selectedDriver.Head.localEulerAngles[2]);
            _axisZSlider.value = axisZVal;
            _axisZFloatField.value = axisZVal;
        }
        else
        {
            _axisZControl.style.display = DisplayStyle.None;
        }
    }

    private static float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        return angle;
    }
}
