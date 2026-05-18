using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using VRCLightVolumes;

// Bakes an AnimatedLightVolume texture by:
//   1. Force-evaluating an Animator to N evenly-spaced times across an AnimationClip.
//   2. Triggering a Bakery bake for each snapshot.
//   3. Reading BakeryVolume.bakedTexture0/1/2 after each bake.
//   4. Packing all snapshots into a Texture3D via ALVTextureWriter.SavePackedTexture.
//
// Open via Tools > Lighting > Bake animated light volume...
#if BAKERY_INCLUDED
public class ALVTextureBaker : EditorWindow
{
    // --- Fields serialised in the window --------------------------------

    LightVolume _targetVolume;
    Animator _animator;
    AnimationClip _animClip;
    int _startFrame = 0;
    int _endFrame = -1; // -1 = use last frame of clip
    int _snapshotCount = 8;
    ALVSHMode   _shMode   = ALVSHMode.MonoL1;
    ALVBitDepth _bitDepth = ALVBitDepth.Depth8;
    string _outputName = "ALV_Bake";

    // --- Internal bake state --------------------------------------------

    bool _baking = false;
    int _currentSnapshot = 0;
    readonly List<ALVTextureWriter.SnapshotSH> _collectedSnapshots = new();

    // Hierarchy paths resolved at bake start, used to re-find objects across
    // snapshots in case Bakery's scene management destroys the live references mid-bake.
    string _animatorPath;
    string _targetVolumePath;

    // Stopwatch runs for each snapshot bake; first completed bake sets
    // _secsPerSnapshotBake for the time estimate.
    readonly System.Diagnostics.Stopwatch _snapshotStopwatch = new();
    double _secsPerSnapshotBake = -1;

    // 0-based index of the currently previewed snapshot.
    int _previewSnapshot = 0;

    // --- Animation window (reflection) ----------------------------------
    // The Animation window has no public API for setting the current frame,
    // so it's accessed via reflection.

    EditorWindow _animationWindow;
    PropertyInfo _animWindowFrameProp;

    int AnimationWindowFrame
    {
        set
        {
            if (_animationWindow == null || _animWindowFrameProp == null) FindAnimationWindow();
            _animWindowFrameProp?.SetValue(_animationWindow, value, null);
        }
    }

    void FindAnimationWindow()
    {
        var type = System.Type.GetType("UnityEditor.AnimationWindow, UnityEditor");
        if (type == null) return;
        var windows = Resources.FindObjectsOfTypeAll(type);
        if (windows.Length == 0) return;
        _animationWindow = windows[0] as EditorWindow;
        _animWindowFrameProp = type.GetProperty("frame",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    }

    // Resolved bake window in seconds, derived from frame fields and clip frame rate.
    float BakeStart    => _animClip != null ? Mathf.Clamp(_startFrame / _animClip.frameRate, 0f, _animClip.length) : 0f;
    float BakeEnd      => _animClip != null ? (_endFrame < 0 ? _animClip.length : Mathf.Clamp(_endFrame / _animClip.frameRate, BakeStart, _animClip.length)) : 0f;
    float BakeDuration => BakeEnd - BakeStart;

    // Returns the animation window frame index for a given snapshot index (0-based).
    int SnapshotToAnimFrame(int snapshotIndex)
    {
        if (_animClip == null) return 0;
        float t = BakeStart + ((_snapshotCount > 1) ? BakeDuration * snapshotIndex / (_snapshotCount - 1) : 0f);
        return Mathf.RoundToInt(t * _animClip.frameRate);
    }

    // Returns the AnimatedLightVolume on the same GameObject as the target volume, if present.
    AnimatedLightVolume ALVOnVolume =>
        _targetVolume != null ? _targetVolume.GetComponent<AnimatedLightVolume>() : null;

    // --- Window lifecycle -----------------------------------------------

    [MenuItem("Tools/Lighting/Bake animated light volume...")]
    static void Open() => GetWindow<ALVTextureBaker>("Bake animated light volume");

    void OnEnable()
    {
        ftRenderLightmap.OnFinishedFullRender += OnBakeFinished;
        FindAnimationWindow();
    }

    void OnDisable()
    {
        ftRenderLightmap.OnFinishedFullRender -= OnBakeFinished;
        if (_baking) AbortBake("Window closed");
    }

    // Fires at ~10Hz; catches externally-nulled references (deleted object, scene change)
    // that don't trigger the field's value-changed callback.
    void OnInspectorUpdate() => UpdateUI();

    // Returns the project-relative path to the directory containing this script.
    static string ScriptDir()
    {
        foreach (var guid in AssetDatabase.FindAssets($"t:MonoScript {nameof(ALVTextureBaker)}"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.EndsWith($"{nameof(ALVTextureBaker)}.cs"))
                return Path.GetDirectoryName(path).Replace('\\', '/');
        }
        return "Assets/Modules/Animated Light Volumes/Editor";
    }

    // --- UI -------------------------------------------------------------

    // Cached element refs, written to by CreateGUI and read/updated by bake logic.
    ObjectField _volumeField;
    ObjectField _animatorField;
    ObjectField _clipField;
    IntegerField _startFrameField;
    IntegerField _endFrameField;
    IntegerField _snapshotCountField;
    TextField _outputNameField;
    EnumField _shModeField;
    EnumField _bitDepthField;
    HelpBox _modeHintL1;
    HelpBox _modeHintMonoL1;
    HelpBox _modeHintMonoL0;
    HelpBox _formatHint16BPC;
    HelpBox _formatHint8BPC;
    HelpBox _validationBox;
    HelpBox _bakeProgressBox;
    Button _bakeBtn;
    Button _cancelBtn;
    IntegerField _previewSnapshotField;
    Label _previewSnapshotMax;
    Label _animFrameCounter;
    Label _animFrameInterval;
    VisualElement _previewControls;
    Label _outputResLabel;
    Label _vramSizeLabel;
    Label _bundleSizeLabel;
    VisualElement _showWithVolume;
    VisualElement _hideWithVolume;

    public void CreateGUI()
    {
        string dir = ScriptDir();
        var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>($"{dir}/ALVBakeTexture.uxml");
        if (uxml == null)
        {
            rootVisualElement.Add(new Label($"ALVBakeTexture.uxml not found in {dir}."));
            return;
        }
        uxml.CloneTree(rootVisualElement);

        // --- Volume field (top) ---

        _volumeField = rootVisualElement.Q<ObjectField>("volume-field");
        _volumeField.objectType = typeof(LightVolume);
        _volumeField.value = _targetVolume;
        _volumeField.RegisterValueChangedCallback(e =>
        {
            _targetVolume = e.newValue as LightVolume;
            if (_targetVolume != null) LoadFromALV();
            UpdateUI();
        });

        // --- Setup fields ---

        _animatorField = rootVisualElement.Q<ObjectField>("animator-field");
        _animatorField.objectType = typeof(Animator);
        _animatorField.value = _animator;
        _animatorField.RegisterValueChangedCallback(e =>
        {
            _animator = e.newValue as Animator;
            SaveToALV();
            UpdateUI();
        });

        _clipField = rootVisualElement.Q<ObjectField>("clip-field");
        _clipField.objectType = typeof(AnimationClip);
        _clipField.value = _animClip;
        _clipField.RegisterValueChangedCallback(e =>
        {
            _animClip = e.newValue as AnimationClip;
            _startFrame = 0;
            _endFrame = -1;
            _previewSnapshot = 0;
            UpdateRangeFields();
            UpdatePreviewReadout();
            SaveToALV();
            UpdateUI();
        });

        _startFrameField = rootVisualElement.Q<IntegerField>("start-time-field");
        _startFrameField.value = _startFrame;
        _startFrameField.RegisterCallback<FocusOutEvent>(_ =>
        {
            _startFrame = Mathf.Max(_startFrameField.value, 0);
            _startFrameField.SetValueWithoutNotify(_startFrame);
            SaveToALV();
            UpdateUI();
        });

        _endFrameField = rootVisualElement.Q<IntegerField>("end-time-field");
        _endFrameField.value = _endFrame;
        _endFrameField.RegisterCallback<FocusOutEvent>(_ =>
        {
            _endFrame = _endFrameField.value;
            SaveToALV();
            UpdateUI();
        });

        // --- Bake fields ---

        _snapshotCountField = rootVisualElement.Q<IntegerField>("snapshot-count-field");
        _snapshotCountField.value = _snapshotCount;
        _snapshotCountField.RegisterCallback<FocusOutEvent>(_ =>
        {
            _snapshotCount = Mathf.Max(_snapshotCountField.value, 2);
            _snapshotCountField.SetValueWithoutNotify(_snapshotCount);
            _previewSnapshot = Mathf.Clamp(_previewSnapshot, 0, _snapshotCount - 1);
            UpdatePreviewReadout();
            SaveToALV();
            UpdateUI();
        });

        _outputNameField = rootVisualElement.Q<TextField>("output-name-field");
        _outputNameField.value = _outputName;
        _outputNameField.RegisterValueChangedCallback(e =>
        {
            _outputName = e.newValue;
            SaveToALV();
        });

        _shModeField = rootVisualElement.Q<EnumField>("sh-mode-field");
        _modeHintL1 = rootVisualElement.Q<HelpBox>("mode-hint-L1");
        _modeHintMonoL1  = rootVisualElement.Q<HelpBox>("mode-hint-MonoL1");
        _modeHintMonoL0  = rootVisualElement.Q<HelpBox>("mode-hint-MonoL0");
        _shModeField.Init(_shMode);
        _shModeField.RegisterValueChangedCallback(e =>
        {
            _shMode = (ALVSHMode)e.newValue;
            SaveToALV();
            UpdateModeHint();
            UpdateOutputTextureEstimates();
        });

        _bitDepthField = rootVisualElement.Q<EnumField>("bit-depth-field");
        _formatHint8BPC  = rootVisualElement.Q<HelpBox>("format-hint-8bpc");
        _formatHint16BPC = rootVisualElement.Q<HelpBox>("format-hint-16bpc");
        _bitDepthField.Init(_bitDepth);
        _bitDepthField.RegisterValueChangedCallback(e =>
        {
            _bitDepth = (ALVBitDepth)e.newValue;
            SaveToALV();
            UpdateFormatHint();
            UpdateOutputTextureEstimates();
        });

        UpdateModeHint();
        UpdateFormatHint();

        // --- Preview controls ---

        _previewControls   = rootVisualElement.Q<VisualElement>("preview-controls");
        _previewSnapshotField = rootVisualElement.Q<IntegerField>("preview-snapshot-field");
        _previewSnapshotMax   = rootVisualElement.Q<Label>("preview-snapshot-max");
        _animFrameCounter  = rootVisualElement.Q<Label>("anim-frame-counter");

        _previewSnapshotField.RegisterValueChangedCallback(e =>
        {
            // Field is 1-indexed; clamp to valid range then push back if corrected.
            _previewSnapshot = Mathf.Clamp(e.newValue - 1, 0, _snapshotCount - 1);
            _previewSnapshotField.SetValueWithoutNotify(_previewSnapshot + 1);
            UpdatePreviewReadout();
        });

        rootVisualElement.Q<Button>("start-btn").clicked += () =>
        {
            _previewSnapshot = 0;
            UpdatePreviewReadout();
            AnimationWindowFrame = SnapshotToAnimFrame(_previewSnapshot);
        };

        rootVisualElement.Q<Button>("prev-btn").clicked += () =>
        {
            if (_previewSnapshot > 0) _previewSnapshot--;
            UpdatePreviewReadout();
            AnimationWindowFrame = SnapshotToAnimFrame(_previewSnapshot);
        };

        rootVisualElement.Q<Button>("next-btn").clicked += () =>
        {
            if (_previewSnapshot < _snapshotCount - 1) _previewSnapshot++;
            UpdatePreviewReadout();
            AnimationWindowFrame = SnapshotToAnimFrame(_previewSnapshot);
        };

        rootVisualElement.Q<Button>("end-btn").clicked += () =>
        {
            _previewSnapshot = _snapshotCount - 1;
            UpdatePreviewReadout();
            AnimationWindowFrame = SnapshotToAnimFrame(_previewSnapshot);
        };

        _previewSnapshotField.RegisterCallback<FocusOutEvent>(_ =>
            AnimationWindowFrame = SnapshotToAnimFrame(_previewSnapshot));

        // --- Status / bake buttons ---

        _validationBox   = rootVisualElement.Q<HelpBox>("validation-box");
        _bakeProgressBox = rootVisualElement.Q<HelpBox>("bake-progress-box");
        _bakeBtn         = rootVisualElement.Q<Button>("bake-btn");
        _cancelBtn       = rootVisualElement.Q<Button>("cancel-btn");

        _bakeBtn.clicked   += StartBake;
        _cancelBtn.clicked += () => AbortBake("Cancelled by user");

        // --- Volume-dependant section ---
        _showWithVolume = rootVisualElement.Q<VisualElement>("show-with-volume");
        _hideWithVolume = rootVisualElement.Q<VisualElement>("hide-with-volume");

        // --- Output estimate labels ---
        _animFrameInterval = rootVisualElement.Q<Label>("anim-frame-interval");
        _outputResLabel    = rootVisualElement.Q<Label>("output-res");
        _vramSizeLabel     = rootVisualElement.Q<Label>("vram-size");
        _bundleSizeLabel   = rootVisualElement.Q<Label>("estimated-bundle-size");

        UpdatePreviewReadout();
        UpdateUI();
    }

    // Copies bake settings from the ALV component on the target volume's GameObject, if present.
    void LoadFromALV()
    {
        AnimatedLightVolume alv = ALVOnVolume;
        if (alv == null) return;
        _animator      = alv.BakeAnimator;
        _animClip      = alv.BakeClip;
        _snapshotCount   = alv.BakeSnapshotCount;
        _startFrame    = alv.BakeStartFrame;
        _endFrame      = alv.BakeEndFrame;
        _shMode    = alv.BakeSHMode;
        _bitDepth  = alv.BakeBitDepth;
        _outputName = alv.BakeOutputName;

        _animatorField?.SetValueWithoutNotify(_animator);
        _clipField?.SetValueWithoutNotify(_animClip);
        _snapshotCountField?.SetValueWithoutNotify(_snapshotCount);
        _outputNameField?.SetValueWithoutNotify(_outputName);
        _shModeField?.SetValueWithoutNotify(_shMode);
        _bitDepthField?.SetValueWithoutNotify(_bitDepth);

        UpdateModeHint();
        UpdateFormatHint();
        UpdateRangeFields();
        UpdatePreviewReadout();
    }

    // Writes current window fields back to the ALV component on the target volume's GameObject, if present.
    void SaveToALV()
    {
        AnimatedLightVolume alv = ALVOnVolume;
        if (alv == null) return;
        alv.BakeAnimator    = _animator;
        alv.BakeClip        = _animClip;
        alv.BakeSnapshotCount = _snapshotCount;
        alv.BakeStartFrame  = _startFrame;
        alv.BakeEndFrame    = _endFrame;
        alv.BakeSHMode      = _shMode;
        alv.BakeBitDepth    = _bitDepth;
        alv.BakeOutputName  = _outputName;
        EditorUtility.SetDirty(alv);
    }

    void UpdateModeHint()
    {
        if (_modeHintL1 == null || _modeHintMonoL1 == null || _modeHintMonoL0 == null) return;

        switch (_shMode)
        {
            case ALVSHMode.MonoL1:
                _modeHintL1.style.display = DisplayStyle.None;
                _modeHintMonoL1.style.display = DisplayStyle.Flex;
                _modeHintMonoL0.style.display = DisplayStyle.None;
                break;

            case ALVSHMode.MonoL0:
                _modeHintL1.style.display = DisplayStyle.None;
                _modeHintMonoL1.style.display = DisplayStyle.None;
                _modeHintMonoL0.style.display = DisplayStyle.Flex;
                break;

            // case ALVSHMode.L1
            default:
                _modeHintL1.style.display = DisplayStyle.Flex;
                _modeHintMonoL1.style.display = DisplayStyle.None;
                _modeHintMonoL0.style.display = DisplayStyle.None;
                break;
        }
    }

    void UpdateFormatHint()
    {
        if (_formatHint16BPC == null) return;
        bool is8bit = _bitDepth == ALVBitDepth.Depth8;
        _formatHint8BPC.style.display  = is8bit ? DisplayStyle.Flex : DisplayStyle.None;
        _formatHint16BPC.style.display = is8bit ? DisplayStyle.None : DisplayStyle.Flex;
    }

    // Resets start/end frame fields to defaults when the clip changes.
    void UpdateRangeFields()
    {
        _startFrameField?.SetValueWithoutNotify(_startFrame);
        _endFrameField?.SetValueWithoutNotify(_endFrame);
    }

    // Refreshes the preview snapshot field and anim-frame-counter label.
    void UpdatePreviewReadout()
    {
        if (_previewSnapshotField == null) return;

        bool canPreview = _animClip != null && _snapshotCount >= 2;
        _previewControls?.SetEnabled(canPreview);

        _previewSnapshotField.SetValueWithoutNotify(_previewSnapshot + 1);
        _previewSnapshotMax.text  = $"/ {_snapshotCount}";
        _animFrameCounter.text = canPreview ? $"f{SnapshotToAnimFrame(_previewSnapshot)}" : "—";
    }

    // Refreshes validation message and bake/cancel button visibility.
    void UpdateUI()
    {
        if (_bakeBtn == null) return;

        // Re-read the field in case the referenced object was deleted or the scene changed,
        // since the callback won't fire in those cases.
        _targetVolume = _volumeField?.value as LightVolume;

        _showWithVolume.style.display = _targetVolume != null ? DisplayStyle.Flex : DisplayStyle.None;
        _hideWithVolume.style.display = _targetVolume != null ? DisplayStyle.None : DisplayStyle.Flex;

        string error = Validate();

        // Validation box: only shown when there's an error and not currently baking
        bool showError = error != null && !_baking;
        _validationBox.text = error ?? "";
        _validationBox.style.display = showError ? DisplayStyle.Flex : DisplayStyle.None;

        // Progress box: only shown while baking
        _bakeProgressBox.style.display = _baking ? DisplayStyle.Flex : DisplayStyle.None;
        if (_baking)
        {
            int remaining = _snapshotCount - _currentSnapshot;
            string etr = _secsPerSnapshotBake >= 0
                ? $"\n(~{System.TimeSpan.FromSeconds(_secsPerSnapshotBake * (remaining + 2)):m\\:ss} remaining)"
                : "";
            _bakeProgressBox.text = $"Baking snapshot {_currentSnapshot + 1} / {_snapshotCount}…{etr}";
        }

        _bakeBtn.style.display   = _baking ? DisplayStyle.None : DisplayStyle.Flex;
        _cancelBtn.style.display = _baking ? DisplayStyle.Flex : DisplayStyle.None;
        _bakeBtn.SetEnabled(error == null);

        UpdateOutputAnimationEstimates();
        UpdateOutputTextureEstimates();
    }

    void UpdateOutputAnimationEstimates()
    {
        if (_animator == null || _animClip == null)
        {
            _animFrameInterval.text = "—";
            return;
        }

        // Display number of animation frames in between snapshots.
        // Subtract one from _snapshotCount since we capture both the first and last frame.
        float frameInterval = Mathf.Round(BakeDuration * _animClip.frameRate) / (_snapshotCount - 1);
        _animFrameInterval.text = $"f{frameInterval:0.#}";
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

        // Resolution uses the volume's voxel grid.
        Vector3Int res = _targetVolume.Resolution;
        int w = res.x;
        int h = res.y;
        int d = res.z;

        int packedH = ALVFormat.PackedHeight(h, _snapshotCount);
        int packedD = ALVFormat.PackedDepth(d, _shMode);
        _outputResLabel.text = $"{w} × {packedH} × {packedD}";

        double vram = ALVFormat.VramMB(w, h, d, _snapshotCount, _shMode, _bitDepth);

        // Per-format bundle size range derived from noise (worst-case upper bound) and
        // Gaussian-blob (realistic lower bound) AssetBundle compression tests.
        // See ALV-BUNDLE-SIZE.md at the repo root for methodology and full data.
        double bundleLow  = vram * 0.5;
        double bundleHigh = vram * (_shMode == ALVSHMode.MonoL0 ? 0.7 : 0.9);

        _vramSizeLabel.text   = $"{vram:0.00} MB";
        _bundleSizeLabel.text = $"{bundleLow:0.00} – {bundleHigh:0.00} MB";
    }

    // --- Validation -----------------------------------------------------
    string Validate()
    {
        if (_animator == null)     return "Assign an Animator to bake.";
        if (_animClip == null)     return "Assign an Animation Clip to bake.";
        if (_targetVolume.BakeryVolume == null)
            return "Target Light Volume has no BakeryVolume child. Run a regular Bakery bake on it first to generate one.";
        return null;
    }

    // --- Bake loop ------------------------------------------------------
    void StartBake()
    {
        _baking = true;
        _currentSnapshot = 0;
        _secsPerSnapshotBake = -1;
        _collectedSnapshots.Clear();

        // Cache hierarchy paths now while references are guaranteed live.
        _animatorPath     = ALVEditorUtils.GetHierarchyPath(_animator.gameObject);
        _targetVolumePath = ALVEditorUtils.GetHierarchyPath(_targetVolume.gameObject);

        UpdateUI();
        BakeNextSnapshot();
    }

    bool RefreshReferences()
    {
        Animator animator = ALVEditorUtils.FindByPath<Animator>(_animatorPath);
        if (animator == null) { AbortBake("Animator lost after scene reload!"); return false; }

        LightVolume volume = ALVEditorUtils.FindByPath<LightVolume>(_targetVolumePath);
        if (volume == null) { AbortBake("Target LightVolume lost after scene reload!"); return false; }

        _animator     = animator;
        _targetVolume = volume;

        // Keep the UI fields in sync so they don't show "Missing" after Bakery's scene reload.
        _animatorField?.SetValueWithoutNotify(_animator);
        _volumeField?.SetValueWithoutNotify(_targetVolume);
        return true;
    }

    void BakeNextSnapshot()
    {
        if (!RefreshReferences()) return;

        // Snapshot 0 = BakeStart, snapshot N-1 = BakeEnd (last snapshot inclusive).
        float t = BakeStart + (_snapshotCount > 1 ? BakeDuration * _currentSnapshot / (_snapshotCount - 1) : 0f);

        // Force-evaluate the animator to the target time.
        _animator.Play(_animClip.name, 0, t / _animClip.length);
        _animator.Update(0f);

        // Trigger a full Bakery bake. OnBakeFinished fires when it completes.
        _snapshotStopwatch.Restart();
        EditorWindow.GetWindow<ftRenderLightmap>().RenderButton(showMsgWindows: false);

        UpdateUI();
    }

    void OnBakeFinished(object sender, System.EventArgs e)
    {
        if (!_baking) return;

        _snapshotStopwatch.Stop();
        if (_secsPerSnapshotBake < 0)
            _secsPerSnapshotBake = _snapshotStopwatch.Elapsed.TotalSeconds;

        BakeryVolume bv = _targetVolume.BakeryVolume;
        if (bv.bakedTexture0 == null || bv.bakedTexture1 == null || bv.bakedTexture2 == null)
        {
            AbortBake($"BakeryVolume textures are null after bake on snapshot {_currentSnapshot}. Check Bakery output.");
            return;
        }

        _collectedSnapshots.Add(ALVTextureWriter.DeringSnapshot(
            bv.bakedTexture0.GetPixels(),
            bv.bakedTexture1.GetPixels(),
            bv.bakedTexture2.GetPixels()));

        _currentSnapshot++;

        if (_currentSnapshot < _snapshotCount)
            BakeNextSnapshot();
        else
            FinishBake();

        UpdateUI();
    }

    void FinishBake()
    {
        _baking = false;

        BakeryVolume bv = _targetVolume.BakeryVolume;
        int w = bv.bakedTexture0.width;
        int h = bv.bakedTexture0.height;
        int d = bv.bakedTexture0.depth;

        string assetDir = ALVEditorUtils.SceneAssetDir();
        ALVEditorUtils.CreateDirectory(assetDir);

        string assetPath = $"{assetDir}/{_outputName}.asset";
        ALVTextureWriter.SavePackedTexture(_collectedSnapshots.ToArray(), w, h, d, assetPath, _shMode, _bitDepth);

        new ALVTextureInfo { snapshotX = w, snapshotY = h, snapshotZ = d, numSnapshots = _snapshotCount, shMode = _shMode, bitDepth = _bitDepth }.Save(assetPath);

        // Reload references to make it faster to re-bake a sequence if needed.
        RefreshReferences();

        Debug.Log($"  [ALVBakeTexture] Done. {_snapshotCount} snapshots baked into {assetPath} (snapshotX={w} snapshotY={h} snapshotZ={d})");
        UpdateUI();
    }

    void AbortBake(string reason)
    {
        _baking = false;
        Debug.LogError($"  [ALVBakeTexture] Bake aborted: {reason}");
        UpdateUI();
    }
}
#endif
