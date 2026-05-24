using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using VRCLightVolumes;

// Bakes snapshots into an ALV atlas that was previously created by MomentEWinSetup.
//
// This window does not initialise atlases or create sidecars, as that is solely the Setup window's job.
// It reads the bake params off the MomentAnimatedLightVolume component and the sidecar JSON, then:
//   1. Force-evaluates the Animator to each queued snapshot's time via AnimationMode.SampleAnimationClip.
//   2. Triggers a Bakery bake.
//   3. Reads BakeryVolume.bakedTexture0/1/2 and writes the slice into the existing atlas.
//   4. Updates the sidecar's per-snapshot baked flag.
//
// If the current ALV setup params don't match the atlas/sidecar on disk, the bake controls are hidden
// and a banner directs the user to (re-)open the Setup window. Setup is the only place that wipes data.
//
// Opens via Tools > Moment ALV > Bake animated Light Volume...
//
#if BAKERY_INCLUDED
public class MomentEWinBaker : EditorWindow
{
    // --- Window state ---------------------------------------------------

    LightVolume _targetVolume;

    // Bake params read from the ALV component on volume select. Read-only here; Setup window owns writes.
    MomentBakeParams _params;
    MomentALVSHMode _shMode;
    MomentALVBitDepth _bitDepth;
    string _outputName;

    // True when the current ALV is set up and ready to accept bakes.
    // When false, the bake/queue UI is hidden and the mismatch banner is shown instead.
    bool _readyToBake;

    // --- Bake session state ---------------------------------------------

    bool _baking = false;

    // Ordered list of snapshot indices (0-based) to bake in the current session.
    int[] _snapshotQueue;
    int _queuePosition;

    // Asset path and sidecar resolved at bake start; used by OnBakeFinished to write each slice.
    string _assetPath;
    MomentTextureInfo _activeSidecar;

    // Last-known flipbook state loaded from disk. Updated when the volume changes and after each completed bake.
    MomentTextureInfo _flipbookState;

    // Set whenever flipbook state or bake progress changes; cleared after UpdateFlipbookTimeline runs.
    bool _timelineDirty = true;

    // Snapshot indices staged for the next bake. Mutated by the queue add/remove buttons;
    // consumed (and cleared) when StartBake fires.
    readonly HashSet<int> _stagedQueue = new();

    // Hierarchy paths resolved at bake start, used to re-find objects across snapshots in case
    // Bakery's scene management destroys the live references mid-bake.
    string _animatorPath;
    string _targetVolumePath;
    Animator _animator;

    readonly MomentBakeTimer _bakeTimer = new();
    readonly MomentBakeEstimator _bakeEstimator = new();

    // Cached before bake starts; used in place of live scene-object references while Bakery's temp scene is active.
    string _cachedAnimatorName;

    // Last anim-window frame seen during polling; used to detect external scrub changes.
    int _lastAnimWindowFrame = -1;

    // --- Animation window (reflection) ----------------------------------
    // The Animation window has no public API for setting the current frame, so it's accessed via reflection.

    EditorWindow _animationWindow;
    PropertyInfo _animWindowFrameProp;

    int AnimationWindowFrame
    {
        get
        {
            if (_animationWindow == null || _animWindowFrameProp == null) FindAnimationWindow();
            return _animWindowFrameProp?.GetValue(_animationWindow) is int f ? f : -1;
        }
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
    void OnInspectorUpdate()
    {
        UpdateUI();
        PollAnimWindowForFocus();
    }

    // --- UI -------------------------------------------------------------

    ObjectField _volumeField;
    VisualElement _showWithVolume;
    VisualElement _hideWithVolume;
    VisualElement _mismatchBlock;
    HelpBox _mismatchBox;
    Button _openSetupBtn;
    Label _summaryAnimator, _summaryClip, _summarySnapshots, _summaryRange, _summaryLighting, _summaryOutput;
    VisualElement _bakeBlock;
    VisualElement _bakeActionBlock;
    VisualElement _flipbookBakeEstimates;
    HelpBox _validationBox;
    HelpBox _bakeProgressBox;
    Button _bakeBtn;
    Button _cancelBtn;
    MomentFlipbookTimeline _flipbookTimeline;
    Label _flipbookStatusSelected;
    Label _flipbookStatusFocus;
    Label _flipbookStatusQueued;
    Label _flipbookAverageSnapshotBakeTime;
    Label _flipbookEstimatedTotalBakeTime;
    Button _snapshotQueueAddBtn;
    Button _snapshotQueueRemoveBtn;
    Button _snapshotClearBakeBtn;

    public void CreateGUI()
    {
        string dir = MomentAssetPaths.ScriptDir();

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

        // --- Target volume ---
        _volumeField = rootVisualElement.Q<ObjectField>("volume-field");
        _volumeField.objectType = typeof(LightVolume);
        _volumeField.value = _targetVolume;
        _volumeField.RegisterValueChangedCallback(e =>
        {
            _targetVolume = e.newValue as LightVolume;
            LoadParamsFromALV();
            LoadFlipbookState();
            UpdateUI();
        });

        // --- Mismatch / setup-link block ---
        _mismatchBlock = rootVisualElement.Q<VisualElement>("mismatch-block");
        _mismatchBox = rootVisualElement.Q<HelpBox>("mismatch-box");
        _openSetupBtn = rootVisualElement.Q<Button>("open-setup-btn");
        _openSetupBtn.clicked += () => MomentEWinSetup.OpenWithVolume(_targetVolume);

        // --- Setup summary labels ---
        _summaryAnimator = rootVisualElement.Q<Label>("summary-animator");
        _summaryClip = rootVisualElement.Q<Label>("summary-clip");
        _summarySnapshots = rootVisualElement.Q<Label>("summary-snapshots");
        _summaryRange = rootVisualElement.Q<Label>("summary-range");
        _summaryLighting = rootVisualElement.Q<Label>("summary-lighting");
        _summaryOutput = rootVisualElement.Q<Label>("summary-output");

        // --- Flipbook & queue ---
        _bakeBlock = rootVisualElement.Q<VisualElement>("bake-block");
        _flipbookTimeline = rootVisualElement.Q<MomentFlipbookTimeline>("flipbook-timeline");
        _flipbookStatusSelected = rootVisualElement.Q<Label>("flipbook-status-selected");
        _flipbookStatusFocus = rootVisualElement.Q<Label>("flipbook-status-focus");
        _flipbookStatusQueued = rootVisualElement.Q<Label>("flipbook-status-queued");
        _flipbookAverageSnapshotBakeTime = rootVisualElement.Q<Label>("flipbook-average-snapshot-bake-time");
        _flipbookEstimatedTotalBakeTime = rootVisualElement.Q<Label>("flipbook-estimated-total-bake-time");

        _flipbookTimeline.OnSelectionChanged += _ =>
        {
            UpdateQueueButtons();
            UpdateFlipbookStatusLabels();
            ScrubToLastClickedCell();
        };
        _flipbookTimeline.OnHoverChanged += _ => UpdateFlipbookStatusLabels();
        _flipbookTimeline.OnFocusChanged += _ => UpdateFlipbookStatusLabels();

        _snapshotQueueAddBtn = rootVisualElement.Q<Button>("snapshot-queue-add-btn");
        _snapshotQueueRemoveBtn = rootVisualElement.Q<Button>("snapshot-queue-remove-btn");

        _snapshotQueueAddBtn.clicked += () =>
        {
            foreach (int i in _flipbookTimeline.SelectedIndices) _stagedQueue.Add(i);
            _timelineDirty = true;
            UpdateUI();
        };

        _snapshotQueueRemoveBtn.clicked += () =>
        {
            foreach (int i in _flipbookTimeline.SelectedIndices) _stagedQueue.Remove(i);
            _timelineDirty = true;
            UpdateUI();
        };

        _snapshotClearBakeBtn = rootVisualElement.Q<Button>("snapshot-clear-bake-btn");
        _snapshotClearBakeBtn.clicked += ClearSelectedSnapshots;

        // --- Bake action block ---
        _bakeActionBlock = rootVisualElement.Q<VisualElement>("bake-action-block");
        _flipbookBakeEstimates = rootVisualElement.Q<VisualElement>("flipbook-bake-estimates");
        _validationBox = rootVisualElement.Q<HelpBox>("validation-box");
        _bakeProgressBox = rootVisualElement.Q<HelpBox>("bake-progress-box");
        _bakeBtn = rootVisualElement.Q<Button>("bake-btn");
        _cancelBtn = rootVisualElement.Q<Button>("cancel-btn");

        _bakeBtn.clicked += () => StartBake(_stagedQueue.OrderBy(i => i).ToArray());
        _cancelBtn.clicked += () => AbortBake("Cancelled by user");

        // --- Volume-dependant section ---
        _showWithVolume = rootVisualElement.Q<VisualElement>("show-with-volume");
        _hideWithVolume = rootVisualElement.Q<VisualElement>("hide-with-volume");

        UpdateUI();
    }

    // Copies bake params from the Moment component into the window's local state for use by the bake loop.
    // Doesn't write anything back, that's the Setup window's job.
    void LoadParamsFromALV()
    {
        MomentAnimatedLightVolume alv = MomentOnVolume;
        if (alv == null)
        {
            _params = default;
            _shMode = MomentALVSHMode.MonoL1;
            _bitDepth = MomentALVBitDepth.Depth8;
            _outputName = "";
            _animator = null;
            return;
        }
        _animator = alv.BakeAnimator;
        _params.Clip = alv.BakeClip;
        _params.SnapshotCount = alv.BakeSnapshotCount;
        _params.StartFrame = alv.BakeStartFrame;
        _params.EndFrame = alv.BakeEndFrame;
        _shMode = alv.BakeSHMode;
        _bitDepth = alv.BakeBitDepth;
        _outputName = alv.BakeOutputName;
    }

    // Loads the flipbook state from the sidecar for the current ALV's output name into _flipbookState.
    void LoadFlipbookState()
    {
        if (_targetVolume == null || string.IsNullOrEmpty(_outputName))
        {
            _flipbookState = null;
            _stagedQueue.Clear();
            _timelineDirty = true;
            return;
        }
        string assetDir = MomentAssetPaths.SceneAssetDir();
        string assetPath = $"{assetDir}/{_outputName}.asset";
        _flipbookState = MomentTextureInfo.Load(assetPath);
        _stagedQueue.RemoveWhere(i => i >= _params.SnapshotCount);
        _timelineDirty = true;
    }

    // Re-checks whether the ALV is in a runnable state. Sets _readyToBake and a user-facing reason.
    string EvaluateReadiness()
    {
        if (_targetVolume == null) return null; // The "Assign a Light Volume" branch handles this.

        MomentAnimatedLightVolume alv = MomentOnVolume;
        if (alv == null)
            return "This Light Volume has no MomentAnimatedLightVolume component. Open the Setup window to create one.";

        if (alv.AnimatedTexture == null)
            return "This ALV has no atlas yet. Open the Setup window to initialise it.";

        if (_flipbookState == null)
            return "Sidecar metadata is missing for this ALV. Open the Setup window to re-initialise.";

        Vector3Int res = _targetVolume.Resolution;
        if (!_flipbookState.MatchesParams(res.x, res.y, res.z, _params.SnapshotCount, _shMode, _bitDepth))
            return "Setup parameters have changed since the last bake. Open the Setup window to re-initialise the atlas.";

        // Bakery-side prerequisites (animator, clip, BakeryVolume). Same checks as before.
        string paramError = _params.Validate(_animator, _targetVolume);
        if (paramError != null) return paramError;

        return null;
    }

    void UpdateUI()
    {
        if (_bakeBtn == null) return;

        // During an active bake, Bakery moves the scene into a temp scene. The LightVolume object
        // ceases to exist and the ObjectField returns null. Skip the re-read so we keep the cached
        // state; during baking, _baking itself serves as the "volume is present" signal.
        if (!_baking)
            _targetVolume = _volumeField?.value as LightVolume;

        bool hasVolume = _baking || _targetVolume != null;
        _showWithVolume.style.display = hasVolume ? DisplayStyle.Flex : DisplayStyle.None;
        _hideWithVolume.style.display = hasVolume ? DisplayStyle.None : DisplayStyle.Flex;

        string notReadyReason = EvaluateReadiness();
        _readyToBake = notReadyReason == null;

        // Show the mismatch banner only when there's a Light Volume but it isn't bake-ready,
        // and we aren't mid-bake (in which case we trust the existing session to finish).
        bool showMismatch = hasVolume && !_readyToBake && !_baking;
        _mismatchBlock.style.display = showMismatch ? DisplayStyle.Flex : DisplayStyle.None;
        if (showMismatch) _mismatchBox.text = notReadyReason;

        // Setup summary is always visible: it shares the Light Volume panel and shows placeholder
        // values ("—") when no volume is selected or no ALV component is present.
        UpdateSummary();

        // Hide the bake/queue/action UI when not ready. Still show it during an in-progress bake so progress is visible.
        bool showBakeUi = hasVolume && (_readyToBake || _baking);
        _bakeBlock.style.display = showBakeUi ? DisplayStyle.Flex : DisplayStyle.None;
        _bakeActionBlock.style.display = showBakeUi ? DisplayStyle.Flex : DisplayStyle.None;
        if (_flipbookBakeEstimates != null)
            _flipbookBakeEstimates.style.display = _baking ? DisplayStyle.None : DisplayStyle.Flex;

        // Validation box (separate from the mismatch banner) only used for transient errors during a session.
        _validationBox.style.display = DisplayStyle.None;

        // Progress box: only shown while baking.
        _bakeProgressBox.style.display = _baking ? DisplayStyle.Flex : DisplayStyle.None;
        if (_baking)
        {
            int snapshotIndex = _snapshotQueue[_queuePosition];
            int snapshotsLeft = _snapshotQueue.Length - _queuePosition;
            float etaSeconds = _bakeEstimator.EstimateRemaining(snapshotsLeft);
            string etaLine = etaSeconds >= 0f
                ? $"\nEstimated time remaining: {FormatSeconds(etaSeconds)} ({_bakeEstimator.AverageSeconds:0.0}s average)"
                : "\nEstimating time remaining...";
            _bakeProgressBox.text = $"Baking snapshot {snapshotIndex + 1}\n{_queuePosition + 1} / {_snapshotQueue.Length} in queue{etaLine}";
        }

        _bakeBtn.style.display = _baking ? DisplayStyle.None : DisplayStyle.Flex;
        _cancelBtn.style.display = _baking ? DisplayStyle.Flex : DisplayStyle.None;
        _bakeBtn.SetEnabled(_readyToBake && _stagedQueue.Count > 0);

        UpdateFlipbookTimeline();
        UpdateQueueButtons();
        UpdateFlipbookStatusLabels();
    }

    void UpdateSummary()
    {
        // _animator is a scene object and may be null during Bakery's temp-scene bake; use the cached name.
        _summaryAnimator.text = _baking ? _cachedAnimatorName
            : (_animator != null ? _animator.gameObject.name : "—");
        _summaryClip.text = _params.Clip != null ? _params.Clip.name : "—";
        _summarySnapshots.text = _params.SnapshotCount.ToString();
        _summaryRange.text = $"{_params.StartFrame} – {(_params.EndFrame < 0 ? "end" : _params.EndFrame.ToString())}";
        _summaryLighting.text = $"{_shMode}, {_bitDepth}";
        _summaryOutput.text = string.IsNullOrEmpty(_outputName) ? "—" : _outputName;
    }

    void UpdateFlipbookTimeline()
    {
        if (_flipbookTimeline == null || !_timelineDirty) return;
        _timelineDirty = false;

        int count = _params.SnapshotCount;
        if (count <= 0)
        {
            _flipbookTimeline.UpdateStates(0, System.Array.Empty<MomentFlipbookTimeline.CellState>());
            return;
        }

        var states = new MomentFlipbookTimeline.CellState[count];

        for (int i = 0; i < count; i++)
        {
            bool baked = _flipbookState != null
                && _flipbookState.snapshots != null
                && i < _flipbookState.snapshots.Length
                && _flipbookState.snapshots[i].baked;

            FlipbookCellOverlay overlay = FlipbookCellOverlay.None;
            if (_baking && _snapshotQueue != null)
            {
                int qi = System.Array.IndexOf(_snapshotQueue, i);
                if (qi >= 0)
                    overlay = qi == _queuePosition ? FlipbookCellOverlay.Active : FlipbookCellOverlay.Queued;
            }
            else if (_stagedQueue.Contains(i))
            {
                overlay = FlipbookCellOverlay.Queued;
            }

            states[i] = new MomentFlipbookTimeline.CellState { Baked = baked, Overlay = overlay };
        }

        _flipbookTimeline.UpdateStates(count, states);
    }

    void UpdateFlipbookStatusLabels()
    {
        if (_flipbookStatusSelected == null) return;

        int selCount = _flipbookTimeline?.SelectedIndices.Count ?? 0;
        _flipbookStatusSelected.text = $"{selCount} snapshot(s) selected.";

        // Focus label: prefer hovered cell, fall back to focused cell, blank if neither.
        int displayIdx = _flipbookTimeline != null && _flipbookTimeline.HoveredIndex >= 0
            ? _flipbookTimeline.HoveredIndex
            : _flipbookTimeline?.FocusedIndex ?? -1;

        if (displayIdx >= 0 && _params.Clip != null)
        {
            int frame = _params.SnapshotToAnimFrame(displayIdx);

            string timeStr = "";
            if (_flipbookState?.snapshots != null && displayIdx < _flipbookState.snapshots.Length)
            {
                float t = _flipbookState.snapshots[displayIdx].renderSeconds;
                if (t >= 0f)
                    timeStr = $"  ·  Last bake {t:0.0}s";
            }

            _flipbookStatusFocus.text = $"Snapshot {displayIdx + 1}  ·  f{frame}{timeStr}";
        }
        else
        {
            _flipbookStatusFocus.text = "";
        }

        _flipbookStatusQueued.text = $"{_stagedQueue.Count}";

        // Average and estimated total bake time for the staged queue.
        float avgSeconds = -1f;
        if (_flipbookState?.snapshots != null && _stagedQueue.Count > 0)
        {
            float sum = 0f;
            int counted = 0;
            foreach (int i in _stagedQueue)
            {
                if (i < _flipbookState.snapshots.Length)
                {
                    float t = _flipbookState.snapshots[i].renderSeconds;
                    if (t >= 0f) { sum += t; counted++; }
                }
            }
            if (counted > 0) avgSeconds = sum / counted;
        }

        if (_flipbookAverageSnapshotBakeTime != null)
            _flipbookAverageSnapshotBakeTime.text = avgSeconds >= 0f ? FormatSeconds(avgSeconds) : "—";

        if (_flipbookEstimatedTotalBakeTime != null)
            _flipbookEstimatedTotalBakeTime.text = avgSeconds >= 0f ? FormatSeconds(avgSeconds * _stagedQueue.Count) : "—";
    }

    // Formats a duration in seconds as "#h #m 0.0s", omitting hours/minutes when zero.
    // e.g. 7383.0s → "2h 3m 3.0s", 65.0s → "1m 5.0s", 4.5s → "4.5s"
    static string FormatSeconds(float totalSeconds)
    {
        int h = (int)(totalSeconds / 3600f);
        int m = (int)((totalSeconds % 3600f) / 60f);
        float s = totalSeconds % 60f;
        if (h > 0) return $"{h}h {m}m {s:0.0}s";
        if (m > 0) return $"{m}m {s:0.0}s";
        return $"{s:0.0}s";
    }

    // Enables/disables the add/remove queue buttons based on whether the timeline has a selection.
    void UpdateQueueButtons()
    {
        if (_snapshotQueueAddBtn == null) return;
        bool hasSelection = _flipbookTimeline != null && _flipbookTimeline.SelectedIndices.Count > 0;
        _snapshotQueueAddBtn.SetEnabled(hasSelection && _readyToBake);
        _snapshotQueueRemoveBtn.SetEnabled(hasSelection);
        _snapshotClearBakeBtn.SetEnabled(hasSelection && !_baking && _readyToBake);
    }

    // Scrubs the animation window to the frame corresponding to the last-clicked timeline cell.
    void ScrubToLastClickedCell()
    {
        if (_flipbookTimeline == null || _params.Clip == null) return;
        int idx = _flipbookTimeline.LastClickedIndex;
        if (idx < 0) return;
        AnimationWindowFrame = _params.SnapshotToAnimFrame(idx);
        _lastAnimWindowFrame = AnimationWindowFrame;
    }

    // Called at ~10Hz. Detects when the animation window has been scrubbed externally
    // and moves keyboard focus to the nearest snapshot cell, if one maps to that frame.
    void PollAnimWindowForFocus()
    {
        if (_flipbookTimeline == null || _params.Clip == null) return;

        int frame = AnimationWindowFrame;
        if (frame == _lastAnimWindowFrame) return;
        _lastAnimWindowFrame = frame;

        int best = -1;
        int bestDist = int.MaxValue;
        for (int i = 0; i < _params.SnapshotCount; i++)
        {
            int dist = Mathf.Abs(_params.SnapshotToAnimFrame(i) - frame);
            if (dist < bestDist) { bestDist = dist; best = i; }
        }

        // Only focus if the frame is within half a snapshot interval of the nearest cell.
        float halfInterval = _params.SnapshotCount > 1
            ? Mathf.Round(_params.BakeDuration * _params.Clip.frameRate) / (_params.SnapshotCount - 1) * 0.5f
            : float.MaxValue;

        if (best >= 0 && bestDist <= halfInterval)
            _flipbookTimeline.FocusCell(best);
    }

    void ClearSelectedSnapshots()
    {
        if (_flipbookState == null || _flipbookState.snapshots == null) return;

        var indices = _flipbookTimeline.SelectedIndices;
        if (indices.Count == 0) return;

        var bakedSelected = new List<int>();
        foreach (int i in indices)
        {
            if (i < _flipbookState.snapshots.Length && _flipbookState.snapshots[i].baked)
                bakedSelected.Add(i);
        }

        if (bakedSelected.Count == 0) return;

        string list = string.Join(", ", bakedSelected.ConvertAll(i => $"#{i + 1}"));
        bool confirmed = EditorUtility.DisplayDialog(
            "Clear snapshots",
            $"Zero the baked data for snapshot{(bakedSelected.Count == 1 ? "" : "s")} {list}?\n\nThis cannot be undone.",
            "Clear", "Cancel");

        if (!confirmed) return;

        string assetDir = MomentAssetPaths.SceneAssetDir();
        string assetPath = $"{assetDir}/{_outputName}.asset";

        foreach (int i in bakedSelected)
        {
            MomentTextureWriter.ClearSnapshotSlice(
                assetPath, i,
                _flipbookState.snapshotX, _flipbookState.snapshotY, _flipbookState.snapshotZ,
                _flipbookState.shMode, _flipbookState.bitDepth);

            _flipbookState.snapshots[i] = new MomentTextureInfo.SnapshotEntry { baked = false };
        }

        _flipbookState.Save(assetPath);
        _timelineDirty = true;
        UpdateUI();

        Debug.Log($"[Moment] Cleared snapshot{(bakedSelected.Count == 1 ? "" : "s")} {list} in {assetPath}");
    }

    // --- Bake loop ------------------------------------------------------

    // Starts a bake session for the given snapshot indices (0-based, in order).
    // The atlas and sidecar must already exist and match current params. Setup window enforces that.
    void StartBake(int[] snapshotIndices)
    {
        if (!_readyToBake)
        {
            Debug.LogError("[Moment] Bake refused: ALV is not set up. Open the Setup window first.");
            return;
        }
        if (snapshotIndices.Length == 0) return;

        _baking = true;
        _snapshotQueue = snapshotIndices;
        _queuePosition = 0;
        _timelineDirty = true;
        _bakeEstimator.Reset();

        // Disable interaction with the flipbook timeline and the Light Volume field.
        _volumeField?.SetEnabled(false);
        if (_flipbookTimeline != null) _flipbookTimeline.Locked = true;

        // Cache everything we need to display the bake UI while the live scene objects are gone.
        _cachedAnimatorName = _animator != null ? _animator.gameObject.name : "—";

        // Cache hierarchy paths now while references are guaranteed live.
        _animatorPath = MomentSceneQuery.GetHierarchyPath(_animator.gameObject);
        _targetVolumePath = MomentSceneQuery.GetHierarchyPath(_targetVolume.gameObject);

        string assetDir = MomentAssetPaths.SceneAssetDir();
        _assetPath = $"{assetDir}/{_outputName}.asset";

        // Reuse the on-disk sidecar. Setup guarantees it matches.
        _activeSidecar = _flipbookState;

        Debug.Log($"[Moment] Baking {snapshotIndices.Length} snapshot{(snapshotIndices.Length == 1 ? "" : "s")} into {_assetPath}");

        UpdateUI();
        BakeNextSnapshot();
    }

    bool RefreshReferences()
    {
        Animator animator = MomentSceneQuery.FindByPath<Animator>(_animatorPath);
        if (animator == null) { AbortBake("Animator lost after scene reload!"); return false; }

        LightVolume volume = MomentSceneQuery.FindByPath<LightVolume>(_targetVolumePath);
        if (volume == null) { AbortBake("Target LightVolume lost after scene reload!"); return false; }

        _animator = animator;
        _targetVolume = volume;

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
        EditorApplication.update += RenderOnNextTick;
        void RenderOnNextTick()
        {
            EditorApplication.update -= RenderOnNextTick;
            if (!_baking) return;
            _bakeTimer.Start();
            GetWindow<ftRenderLightmap>().RenderButton(showMsgWindows: false);
        }

        UpdateUI();
    }

    void OnBakeFinished(object sender, System.EventArgs e)
    {
        if (!_baking) return;

        float renderSeconds = _bakeTimer.StopAndGetSeconds();
        _bakeEstimator.RecordSample(renderSeconds);
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
            baked = true,
            animFrame = _params.SnapshotToAnimFrame(snapshotIndex),
            renderSeconds = renderSeconds,
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
        if (_flipbookTimeline != null) _flipbookTimeline.Locked = false;
        _volumeField?.SetEnabled(true);
        _stagedQueue.Clear();
        _timelineDirty = true;
        if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();

        // Reload references to make it faster to re-bake a sequence if needed.
        RefreshReferences();

        Debug.Log($"[Moment] Done. {_snapshotQueue.Length} snapshot{(_snapshotQueue.Length == 1 ? "" : "s")} baked into {_assetPath}");
        UpdateUI();
    }

    void AbortBake(string reason)
    {
        _baking = false;
        if (_flipbookTimeline != null) _flipbookTimeline.Locked = false;
        _volumeField?.SetEnabled(true);
        _timelineDirty = true;
        if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
        Debug.LogError($"  [Moment] Bake aborted: {reason}");
        UpdateUI();
    }
}
#endif
