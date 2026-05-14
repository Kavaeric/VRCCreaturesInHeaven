using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

// Editor window for editing the currently selected fixture's animation properties.
// Displays and edits FixtureDefinition's keyframeable properties without requiring
// multi-select of parent and children. Supports multi-select for mass editing.
//
// Open via: Tools > Fixture Properties
public class DiamondEUIFixtureProperties : EditorWindow
{
    private List<(DiamondFixtureDefinition def, DiamondFixtureDriver driver, DiamondFixtureProfile profile)> _selection
        = new List<(DiamondFixtureDefinition, DiamondFixtureDriver, DiamondFixtureProfile)>();

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

    [MenuItem("Tools/Diamond/Fixture properties")]
    private static void Open() => GetWindow<DiamondEUIFixtureProperties>("Fixture properties");

    // Returns the project-relative path to the directory containing this script.
    // Used to load sibling assets (UXML) without hardcoding folder paths.
    private static string ScriptDir()
    {
        foreach (var guid in AssetDatabase.FindAssets($"t:MonoScript {nameof(DiamondEUIFixtureProperties)}"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.EndsWith($"{nameof(DiamondEUIFixtureProperties)}.cs"))
                return Path.GetDirectoryName(path).Replace('\\', '/');
        }
        return "Assets/Modules/Diamond/Editor";
    }

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
        // Set window icon
        string iconPath = $"{ScriptDir()}/Resources/Icons/Icon EUI DiamondFixtureProperties@2x.png";
        Texture2D windowIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
        titleContent = new GUIContent("Fixture properties", windowIcon);

        // Create UXML layout
        var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>($"{ScriptDir()}/DiamondEUIFixtureProperties.uxml");
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
        _colourModeField.Init(DiamondFixtureDefinition.ColourMode.Blackbody);
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
            foreach (var (_, driver, _) in _selection)
            {
                Undo.RecordObject(driver.PropsTransform.gameObject, "Fixture On/Off");
                driver.PropsTransform.gameObject.SetActive(e.newValue);
            }
        });

        _colourModeField.RegisterValueChangedCallback(e =>
        {
            var mode = (DiamondFixtureDefinition.ColourMode)e.newValue;
            foreach (var (def, _, _) in _selection)
            {
                Undo.RecordObject(def, "Fixture Colour Mode");
                def.Colour = mode;
            }
            RefreshColourUI();
        });

        _colourTemperatureField.RegisterValueChangedCallback(e =>
        {
            foreach (var (def, _, _) in _selection)
            {
                Undo.RecordObject(def, "Fixture Colour Temperature");
                def.ColourTemperature = e.newValue;
            }
            RefreshColourUI();
        });

        _emissionColourField.RegisterCallback<ChangeEvent<Color>>(e =>
        {
            foreach (var (def, _, _) in _selection)
            {
                Undo.RecordObject(def, "Fixture Emission Colour");
                def.EmissionColor = e.newValue;
            }
        });

        _brightnessFloatField.RegisterValueChangedCallback(e =>
        {
            _brightnessSlider.SetValueWithoutNotify(e.newValue);
            foreach (var (_, driver, _) in _selection)
            {
                Undo.RecordObject(driver.PropsTransform, "Fixture Brightness");
                var scale = driver.PropsTransform.localScale;
                scale.x = e.newValue;
                driver.PropsTransform.localScale = scale;
            }
        });

        _brightnessSlider.RegisterValueChangedCallback(e =>
        {
            _brightnessFloatField.SetValueWithoutNotify(e.newValue);
            foreach (var (_, driver, _) in _selection)
            {
                Undo.RecordObject(driver.PropsTransform, "Fixture Brightness");
                var scale = driver.PropsTransform.localScale;
                scale.x = e.newValue;
                driver.PropsTransform.localScale = scale;
            }
        });

        _spreadFloatField.RegisterValueChangedCallback(e =>
        {
            _spreadSlider.SetValueWithoutNotify(e.newValue);
            foreach (var (_, driver, profile) in _selection)
            {
                if (!profile.HasSpread) continue;
                Undo.RecordObject(driver.PropsTransform, "Fixture Spread");
                var scale = driver.PropsTransform.localScale;
                scale.y = e.newValue;
                driver.PropsTransform.localScale = scale;
            }
        });

        _spreadSlider.RegisterValueChangedCallback(e =>
        {
            _spreadFloatField.SetValueWithoutNotify(e.newValue);
            foreach (var (_, driver, profile) in _selection)
            {
                if (!profile.HasSpread) continue;
                Undo.RecordObject(driver.PropsTransform, "Fixture Spread");
                var scale = driver.PropsTransform.localScale;
                scale.y = e.newValue;
                driver.PropsTransform.localScale = scale;
            }
        });

        _axisXFloatField.RegisterValueChangedCallback(e =>
        {
            _axisXSlider.SetValueWithoutNotify(e.newValue);
            SetAxisRotation(0, e.newValue);
        });
        _axisXSlider.RegisterValueChangedCallback(e =>
        {
            _axisXFloatField.SetValueWithoutNotify(e.newValue);
            SetAxisRotation(0, e.newValue);
        });

        _axisYFloatField.RegisterValueChangedCallback(e =>
        {
            _axisYSlider.SetValueWithoutNotify(e.newValue);
            SetAxisRotation(1, e.newValue);
        });
        _axisYSlider.RegisterValueChangedCallback(e =>
        {
            _axisYFloatField.SetValueWithoutNotify(e.newValue);
            SetAxisRotation(1, e.newValue);
        });

        _axisZFloatField.RegisterValueChangedCallback(e =>
        {
            _axisZSlider.SetValueWithoutNotify(e.newValue);
            SetAxisRotation(2, e.newValue);
        });
        _axisZSlider.RegisterValueChangedCallback(e =>
        {
            _axisZFloatField.SetValueWithoutNotify(e.newValue);
            SetAxisRotation(2, e.newValue);
        });
    }

    private void RefreshColourUI()
    {
        if (_selection.Count == 0) return;

        var firstDef = _selection[0].def;
        var allSameMode = _selection.All(s => s.def.Colour == firstDef.Colour);

        if (allSameMode && firstDef.Colour == DiamondFixtureDefinition.ColourMode.Blackbody)
        {
            _colourTemperatureControl.style.display = DisplayStyle.Flex;
            _colourRGBControl.style.display = DisplayStyle.None;
            _colourPlaceholder.style.display = DisplayStyle.None;

            var allSameTemp = _selection.All(s => Mathf.Approximately(s.def.ColourTemperature, firstDef.ColourTemperature));
            if (allSameTemp)
            {
                _colourTemperatureField.value = firstDef.ColourTemperature;
                Color preview = DiamondFixtureDefinition.BlackbodyToRGB(firstDef.ColourTemperature);
                _colourPreviewField.value = preview;
            }
            else
            {
                _colourTemperatureField.SetValueWithoutNotify(0);
            }
        }
        else if (allSameMode && firstDef.Colour == DiamondFixtureDefinition.ColourMode.RGB)
        {
            _colourTemperatureControl.style.display = DisplayStyle.None;
            _colourRGBControl.style.display = DisplayStyle.Flex;
            _colourPlaceholder.style.display = DisplayStyle.None;

            var allSameColor = _selection.All(s => s.def.EmissionColor == firstDef.EmissionColor);
            if (allSameColor)
            {
                _emissionColourField.value = firstDef.EmissionColor;
            }
            else
            {
                _emissionColourField.SetValueWithoutNotify(Color.black);
            }
        }
        else
        {
            _colourTemperatureControl.style.display = DisplayStyle.None;
            _colourRGBControl.style.display = DisplayStyle.None;
            _colourPlaceholder.style.display = DisplayStyle.Flex;
        }
    }

    private void SetAxisRotation(int component, float value)
    {
        foreach (var (_, driver, profile) in _selection)
        {
            var axis = component == 0 ? profile.AxisX : component == 1 ? profile.AxisY : profile.AxisZ;
            if (!axis.Enabled) continue;

            var head = driver.Head;
            if (head == null) continue;
            Undo.RecordObject(head, "Fixture Rotation");
            var euler = head.localEulerAngles;
            euler[component] = value;
            head.localEulerAngles = euler;
        }
    }

    private void OnEditorUpdate()
    {
        if (_selection.Count == 0) return;

        // Update brightness
        var brightnessValues = _selection.Select(s => s.driver.PropsTransform.localScale.x).Distinct().ToList();
        if (brightnessValues.Count == 1)
        {
            if (!Mathf.Approximately(_brightnessSlider.value, brightnessValues[0]))
            {
                _brightnessSlider.SetValueWithoutNotify(brightnessValues[0]);
                _brightnessFloatField.SetValueWithoutNotify(brightnessValues[0]);
            }
        }

        // Update spread
        bool anyHasSpread = _selection.Any(s => s.profile.HasSpread);
        if (anyHasSpread)
        {
            var spreadCapable = _selection.Where(s => s.profile.HasSpread).ToList();
            var spreadValues = spreadCapable.Select(s => s.driver.PropsTransform.localScale.y).Distinct().ToList();
            if (spreadValues.Count == 1)
            {
                if (!Mathf.Approximately(_spreadSlider.value, spreadValues[0]))
                {
                    _spreadSlider.SetValueWithoutNotify(spreadValues[0]);
                    _spreadFloatField.SetValueWithoutNotify(spreadValues[0]);
                }
            }
        }

        // Update rotations
        bool anyAxisXEnabled = _selection.Any(s => s.profile.AxisX.Enabled);
        if (anyAxisXEnabled)
        {
            var axisXCapable = _selection.Where(s => s.profile.AxisX.Enabled).ToList();
            var axisXValues = axisXCapable.Select(s => NormalizeAngle(s.driver.Head.localEulerAngles[0])).Distinct().ToList();
            if (axisXValues.Count == 1)
            {
                if (!Mathf.Approximately(_axisXSlider.value, axisXValues[0]))
                {
                    _axisXSlider.SetValueWithoutNotify(axisXValues[0]);
                    _axisXFloatField.SetValueWithoutNotify(axisXValues[0]);
                }
            }
        }

        bool anyAxisYEnabled = _selection.Any(s => s.profile.AxisY.Enabled);
        if (anyAxisYEnabled)
        {
            var axisYCapable = _selection.Where(s => s.profile.AxisY.Enabled).ToList();
            var axisYValues = axisYCapable.Select(s => NormalizeAngle(s.driver.Head.localEulerAngles[1])).Distinct().ToList();
            if (axisYValues.Count == 1)
            {
                if (!Mathf.Approximately(_axisYSlider.value, axisYValues[0]))
                {
                    _axisYSlider.SetValueWithoutNotify(axisYValues[0]);
                    _axisYFloatField.SetValueWithoutNotify(axisYValues[0]);
                }
            }
        }

        bool anyAxisZEnabled = _selection.Any(s => s.profile.AxisZ.Enabled);
        if (anyAxisZEnabled)
        {
            var axisZCapable = _selection.Where(s => s.profile.AxisZ.Enabled).ToList();
            var axisZValues = axisZCapable.Select(s => NormalizeAngle(s.driver.Head.localEulerAngles[2])).Distinct().ToList();
            if (axisZValues.Count == 1)
            {
                if (!Mathf.Approximately(_axisZSlider.value, axisZValues[0]))
                {
                    _axisZSlider.SetValueWithoutNotify(axisZValues[0]);
                    _axisZFloatField.SetValueWithoutNotify(axisZValues[0]);
                }
            }
        }

        Repaint();
    }

    private void OnSelectionChanged()
    {
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (_noSelectionHelp == null) return; // CreateGUI hasn't run yet
        _selection.Clear();

        var selected = Selection.gameObjects;
        if (selected.Length == 0)
        {
            ShowNoSelection();
            return;
        }

        var invalidCount = 0;
        foreach (var obj in selected)
        {
            var def = obj.GetComponent<DiamondFixtureDefinition>();
            if (def == null) continue;

            var driver = def.GetComponent<DiamondFixtureDriver>();
            var profile = def.Profile;

            if (driver == null || profile == null)
            {
                invalidCount++;
                continue;
            }

            _selection.Add((def, driver, profile));
        }

        if (_selection.Count == 0)
        {
            if (invalidCount > 0)
                ShowError($"{invalidCount} fixture(s) selected but incomplete (missing driver or profile).");
            else
                ShowNoSelection();
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
        if (_selection.Count == 1)
        {
            var def = _selection[0].def;
            string displayName = string.IsNullOrEmpty(def.DisplayName) ? def.gameObject.name : def.DisplayName;
            _fixtureName.text = displayName;
        }
        else
        {
            _fixtureName.text = $"{_selection.Count} fixtures selected";
        }

        // Update on toggle - show blank if mixed
        var onStates = _selection.Select(s => s.driver.PropsTransform.gameObject.activeSelf).Distinct().ToList();
        if (onStates.Count == 1)
        {
            _onToggle.SetValueWithoutNotify(onStates[0]);
        }
        else
        {
            _onToggle.SetValueWithoutNotify(false);
        }

        // Update colour mode and controls
        var colorModes = _selection.Select(s => s.def.Colour).Distinct().ToList();
        if (colorModes.Count == 1)
        {
            _colourModeField.SetValueWithoutNotify(colorModes[0]);
        }
        else
        {
            _colourModeField.SetValueWithoutNotify(null);
        }
        RefreshColourUI();

        // Brightness - union of all profiles
        _brightnessFloatField.style.display = DisplayStyle.Flex;
        float minBrightness = _selection.Min(s => s.profile.BrightnessMin);
        float maxBrightness = _selection.Max(s => s.profile.BrightnessMax);
        _brightnessSlider.lowValue = minBrightness;
        _brightnessSlider.highValue = maxBrightness;

        var brightnessValues = _selection.Select(s => s.driver.PropsTransform.localScale.x).Distinct().ToList();
        if (brightnessValues.Count == 1)
        {
            _brightnessSlider.SetValueWithoutNotify(brightnessValues[0]);
            _brightnessFloatField.SetValueWithoutNotify(brightnessValues[0]);
        }
        else
        {
            _brightnessSlider.SetValueWithoutNotify(0);
            _brightnessFloatField.SetValueWithoutNotify(0);
        }

        // Spread - show only if any fixture has it
        bool anyHasSpread = _selection.Any(s => s.profile.HasSpread);
        if (anyHasSpread)
        {
            _spreadPlaceholder.style.display = DisplayStyle.None;
            _spreadFloatField.style.display = DisplayStyle.Flex;
            _spreadSlider.style.display = DisplayStyle.Flex;

            var spreadCapable = _selection.Where(s => s.profile.HasSpread).ToList();
            var spreadValues = spreadCapable.Select(s => s.driver.PropsTransform.localScale.y).Distinct().ToList();
            if (spreadValues.Count == 1)
            {
                _spreadSlider.SetValueWithoutNotify(spreadValues[0]);
                _spreadFloatField.SetValueWithoutNotify(spreadValues[0]);
            }
            else
            {
                _spreadSlider.SetValueWithoutNotify(0);
                _spreadFloatField.SetValueWithoutNotify(0);
            }
        }
        else
        {
            _spreadPlaceholder.style.display = DisplayStyle.Flex;
            _spreadFloatField.style.display = DisplayStyle.None;
            _spreadSlider.style.display = DisplayStyle.None;
        }

        // Rotations - show if any fixture has rotation
        bool hasRotation = _selection.Any(s => s.profile.AxisX.Enabled || s.profile.AxisY.Enabled || s.profile.AxisZ.Enabled);
        _panelTransform.style.display = hasRotation ? DisplayStyle.Flex : DisplayStyle.None;
        _rotationPlaceholder.style.display = hasRotation ? DisplayStyle.None : DisplayStyle.Flex;

        // Axis X
        bool anyAxisXEnabled = _selection.Any(s => s.profile.AxisX.Enabled);
        if (anyAxisXEnabled)
        {
            _axisXControl.style.display = DisplayStyle.Flex;
            var axisXCapable = _selection.Where(s => s.profile.AxisX.Enabled).ToList();
            _axisXSlider.lowValue = axisXCapable.Min(s => s.profile.AxisX.Min);
            _axisXSlider.highValue = axisXCapable.Max(s => s.profile.AxisX.Max);
            var axisXValues = axisXCapable.Select(s => NormalizeAngle(s.driver.Head.localEulerAngles[0])).Distinct().ToList();
            if (axisXValues.Count == 1)
            {
                _axisXSlider.SetValueWithoutNotify(axisXValues[0]);
                _axisXFloatField.SetValueWithoutNotify(axisXValues[0]);
            }
            else
            {
                _axisXSlider.SetValueWithoutNotify(0);
                _axisXFloatField.SetValueWithoutNotify(0);
            }
        }
        else
        {
            _axisXControl.style.display = DisplayStyle.None;
        }

        // Axis Y
        bool anyAxisYEnabled = _selection.Any(s => s.profile.AxisY.Enabled);
        if (anyAxisYEnabled)
        {
            _axisYControl.style.display = DisplayStyle.Flex;
            var axisYCapable = _selection.Where(s => s.profile.AxisY.Enabled).ToList();
            _axisYSlider.lowValue = axisYCapable.Min(s => s.profile.AxisY.Min);
            _axisYSlider.highValue = axisYCapable.Max(s => s.profile.AxisY.Max);
            var axisYValues = axisYCapable.Select(s => NormalizeAngle(s.driver.Head.localEulerAngles[1])).Distinct().ToList();
            if (axisYValues.Count == 1)
            {
                _axisYSlider.SetValueWithoutNotify(axisYValues[0]);
                _axisYFloatField.SetValueWithoutNotify(axisYValues[0]);
            }
            else
            {
                _axisYSlider.SetValueWithoutNotify(0);
                _axisYFloatField.SetValueWithoutNotify(0);
            }
        }
        else
        {
            _axisYControl.style.display = DisplayStyle.None;
        }

        // Axis Z
        bool anyAxisZEnabled = _selection.Any(s => s.profile.AxisZ.Enabled);
        if (anyAxisZEnabled)
        {
            _axisZControl.style.display = DisplayStyle.Flex;
            var axisZCapable = _selection.Where(s => s.profile.AxisZ.Enabled).ToList();
            _axisZSlider.lowValue = axisZCapable.Min(s => s.profile.AxisZ.Min);
            _axisZSlider.highValue = axisZCapable.Max(s => s.profile.AxisZ.Max);
            var axisZValues = axisZCapable.Select(s => NormalizeAngle(s.driver.Head.localEulerAngles[2])).Distinct().ToList();
            if (axisZValues.Count == 1)
            {
                _axisZSlider.SetValueWithoutNotify(axisZValues[0]);
                _axisZFloatField.SetValueWithoutNotify(axisZValues[0]);
            }
            else
            {
                _axisZSlider.SetValueWithoutNotify(0);
                _axisZFloatField.SetValueWithoutNotify(0);
            }
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
