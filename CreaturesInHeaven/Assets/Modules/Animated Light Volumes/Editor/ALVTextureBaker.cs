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
//   2. Triggering a Bakery bake for each sampled frame.
//   3. Reading BakeryVolume.bakedTexture0/1/2 after each bake
//   4. Packing all bakes into a Texture3D via ALVTextureWriter.SavePackedTexture
//
// Open via Tools > Lighting > Bake ALV Texture
#if BAKERY_INCLUDED
public class ALVTextureBaker : EditorWindow
{
    // --- Fields serialised in the window --------------------------------

    LightVolume _targetVolume;
    Animator _animator;
    AnimationClip _animClip;
    int _startFrame = 0;
    int _endFrame = -1; // -1 = use last frame of clip
    int _sampleCount = 8;
    ALVSHMode   _shMode   = ALVSHMode.MonoL1;
    ALVBitDepth _bitDepth = ALVBitDepth.Depth8;
    string _outputName = "ALV_Bake";

    // --- Internal bake state --------------------------------------------

    bool _baking = false;
    int _currentSample = 0;
    readonly List<ALVTextureWriter.SampleSH> _collectedSamples = new();

    // Hierarchy paths resolved at bake start, used to re-find objects each frame
    // in case Bakery's scene management destroys the live references mid-bake.
    string _animatorPath;
    string _targetVolumePath;

    // Stopwatch runs for each bake; first completed bake sets _secsPerSampleBake for time estimate.
    readonly System.Diagnostics.Stopwatch _frameStopwatch = new();
    double _secsPerSampleBake = -1;

    // 0-based index of the currently previewed animation frame for baking.
    int _previewFrame = 0;

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

    // Returns the animation window frame index for a given bake sample index (0-based).
    int SampleToAnimFrame(int bakeFrame)
    {
        if (_animClip == null) return 0;
        float t = BakeStart + ((_sampleCount > 1) ? BakeDuration * bakeFrame / (_sampleCount - 1) : 0f);
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
    IntegerField _sampleCountField;
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
    IntegerField _previewFrameField;
    Label _previewFrameMax;
    Label _animFrameCounter;
    Label _animFrameInterval;
    VisualElement _previewControls;
    Label _outputResLabel;
    Label _vramSizeLabel;
    Label _bundleSizeLabel;

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
            _previewFrame = 0;
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

        _sampleCountField = rootVisualElement.Q<IntegerField>("sample-count-field");
        _sampleCountField.value = _sampleCount;
        _sampleCountField.RegisterCallback<FocusOutEvent>(_ =>
        {
            _sampleCount = Mathf.Max(_sampleCountField.value, 2);
            _sampleCountField.SetValueWithoutNotify(_sampleCount);
            _previewFrame = Mathf.Clamp(_previewFrame, 0, _sampleCount - 1);
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
        _previewFrameField = rootVisualElement.Q<IntegerField>("preview-frame-field");
        _previewFrameMax   = rootVisualElement.Q<Label>("preview-frame-max");
        _animFrameCounter  = rootVisualElement.Q<Label>("anim-frame-counter");

        _previewFrameField.RegisterValueChangedCallback(e =>
        {
            // Field is 1-indexed; clamp to valid range then push back if corrected.
            _previewFrame = Mathf.Clamp(e.newValue - 1, 0, _sampleCount - 1);
            _previewFrameField.SetValueWithoutNotify(_previewFrame + 1);
            UpdatePreviewReadout();
        });

        rootVisualElement.Q<Button>("start-btn").clicked += () =>
        {
            _previewFrame = 0;
            UpdatePreviewReadout();
            AnimationWindowFrame = SampleToAnimFrame(_previewFrame);
        };

        rootVisualElement.Q<Button>("prev-btn").clicked += () =>
        {
            if (_previewFrame > 0) _previewFrame--;
            UpdatePreviewReadout();
            AnimationWindowFrame = SampleToAnimFrame(_previewFrame);
        };

        rootVisualElement.Q<Button>("next-btn").clicked += () =>
        {
            if (_previewFrame < _sampleCount - 1) _previewFrame++;
            UpdatePreviewReadout();
            AnimationWindowFrame = SampleToAnimFrame(_previewFrame);
        };

        rootVisualElement.Q<Button>("end-btn").clicked += () =>
        {
            _previewFrame = _sampleCount - 1;
            UpdatePreviewReadout();
            AnimationWindowFrame = SampleToAnimFrame(_previewFrame);
        };

        _previewFrameField.RegisterCallback<FocusOutEvent>(_ =>
            AnimationWindowFrame = SampleToAnimFrame(_previewFrame));

        // --- Status / bake buttons ---

        _validationBox   = rootVisualElement.Q<HelpBox>("validation-box");
        _bakeProgressBox = rootVisualElement.Q<HelpBox>("bake-progress-box");
        _bakeBtn         = rootVisualElement.Q<Button>("bake-btn");
        _cancelBtn       = rootVisualElement.Q<Button>("cancel-btn");

        _bakeBtn.clicked   += StartBake;
        _cancelBtn.clicked += () => AbortBake("Cancelled by user");

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
        _sampleCount   = alv.BakeSampleCount;
        _startFrame    = alv.BakeStartFrame;
        _endFrame      = alv.BakeEndFrame;
        _shMode    = alv.BakeSHMode;
        _bitDepth  = alv.BakeBitDepth;
        _outputName = alv.BakeOutputName;

        _animatorField?.SetValueWithoutNotify(_animator);
        _clipField?.SetValueWithoutNotify(_animClip);
        _sampleCountField?.SetValueWithoutNotify(_sampleCount);
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
        alv.BakeSampleCount = _sampleCount;
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

    // Refreshes the preview frame field and anim-frame-counter label.
    void UpdatePreviewReadout()
    {
        if (_previewFrameField == null) return;

        bool canPreview = _animClip != null && _sampleCount >= 2;
        _previewControls?.SetEnabled(canPreview);

        _previewFrameField.SetValueWithoutNotify(_previewFrame + 1);
        _previewFrameMax.text  = $"/ {_sampleCount}";
        _animFrameCounter.text = canPreview ? $"f{SampleToAnimFrame(_previewFrame)}" : "—";
    }

    // Refreshes validation message and bake/cancel button visibility.
    void UpdateUI()
    {
        if (_bakeBtn == null) return;

        string error = Validate();

        // Validation box: only shown when there's an error and not currently baking
        bool showError = error != null && !_baking;
        _validationBox.text = error ?? "";
        _validationBox.style.display = showError ? DisplayStyle.Flex : DisplayStyle.None;

        // Progress box: only shown while baking
        _bakeProgressBox.style.display = _baking ? DisplayStyle.Flex : DisplayStyle.None;
        if (_baking)
        {
            int remaining = _sampleCount - _currentSample;
            string etr = _secsPerSampleBake >= 0
                ? $"\n(~{System.TimeSpan.FromSeconds(_secsPerSampleBake * (remaining + 2)):m\\:ss} remaining)"
                : "";
            _bakeProgressBox.text = $"Baking sample {_currentSample + 1} / {_sampleCount}…{etr}";
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

        // Display number of animation frames in between baked frames.
        // Subtract one from _sampleCount since we're accounting for sampling both the first and last frame.
        float frameInterval = Mathf.Round(BakeDuration * _animClip.frameRate) / (_sampleCount - 1);
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

        // Packed texture dimensions: X unchanged, Y stacked by numFrames, Z = depth * numSlots.
        int numSlots = ALVFormat.NumSlots(_shMode);
        
        int packedH = h * _sampleCount;
        int packedD = d * numSlots;
        _outputResLabel.text = $"{w} × {packedH} × {packedD}";

        int bytesPerTexel = ALVTextureWriter.BytesPerTexel(_shMode, _bitDepth);
        long voxels = (long)w * h * d;
        double vram = voxels * _sampleCount * (double)numSlots * bytesPerTexel / (1024.0 * 1024.0);

        // Per-format bundle size range derived from noise (worst-case upper bound) and
        // Gaussian-blob (realistic lower bound) AssetBundle compression tests.
        // See ALV-BUNDLE-SIZE.md at the repo root for methodology and full data.
        double bundleLow = vram * 0.5;
        double bundleHigh = vram * (_shMode == ALVSHMode.MonoL0 ? 0.7 : 0.9);

        _vramSizeLabel.text   = $"{vram:0.00} MB";
        _bundleSizeLabel.text = $"{bundleLow:0.00} – {bundleHigh:0.00} MB";
    }

    // --- Validation -----------------------------------------------------
    string Validate()
    {
        if (_targetVolume == null) return "Assign a target Light Volume to bake.";
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
        _currentSample = 0;
        _secsPerSampleBake = -1;
        _collectedSamples.Clear();

        // Cache hierarchy paths now while references are guaranteed live.
        _animatorPath     = ALVEditorUtils.GetHierarchyPath(_animator.gameObject);
        _targetVolumePath = ALVEditorUtils.GetHierarchyPath(_targetVolume.gameObject);

        UpdateUI();
        BakeNextFrame();
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

    void BakeNextFrame()
    {
        if (!RefreshReferences()) return;

        // Frame 0 = BakeStart, frame N-1 = BakeEnd (last frame inclusive).
        float t = BakeStart + (_sampleCount > 1 ? BakeDuration * _currentSample / (_sampleCount - 1) : 0f);

        // Force-evaluate the animator to the target time.
        _animator.Play(_animClip.name, 0, t / _animClip.length);
        _animator.Update(0f);

        // Trigger a full Bakery bake. OnBakeFinished fires when it completes.
        _frameStopwatch.Restart();
        EditorWindow.GetWindow<ftRenderLightmap>().RenderButton(showMsgWindows: false);

        UpdateUI();
    }

    void OnBakeFinished(object sender, System.EventArgs e)
    {
        if (!_baking) return;

        _frameStopwatch.Stop();
        if (_secsPerSampleBake < 0)
            _secsPerSampleBake = _frameStopwatch.Elapsed.TotalSeconds;

        BakeryVolume bv = _targetVolume.BakeryVolume;
        if (bv.bakedTexture0 == null || bv.bakedTexture1 == null || bv.bakedTexture2 == null)
        {
            AbortBake($"BakeryVolume textures are null after bake on frame {_currentSample}. Check Bakery output.");
            return;
        }

        _collectedSamples.Add(ALVTextureWriter.DeringSample(
            bv.bakedTexture0.GetPixels(),
            bv.bakedTexture1.GetPixels(),
            bv.bakedTexture2.GetPixels()));

        _currentSample++;

        if (_currentSample < _sampleCount)
            BakeNextFrame();
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
        ALVTextureWriter.SavePackedTexture(_collectedSamples.ToArray(), w, h, d, assetPath, _shMode, _bitDepth);

        new ALVTextureInfo { sampleX = w, sampleY = h, sampleZ = d, numSamples = _sampleCount, shMode = _shMode, bitDepth = _bitDepth }.Save(assetPath);

        // Reload references to make it faster to re-bake a sequence if needed.
        RefreshReferences();

        Debug.Log($"  [ALVBakeTexture] Done. {_sampleCount} samples baked into {assetPath} (sampleX={w} sampleY={h} sampleZ={d})");
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
