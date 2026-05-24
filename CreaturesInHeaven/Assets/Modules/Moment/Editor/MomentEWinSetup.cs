using UnityEngine;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using VRCLightVolumes;

// One-time initialisation tool for a MomentAnimatedLightVolume.
// Defines the light volume target, lighting/animation settings, and output texture, then in one action:
//   1. Saves the chosen params onto the ALV component's Bake* fields (the source of truth).
//   2. Sets up the CRT and material (via MomentCRTSetup).
//   3. Creates a blank packed Texture3D atlas at the right dimensions.
//   4. Writes a fresh sidecar (.alv.json) with every snapshot marked unbaked.
//   5. Assigns the new atlas to the ALV component so the runtime can play it back.
//
// The MomentEWinBaker (bake window) reads these params and the sidecar but never modifies them.
// If the user wants to change setup later, they reopen this window and re-run setup — which wipes
// the existing atlas. This is deliberate, and replaces the silent atlas re-init the old baker did.
//
// Open via Tools > Moment ALV > Set up animated light volume...
#if BAKERY_INCLUDED
public class MomentEWinSetup : EditorWindow
{
    // --- Setup fields (mirrored to alv.Bake* on save) -------------------

    LightVolume _targetVolume;
    Animator _animator;
    MomentBakeParams _params = new MomentBakeParams { EndFrame = -1, SnapshotCount = 8 };
    MomentALVSHMode   _shMode   = MomentALVSHMode.MonoL1;
    MomentALVBitDepth _bitDepth = MomentALVBitDepth.Depth8;
    string _outputName = "ALV_Bake";

    // --- UI element refs ------------------------------------------------

    ObjectField _volumeField;
    ObjectField _animatorField;
    ObjectField _clipField;
    IntegerField _startFrameField;
    IntegerField _endFrameField;
    IntegerField _snapshotCountField;
    TextField _outputNameField;
    EnumField _shModeField;
    [SerializeField] AtelierHelpPage _shModeHelpPage;
    EnumField _bitDepthField;
    [SerializeField] AtelierHelpPage _bitDepthHelpPage;
    HelpBox _validationBox;
    HelpBox _existingBakeBox;
    Button _setupBtn;
    Label _animFrameInterval;
    Label _outputResLabel;
    Label _vramSizeLabel;
    Label _bundleSizeLabel;
    VisualElement _showWithVolume;
    VisualElement _hideWithVolume;

    // Returns the MomentAnimatedLightVolume on the same GameObject as the target volume, if present.
    MomentAnimatedLightVolume MomentOnVolume =>
        _targetVolume != null ? _targetVolume.GetComponent<MomentAnimatedLightVolume>() : null;

    // --- Window lifecycle -----------------------------------------------

    [MenuItem("Tools/Moment ALV/Set up animated light volume...")]
    static void Open() => GetWindow<MomentEWinSetup>("Set up ALV");

    // Allows other windows (the baker) to focus this window with a specific volume pre-selected.
    public static void OpenWithVolume(LightVolume volume)
    {
        var win = GetWindow<MomentEWinSetup>("Set up ALV");
        win.SetTargetVolume(volume);
    }

    void SetTargetVolume(LightVolume volume)
    {
        _targetVolume = volume;
        _volumeField?.SetValueWithoutNotify(volume);
        if (volume != null) LoadFromALV();
        UpdateUI();
    }

    void OnInspectorUpdate()
    {
        // Catch externally-nulled references (deleted object, scene change).
        UpdateUI();
    }

    public void CreateGUI()
    {
        string dir = MomentAssetPaths.ScriptDir();

        string iconPath = $"{dir}/Resources/Icons/Icon Moment EWin Baker@2x.png";
        Texture2D windowIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
        titleContent = new GUIContent("Set up ALV", windowIcon);

        var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>($"{dir}/MomentEWinSetup.uxml");
        if (uxml == null)
        {
            rootVisualElement.Add(new Label($"MomentEWinSetup.uxml not found in {dir}."));
            return;
        }
        uxml.CloneTree(rootVisualElement);

        // --- Target volume ---
        _volumeField = rootVisualElement.Q<ObjectField>("volume-field");
        _volumeField.objectType = typeof(LightVolume);
        _volumeField.value = _targetVolume;
        _volumeField.RegisterValueChangedCallback(e =>
        {
            _targetVolume = e.newValue as LightVolume;
            if (_targetVolume != null) LoadFromALV();
            UpdateUI();
        });

        // --- Lighting ---
        _shModeField = rootVisualElement.Q<EnumField>("sh-mode-field");
        _shModeField.Init(_shMode);
        rootVisualElement.Q<Button>("help-btn-sh-mode").clicked += () => AtelierHelpWindow.Open(_shModeHelpPage);
        _shModeField.RegisterValueChangedCallback(e =>
        {
            _shMode = (MomentALVSHMode)e.newValue;
            UpdateUI();
        });

        _bitDepthField = rootVisualElement.Q<EnumField>("bit-depth-field");
        _bitDepthField.Init(_bitDepth);
        rootVisualElement.Q<Button>("help-btn-bit-depth").clicked += () => AtelierHelpWindow.Open(_bitDepthHelpPage);
        _bitDepthField.RegisterValueChangedCallback(e =>
        {
            _bitDepth = (MomentALVBitDepth)e.newValue;
            UpdateUI();
        });

        // --- Animation ---
        _animatorField = rootVisualElement.Q<ObjectField>("animator-field");
        _animatorField.objectType = typeof(Animator);
        _animatorField.value = _animator;
        _animatorField.RegisterValueChangedCallback(e =>
        {
            _animator = e.newValue as Animator;
            UpdateUI();
        });

        _clipField = rootVisualElement.Q<ObjectField>("clip-field");
        _clipField.objectType = typeof(AnimationClip);
        _clipField.value = _params.Clip;
        _clipField.RegisterValueChangedCallback(e =>
        {
            _params.Clip = e.newValue as AnimationClip;
            _params.StartFrame = 0;
            _params.EndFrame = -1;
            UpdateRangeFields();
            UpdateUI();
        });

        // --- Flipbook output ---
        _snapshotCountField = rootVisualElement.Q<IntegerField>("snapshot-count-field");
        _snapshotCountField.value = _params.SnapshotCount;
        _snapshotCountField.RegisterCallback<FocusOutEvent>(_ =>
        {
            _params.SnapshotCount = Mathf.Max(_snapshotCountField.value, 2);
            _snapshotCountField.SetValueWithoutNotify(_params.SnapshotCount);
            UpdateUI();
        });

        _startFrameField = rootVisualElement.Q<IntegerField>("start-time-field");
        _startFrameField.value = _params.StartFrame;
        _startFrameField.RegisterCallback<FocusOutEvent>(_ =>
        {
            _params.StartFrame = Mathf.Max(_startFrameField.value, 0);
            _startFrameField.SetValueWithoutNotify(_params.StartFrame);
            UpdateUI();
        });

        _endFrameField = rootVisualElement.Q<IntegerField>("end-time-field");
        _endFrameField.value = _params.EndFrame;
        _endFrameField.RegisterCallback<FocusOutEvent>(_ =>
        {
            _params.EndFrame = _endFrameField.value;
            UpdateUI();
        });

        _outputNameField = rootVisualElement.Q<TextField>("output-name-field");
        _outputNameField.value = _outputName;
        _outputNameField.RegisterValueChangedCallback(e =>
        {
            _outputName = e.newValue;
            UpdateUI();
        });

        // --- Output estimates ---
        _animFrameInterval = rootVisualElement.Q<Label>("anim-frame-interval");
        _outputResLabel    = rootVisualElement.Q<Label>("output-res");
        _vramSizeLabel     = rootVisualElement.Q<Label>("vram-size");
        _bundleSizeLabel   = rootVisualElement.Q<Label>("estimated-bundle-size");

        // --- Status / action ---
        _validationBox   = rootVisualElement.Q<HelpBox>("validation-box");
        _existingBakeBox = rootVisualElement.Q<HelpBox>("existing-bake-box");
        _setupBtn        = rootVisualElement.Q<Button>("setup-btn");
        _setupBtn.clicked += RunSetup;

        // --- Volume-dependant section ---
        _showWithVolume = rootVisualElement.Q<VisualElement>("show-with-volume");
        _hideWithVolume = rootVisualElement.Q<VisualElement>("hide-with-volume");

        UpdateUI();
    }

    // Copies bake settings from the Moment component on the target volume's GameObject into the window fields.
    void LoadFromALV()
    {
        MomentAnimatedLightVolume alv = MomentOnVolume;
        if (alv == null) return;
        _animator             = alv.BakeAnimator;
        _params.Clip          = alv.BakeClip;
        _params.SnapshotCount = alv.BakeSnapshotCount;
        _params.StartFrame    = alv.BakeStartFrame;
        _params.EndFrame      = alv.BakeEndFrame;
        _shMode               = alv.BakeSHMode;
        _bitDepth             = alv.BakeBitDepth;
        _outputName           = alv.BakeOutputName;

        _animatorField?.SetValueWithoutNotify(_animator);
        _clipField?.SetValueWithoutNotify(_params.Clip);
        _snapshotCountField?.SetValueWithoutNotify(_params.SnapshotCount);
        _outputNameField?.SetValueWithoutNotify(_outputName);
        _shModeField?.SetValueWithoutNotify(_shMode);
        _bitDepthField?.SetValueWithoutNotify(_bitDepth);
        UpdateRangeFields();
    }

    // Writes current window fields onto the Moment component as the canonical params for later baking.
    void SaveToALV()
    {
        MomentAnimatedLightVolume alv = MomentOnVolume;
        if (alv == null) return;
        alv.BakeAnimator      = _animator;
        alv.BakeClip          = _params.Clip;
        alv.BakeSnapshotCount = _params.SnapshotCount;
        alv.BakeStartFrame    = _params.StartFrame;
        alv.BakeEndFrame      = _params.EndFrame;
        alv.BakeSHMode        = _shMode;
        alv.BakeBitDepth      = _bitDepth;
        alv.BakeOutputName    = _outputName;
        EditorUtility.SetDirty(alv);
    }

    void UpdateRangeFields()
    {
        _startFrameField?.SetValueWithoutNotify(_params.StartFrame);
        _endFrameField?.SetValueWithoutNotify(_params.EndFrame);
    }

    // --- UI refresh -----------------------------------------------------

    void UpdateUI()
    {
        if (_setupBtn == null) return;

        // Re-read the field in case the referenced object was deleted or the scene changed.
        _targetVolume = _volumeField?.value as LightVolume;

        _showWithVolume.style.display = _targetVolume != null ? DisplayStyle.Flex : DisplayStyle.None;
        _hideWithVolume.style.display = _targetVolume != null ? DisplayStyle.None : DisplayStyle.Flex;

        string error = _params.Validate(_animator, _targetVolume);

        _validationBox.text = error ?? "";
        _validationBox.style.display = error != null ? DisplayStyle.Flex : DisplayStyle.None;

        _setupBtn.SetEnabled(error == null);

        UpdateOutputAnimationEstimates();
        UpdateOutputTextureEstimates();
        UpdateExistingBakeWarning();
    }

    void UpdateOutputAnimationEstimates()
    {
        if (_animator == null || _params.Clip == null)
        {
            _animFrameInterval.text = "—";
            return;
        }

        // Number of animation frames between successive snapshots. Subtract one from SnapshotCount
        // since both the first and last frame are captured.
        float frameInterval = Mathf.Round(_params.BakeDuration * _params.Clip.frameRate) / (_params.SnapshotCount - 1);
        _animFrameInterval.text = $"f{frameInterval:0.###}";
    }

    void UpdateOutputTextureEstimates()
    {
        if (_outputResLabel == null) return;

        if (_targetVolume == null)
        {
            _outputResLabel.text  = "—";
            _vramSizeLabel.text   = "—";
            _bundleSizeLabel.text = "—";
            return;
        }

        Vector3Int res = _targetVolume.Resolution;
        int w = res.x, h = res.y, d = res.z;

        int packedH = MomentALVFormat.PackedHeight(h, _params.SnapshotCount);
        int packedD = MomentALVFormat.PackedDepth(d, _shMode);
        _outputResLabel.text = $"{w} × {packedH} × {packedD}";

        double vram = MomentALVFormat.VramMB(w, h, d, _params.SnapshotCount, _shMode, _bitDepth);
        double bundleLow  = vram * MomentALVFormat.BundleRatioLow;
        double bundleHigh = vram * MomentALVFormat.BundleHighRatio(_shMode);

        _vramSizeLabel.text   = $"{vram:0.00} MB";
        _bundleSizeLabel.text = $"{bundleLow:0.00} – {bundleHigh:0.00} MB";
    }

    // Warns the user when running setup would overwrite existing baked data.
    void UpdateExistingBakeWarning()
    {
        if (_existingBakeBox == null) return;
        if (_targetVolume == null) { _existingBakeBox.style.display = DisplayStyle.None; return; }

        string assetPath = ResolveAssetPath();
        MomentTextureInfo existing = MomentTextureInfo.Load(assetPath);
        bool hasBaked = existing != null && existing.snapshots != null
            && System.Array.Exists(existing.snapshots, s => s.baked);

        if (!hasBaked) { _existingBakeBox.style.display = DisplayStyle.None; return; }

        _existingBakeBox.text = $"Existing baked data will be overwritten at {assetPath}.";
        _existingBakeBox.style.display = DisplayStyle.Flex;
    }

    // --- Setup action ---------------------------------------------------

    string ResolveAssetPath()
    {
        string assetDir = MomentAssetPaths.SceneAssetDir();
        return $"{assetDir}/{_outputName}.asset";
    }

    void RunSetup()
    {
        MomentAnimatedLightVolume alv = MomentOnVolume;
        if (alv == null)
        {
            EditorUtility.DisplayDialog("Set up ALV",
                $"The target Light Volume has no MomentAnimatedLightVolume component. Add one to '{_targetVolume.gameObject.name}' before setting up.",
                "OK");
            return;
        }

        string assetPath = ResolveAssetPath();

        // Confirm overwrite if a baked sidecar already exists.
        MomentTextureInfo existing = MomentTextureInfo.Load(assetPath);
        bool hasBaked = existing != null && existing.snapshots != null
            && System.Array.Exists(existing.snapshots, s => s.baked);
        if (hasBaked)
        {
            bool ok = EditorUtility.DisplayDialog("Set up ALV",
                $"Setting up this ALV will overwrite the existing baked data at {assetPath}.\n\nContinue?",
                "Set up", "Cancel");
            if (!ok) return;
        }

        // 1. Save params onto the component first so anything downstream reads canonical values.
        SaveToALV();

        // 2. Provision CRT + material and register with the scene's LightVolumeSetup.
        MomentCRTSetup.SetupCRT(alv);

        // 3. Create the blank atlas asset at the chosen path with the chosen format.
        Vector3Int res = _targetVolume.Resolution;
        int w = res.x, h = res.y, d = res.z;

        string assetDir = MomentAssetPaths.SceneAssetDir();
        MomentAssetPaths.CreateDirectory(assetDir);

        Texture3D tex = MomentTextureWriter.InitialiseTexture(w, h, d, _params.SnapshotCount, _shMode, _bitDepth, assetPath);

        // 4. Write a fresh sidecar with all snapshots marked unbaked.
        MomentTextureInfo sidecar = new MomentTextureInfo
        {
            snapshotX    = w,
            snapshotY    = h,
            snapshotZ    = d,
            numSnapshots = _params.SnapshotCount,
            shMode       = _shMode,
            bitDepth     = _bitDepth,
            snapshots    = new MomentTextureInfo.SnapshotEntry[_params.SnapshotCount],
        };
        sidecar.Save(assetPath);

        // 5. Wire the atlas onto the ALV so the runtime knows where to read from, and
        // propagate the layout (SnapshotY, SHMode, BitDepth) used by the runtime/preview.
        alv.AnimatedTexture = tex;
        sidecar.ApplyTo(alv);
        EditorUtility.SetDirty(alv);
        AssetDatabase.SaveAssets();

        Debug.Log($"[Moment] Set up '{alv.gameObject.name}' → atlas at {assetPath} ({w}×{MomentALVFormat.PackedHeight(h, _params.SnapshotCount)}×{MomentALVFormat.PackedDepth(d, _shMode)}, {_params.SnapshotCount} snapshots, {_shMode}, {_bitDepth}).");

        UpdateUI();
    }
}
#endif
