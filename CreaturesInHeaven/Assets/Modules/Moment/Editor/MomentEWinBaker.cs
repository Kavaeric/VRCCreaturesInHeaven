using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using VRCLightVolumes;

// Bakes a Moment texture by:
//   1. Initialising a blank atlas Texture3D asset and sidecar at bake start.
//   2. Force-evaluating an Animator to N evenly-spaced times across an AnimationClip.
//   3. Triggering a Bakery bake for each snapshot.
//   4. Reading BakeryVolume.bakedTexture0/1/2 after each bake and writing the slice
//      directly into the atlas, then updating the sidecar's per-snapshot baked flag.
//
// This means partial bakes are usable at runtime (unbaked slices remain black) and
// the bake can be resumed or selectively overwritten in a future session.
//
// Open via Tools > Lighting > Bake animated light volume...
#if BAKERY_INCLUDED
public class MomentEWinBaker : EditorWindow
{
    // --- Fields serialised in the window --------------------------------

    LightVolume _targetVolume;
    Animator _animator;
    MomentBakeParams _params = new MomentBakeParams { EndFrame = -1, SnapshotCount = 8 };
    MomentALVSHMode   _shMode   = MomentALVSHMode.MonoL1;
    MomentALVBitDepth _bitDepth = MomentALVBitDepth.Depth8;
    string _outputName = "ALV_Bake";

    // --- Internal bake state --------------------------------------------

    bool _baking = false;

    // Ordered list of snapshot indices (0-based) to bake in the current session.
    // Resolved in StartBake and consumed one entry at a time.
    int[] _snapshotQueue;
    int   _queuePosition;   // index into _snapshotQueue for the currently-baking snapshot

    // Asset path and sidecar resolved at bake start; used by OnBakeFinished to write each slice.
    string _assetPath;
    MomentTextureInfo _activeSidecar;

    // Last-known flipbook state loaded from disk. Updated at bake start and after each completed bake.
    // All UI and logic that wants to know "what has been baked so far" reads from this.
    MomentTextureInfo _flipbookState;

    // Set whenever flipbook state or bake progress changes; cleared after UpdateFlipbookTimeline runs.
    bool _timelineDirty = true;

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

    // Returns the MomentAnimatedLightVolume on the same GameObject as the target volume, if present.
    MomentAnimatedLightVolume MomentOnVolume =>
        _targetVolume != null ? _targetVolume.GetComponent<MomentAnimatedLightVolume>() : null;

    // --- Window lifecycle -----------------------------------------------

    [MenuItem("Tools/Moment ALV/Bake animated light volume...")]
    static void Open() => GetWindow<MomentEWinBaker>("Bake animated light volume");

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
    [SerializeField] AtelierHelpPage _shModeHelpPage;
    EnumField _bitDepthField;
    [SerializeField] AtelierHelpPage _bitDepthHelpPage;
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
    MomentFlipbookTimeline _flipbookTimeline;

    public void CreateGUI()
    {
        string dir = MomentAssetPaths.ScriptDir();

        // Set window icon
        string iconPath = $"{dir}/Resources/Icons/Icon Moment EWin Baker@2x.png";
        Texture2D windowIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
        titleContent = new GUIContent("Bake ALV", windowIcon);

        var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>($"{dir}/MomentEWinBaker.uxml");
        if (uxml == null)
        {
            rootVisualElement.Add(new Label($"MomentEWinBaker.uxml not found in {dir}."));
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
            LoadFlipbookState();
            UpdateRangeFields();
            UpdatePreviewReadout();
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
        _clipField.value = _params.Clip;
        _clipField.RegisterValueChangedCallback(e =>
        {
            _params.Clip = e.newValue as AnimationClip;
            _params.StartFrame = 0;
            _params.EndFrame = -1;
            _previewSnapshot = 0;
            UpdateRangeFields();
            UpdatePreviewReadout();
            SaveToALV();
            UpdateUI();
        });

        _startFrameField = rootVisualElement.Q<IntegerField>("start-time-field");
        _startFrameField.value = _params.StartFrame;
        _startFrameField.RegisterCallback<FocusOutEvent>(_ =>
        {
            _params.StartFrame = Mathf.Max(_startFrameField.value, 0);
            _startFrameField.SetValueWithoutNotify(_params.StartFrame);
            SaveToALV();
            UpdateUI();
        });

        _endFrameField = rootVisualElement.Q<IntegerField>("end-time-field");
        _endFrameField.value = _params.EndFrame;
        _endFrameField.RegisterCallback<FocusOutEvent>(_ =>
        {
            _params.EndFrame = _endFrameField.value;
            SaveToALV();
            UpdateUI();
        });

        // --- Bake fields ---

        _snapshotCountField = rootVisualElement.Q<IntegerField>("snapshot-count-field");
        _snapshotCountField.value = _params.SnapshotCount;
        _snapshotCountField.RegisterCallback<FocusOutEvent>(_ =>
        {
            _params.SnapshotCount = Mathf.Max(_snapshotCountField.value, 2);
            _snapshotCountField.SetValueWithoutNotify(_params.SnapshotCount);
            _previewSnapshot = Mathf.Clamp(_previewSnapshot, 0, _params.SnapshotCount - 1);
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
            LoadFlipbookState();
        });

        _shModeField = rootVisualElement.Q<EnumField>("sh-mode-field");
        _shModeField.Init(_shMode);

        rootVisualElement.Q<Button>("help-btn-sh-mode").clicked += () => AtelierHelpWindow.Open(_shModeHelpPage);
        _shModeField.RegisterValueChangedCallback(e =>
        {
            _shMode = (MomentALVSHMode)e.newValue;
            SaveToALV();
            UpdateOutputTextureEstimates();
        });

        _bitDepthField = rootVisualElement.Q<EnumField>("bit-depth-field");
        _bitDepthField.Init(_bitDepth);

        rootVisualElement.Q<Button>("help-btn-bit-depth").clicked += () => AtelierHelpWindow.Open(_bitDepthHelpPage);
        _bitDepthField.RegisterValueChangedCallback(e =>
        {
            _bitDepth = (MomentALVBitDepth)e.newValue;
            SaveToALV();
            UpdateOutputTextureEstimates();
        });

        // --- Preview controls ---

        _previewControls      = rootVisualElement.Q<VisualElement>("preview-controls");
        _previewSnapshotField = rootVisualElement.Q<IntegerField>("preview-snapshot-field");
        _previewSnapshotMax   = rootVisualElement.Q<Label>("preview-snapshot-max");
        _animFrameCounter     = rootVisualElement.Q<Label>("anim-frame-counter");

        _previewSnapshotField.RegisterValueChangedCallback(e =>
        {
            // Field is 1-indexed; clamp to valid range then push back if corrected.
            _previewSnapshot = Mathf.Clamp(e.newValue - 1, 0, _params.SnapshotCount - 1);
            _previewSnapshotField.SetValueWithoutNotify(_previewSnapshot + 1);
            UpdatePreviewReadout();
        });

        rootVisualElement.Q<Button>("start-btn").clicked += () =>
        {
            _previewSnapshot = 0;
            UpdatePreviewReadout();
            AnimationWindowFrame = _params.SnapshotToAnimFrame(_previewSnapshot);
        };

        rootVisualElement.Q<Button>("prev-btn").clicked += () =>
        {
            if (_previewSnapshot > 0) _previewSnapshot--;
            UpdatePreviewReadout();
            AnimationWindowFrame = _params.SnapshotToAnimFrame(_previewSnapshot);
        };

        rootVisualElement.Q<Button>("next-btn").clicked += () =>
        {
            if (_previewSnapshot < _params.SnapshotCount - 1) _previewSnapshot++;
            UpdatePreviewReadout();
            AnimationWindowFrame = _params.SnapshotToAnimFrame(_previewSnapshot);
        };

        rootVisualElement.Q<Button>("end-btn").clicked += () =>
        {
            _previewSnapshot = _params.SnapshotCount - 1;
            UpdatePreviewReadout();
            AnimationWindowFrame = _params.SnapshotToAnimFrame(_previewSnapshot);
        };

        _previewSnapshotField.RegisterCallback<FocusOutEvent>(_ =>
            AnimationWindowFrame = _params.SnapshotToAnimFrame(_previewSnapshot));

        // --- Status / bake buttons ---

        _validationBox   = rootVisualElement.Q<HelpBox>("validation-box");
        _bakeProgressBox = rootVisualElement.Q<HelpBox>("bake-progress-box");
        _bakeBtn         = rootVisualElement.Q<Button>("bake-btn");
        _cancelBtn       = rootVisualElement.Q<Button>("cancel-btn");

        _bakeBtn.clicked   += () => StartBake(AllSnapshotIndices());
        _cancelBtn.clicked += () => AbortBake("Cancelled by user");


        // --- Volume-dependant section ---
        _showWithVolume    = rootVisualElement.Q<VisualElement>("show-with-volume");
        _hideWithVolume    = rootVisualElement.Q<VisualElement>("hide-with-volume");
        _flipbookTimeline  = rootVisualElement.Q<MomentFlipbookTimeline>("flipbook-timeline");

        // --- Output estimate labels ---
        _animFrameInterval = rootVisualElement.Q<Label>("anim-frame-interval");
        _outputResLabel    = rootVisualElement.Q<Label>("output-res");
        _vramSizeLabel     = rootVisualElement.Q<Label>("vram-size");
        _bundleSizeLabel   = rootVisualElement.Q<Label>("estimated-bundle-size");

        UpdatePreviewReadout();
        UpdateUI();
    }

    // Copies bake settings from the Moment component on the target volume's GameObject, if present.
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
    }

    // Writes current window fields back to the Moment component on the target volume's GameObject, if present.
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

    // Resets start/end frame fields to match current params.
    void UpdateRangeFields()
    {
        _startFrameField?.SetValueWithoutNotify(_params.StartFrame);
        _endFrameField?.SetValueWithoutNotify(_params.EndFrame);
    }

    // Refreshes the preview snapshot field and anim-frame-counter label.
    void UpdatePreviewReadout()
    {
        if (_previewSnapshotField == null) return;

        bool canPreview = _params.Clip != null && _params.SnapshotCount >= 2;
        _previewControls?.SetEnabled(canPreview);

        _previewSnapshotField.SetValueWithoutNotify(_previewSnapshot + 1);
        _previewSnapshotMax.text = $"/ {_params.SnapshotCount}";
        _animFrameCounter.text   = canPreview ? $"f{_params.SnapshotToAnimFrame(_previewSnapshot)}" : "—";
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

        string error = _params.Validate(_animator, _targetVolume);

        // Validation box: only shown when there's an error and not currently baking
        bool showError = error != null && !_baking;
        _validationBox.text = error ?? "";
        _validationBox.style.display = showError ? DisplayStyle.Flex : DisplayStyle.None;

        // Progress box: only shown while baking
        _bakeProgressBox.style.display = _baking ? DisplayStyle.Flex : DisplayStyle.None;
        if (_baking)
        {
            int snapshotIndex = _snapshotQueue[_queuePosition];
            int remaining     = _snapshotQueue.Length - _queuePosition;
            string etr = _secsPerSnapshotBake >= 0
                ? $"\n(~{System.TimeSpan.FromSeconds(_secsPerSnapshotBake * (remaining + 2)):m\\:ss} remaining)"
                : "";
            _bakeProgressBox.text = $"Baking snapshot {snapshotIndex + 1} ({_queuePosition + 1} / {_snapshotQueue.Length} in queue)…{etr}";
        }

        _bakeBtn.style.display   = _baking ? DisplayStyle.None : DisplayStyle.Flex;
        _cancelBtn.style.display = _baking ? DisplayStyle.Flex : DisplayStyle.None;
        _bakeBtn.SetEnabled(error == null);

        UpdateOutputAnimationEstimates();
        UpdateOutputTextureEstimates();
        UpdateFlipbookTimeline();
    }

    void UpdateOutputAnimationEstimates()
    {
        if (_animator == null || _params.Clip == null)
        {
            _animFrameInterval.text = "—";
            return;
        }

        // Display number of animation frames in between snapshots.
        // Subtract one from SnapshotCount since we capture both the first and last frame.
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

        // Resolution uses the volume's voxel grid.
        Vector3Int res = _targetVolume.Resolution;
        int w = res.x;
        int h = res.y;
        int d = res.z;

        int packedH = MomentALVFormat.PackedHeight(h, _params.SnapshotCount);
        int packedD = MomentALVFormat.PackedDepth(d, _shMode);
        _outputResLabel.text = $"{w} × {packedH} × {packedD}";

        double vram = MomentALVFormat.VramMB(w, h, d, _params.SnapshotCount, _shMode, _bitDepth);

        // Per-format bundle size range. See MomentALVFormat for ratio constants and methodology.
        double bundleLow  = vram * MomentALVFormat.BundleRatioLow;
        double bundleHigh = vram * MomentALVFormat.BundleHighRatio(_shMode);

        _vramSizeLabel.text   = $"{vram:0.00} MB";
        _bundleSizeLabel.text = $"{bundleLow:0.00} – {bundleHigh:0.00} MB";
    }

    void UpdateFlipbookTimeline()
    {
        if (_flipbookTimeline == null || !_timelineDirty) return;
        _timelineDirty = false;

        int count = _params.SnapshotCount;
        var states = new FlipbookCellState[count];

        for (int i = 0; i < count; i++)
        {
            if (_baking && _snapshotQueue != null)
            {
                int qi = System.Array.IndexOf(_snapshotQueue, i);
                if (qi >= 0)
                {
                    states[i] = qi == _queuePosition ? FlipbookCellState.Active : FlipbookCellState.Queued;
                    continue;
                }
            }

            bool baked = _flipbookState != null
                && _flipbookState.snapshots != null
                && i < _flipbookState.snapshots.Length
                && _flipbookState.snapshots[i].baked;

            states[i] = baked ? FlipbookCellState.Baked : FlipbookCellState.Unbaked;
        }

        _flipbookTimeline.UpdateStates(count, states);
    }

    // --- Bake loop ------------------------------------------------------

    // Loads the flipbook state from the sidecar for the current output name into _flipbookState.
    // Called whenever the volume or output name changes, and after each bake completes.
    void LoadFlipbookState()
    {
        string assetDir = MomentAssetPaths.SceneAssetDir();
        string assetPath = $"{assetDir}/{_outputName}.asset";
        _flipbookState = MomentTextureInfo.Load(assetPath);
        _timelineDirty = true;
    }

    // Returns an ordered array of all snapshot indices for a full bake.
    int[] AllSnapshotIndices()
    {
        int[] indices = new int[_params.SnapshotCount];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;
        return indices;
    }

    // Starts a bake session for the given snapshot indices (0-based, in order).
    // Initialises or reuses the atlas depending on whether the existing sidecar matches current params.
    void StartBake(int[] snapshotIndices)
    {
        _baking        = true;
        _snapshotQueue = snapshotIndices;
        _queuePosition = 0;
        _timelineDirty = true;
        _secsPerSnapshotBake = -1;

        // Cache hierarchy paths now while references are guaranteed live.
        _animatorPath     = MomentSceneQuery.GetHierarchyPath(_animator.gameObject);
        _targetVolumePath = MomentSceneQuery.GetHierarchyPath(_targetVolume.gameObject);

        // Resolve dimensions from the volume's voxel grid — always available, unlike
        // BakeryVolume.bakedTexture0 which is only populated after the first Bakery bake.
        Vector3Int res = _targetVolume.Resolution;
        int w = res.x;
        int h = res.y;
        int d = res.z;

        string assetDir = MomentAssetPaths.SceneAssetDir();
        MomentAssetPaths.CreateDirectory(assetDir);
        _assetPath = $"{assetDir}/{_outputName}.asset";

        // If an existing atlas and sidecar are compatible with the current params, reuse them
        // so only the queued snapshots get overwritten. Otherwise, start fresh with a blank atlas.
        bool reusing = _flipbookState != null
            && _flipbookState.MatchesParams(w, h, d, _params.SnapshotCount, _shMode, _bitDepth);

        if (reusing)
        {
            _activeSidecar = _flipbookState;
            Debug.Log($"[Moment] Resuming existing bake at {_assetPath} ({snapshotIndices.Length} snapshots queued)");
        }
        else
        {
            MomentTextureWriter.InitialiseTexture(w, h, d, _params.SnapshotCount, _shMode, _bitDepth, _assetPath);
            _activeSidecar = new MomentTextureInfo
            {
                snapshotX    = w,
                snapshotY    = h,
                snapshotZ    = d,
                numSnapshots = _params.SnapshotCount,
                shMode       = _shMode,
                bitDepth     = _bitDepth,
                snapshots    = new MomentTextureInfo.SnapshotEntry[_params.SnapshotCount],
            };
            _activeSidecar.Save(_assetPath);
        }

        UpdateUI();
        BakeNextSnapshot();
    }

    bool RefreshReferences()
    {
        Animator animator = MomentSceneQuery.FindByPath<Animator>(_animatorPath);
        if (animator == null) { AbortBake("Animator lost after scene reload!"); return false; }

        LightVolume volume = MomentSceneQuery.FindByPath<LightVolume>(_targetVolumePath);
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

        int snapshotIndex = _snapshotQueue[_queuePosition];

        // Snapshot 0 = BakeStart, snapshot N-1 = BakeEnd (last snapshot inclusive).
        float t = _params.BakeStart + (_params.SnapshotCount > 1
            ? _params.BakeDuration * snapshotIndex / (_params.SnapshotCount - 1) : 0f);

        // Sample the clip directly via AnimationMode so the pose is written to the scene
        // regardless of whether the clip uses motion time or a standard playhead.
        if (!AnimationMode.InAnimationMode())
            AnimationMode.StartAnimationMode();
        AnimationMode.SampleAnimationClip(_animator.gameObject, _params.Clip, t);

        // Defer RenderButton by one editor update tick so Unity flushes the sampled
        // transforms to the scene graph before Bakery reads geometry.
        // EditorApplication.update fires regardless of window focus, unlike delayCall.
        _snapshotStopwatch.Restart();
        EditorApplication.update += RenderOnNextTick;
        void RenderOnNextTick()
        {
            EditorApplication.update -= RenderOnNextTick;
            if (!_baking) return;
            GetWindow<ftRenderLightmap>().RenderButton(showMsgWindows: false);
        }
        
        UpdateUI();
    }

    void OnBakeFinished(object sender, System.EventArgs e)
    {
        if (!_baking) return;

        _snapshotStopwatch.Stop();
        if (_secsPerSnapshotBake < 0)
            _secsPerSnapshotBake = _snapshotStopwatch.Elapsed.TotalSeconds;

        int snapshotIndex = _snapshotQueue[_queuePosition];

        BakeryVolume bv = _targetVolume.BakeryVolume;
        if (bv.bakedTexture0 == null || bv.bakedTexture1 == null || bv.bakedTexture2 == null)
        {
            AbortBake($"BakeryVolume textures are null after bake on snapshot {snapshotIndex}. Check Bakery output.");
            return;
        }

        MomentTextureWriter.SnapshotSH snapshot = MomentTextureWriter.DeringSnapshot(
            bv.bakedTexture0.GetPixels(),
            bv.bakedTexture1.GetPixels(),
            bv.bakedTexture2.GetPixels());

        MomentTextureWriter.WriteSnapshotSlice(
            _assetPath, snapshot, snapshotIndex,
            bv.bakedTexture0.width, bv.bakedTexture0.height, bv.bakedTexture0.depth,
            _shMode, _bitDepth);

        _activeSidecar.snapshots[snapshotIndex] = new MomentTextureInfo.SnapshotEntry
        {
            baked     = true,
            animFrame = _params.SnapshotToAnimFrame(snapshotIndex),
        };
        _activeSidecar.Save(_assetPath);
        _flipbookState = _activeSidecar;
        _timelineDirty = true;

        _queuePosition++;

        if (_queuePosition < _snapshotQueue.Length)
            BakeNextSnapshot();
        else
            FinishBake();

        UpdateUI();
    }

    void FinishBake()
    {
        _baking = false;
        if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();

        // Reload references to make it faster to re-bake a sequence if needed.
        RefreshReferences();

        Debug.Log($"[Moment] Done. {_params.SnapshotCount} snapshots baked into {_assetPath}");
        UpdateUI();
    }

    void AbortBake(string reason)
    {
        _baking        = false;
        _timelineDirty = true;
        if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
        Debug.LogError($"  [Moment] Bake aborted: {reason}");
        UpdateUI();
    }
}
#endif
