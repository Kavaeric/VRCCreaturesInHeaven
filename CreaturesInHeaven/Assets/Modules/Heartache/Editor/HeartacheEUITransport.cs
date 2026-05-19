using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

// Editor window: displays the current animation cursor position in musical time
// (measure, beat, tick) and provides transport controls for navigating by musical units.
// Reads timing config from a MusicEngine component in the scene; reads cues from SongCues.json.
public class HeartacheEUITransport : EditorWindow
{
    // --- Menu --------------------------------------------------------

    [MenuItem("Tools/Heartache/Transport")]
    public static void Open() => GetWindow<HeartacheEUITransport>("Transport");

    // --- Config ------------------------------------------------------

    private HeartacheMusicEngine _musicEngine;
    private AudioClip _audioClip;
    private HeartacheEUISongCue[] _cues = new HeartacheEUISongCue[0];
    private string _cuesPath = "";  // absolute path, persisted via EditorPrefs

    // --- Timing parameters (read from MusicEngine) -------------------

    private float _bpm = 80f;
    private int _beatsPerMeasure = 4;
    private int _ticksPerBeat = 4;
    private float _songLengthSeconds = 0f;

    // Tick is the smallest time unit. All other durations derive from it.
    private float SecondsPerTick => 60f / (_bpm * _ticksPerBeat);
    private int SongTotalTicks => Mathf.FloorToInt(_songLengthSeconds / SecondsPerTick);
    private int SongTotalBeats => SongTotalTicks / _ticksPerBeat;
    private int SongTotalMeasures => SongTotalTicks / (_ticksPerBeat * _beatsPerMeasure);
    private float NormToSongSeconds(float norm) => norm * _songLengthSeconds;

    // Convert wall-clock seconds to an integer tick index (floor, not round).
    private int TicksFromSeconds(float songSeconds) => Mathf.FloorToInt(songSeconds / SecondsPerTick);

    // --- Playback state ----------------------------------------------

    private bool _isPlaying = false;
    private double _playStartEditorTime;  // EditorApplication.timeSinceStartup at play start
    private float _playStartNormTime;     // cursor position [0,1] at play start

    // --- Transport controls ------------------------------------------

    private int _stepAmount = 1;
    private enum StepUnit { Measures, Beats, Ticks, Frames, Seconds }
    private StepUnit _stepUnit = StepUnit.Beats;

    // --- Animation window --------------------------------------------
    // The animation window is accessed via reflection because its relevant properties
    // (frame, animationClip) have no public API. Communicating via frame number is
    // unambiguous regardless of clip sample rate or length.

    private EditorWindow _animationWindow;
    private PropertyInfo _animWindowFrameProp;  // int frame (read/write)
    private PropertyInfo _animWindowClipProp;   // AnimationClip animationClip (read)

    private AnimationClip ActiveClip
    {
        get
        {
            if (_animationWindow == null) return null;
            return _animWindowClipProp?.GetValue(_animationWindow, null) as AnimationClip;
        }
    }

    private int AnimationWindowFrame
    {
        get
        {
            if (_animationWindow == null) return 0;
            var val = _animWindowFrameProp?.GetValue(_animationWindow, null);
            return val == null ? 0 : (int)val;
        }
        set
        {
            if (_animationWindow == null) return;
            _animWindowFrameProp?.SetValue(_animationWindow, value, null);
        }
    }

    private int ClipTotalFrames
    {
        get
        {
            var clip = ActiveClip;
            if (clip == null) return 1;
            return Mathf.Max(Mathf.RoundToInt(clip.length * clip.frameRate), 1);
        }
    }

    // Song position as a normalised value [0, 1].
    // During playback advances via the editor clock; at rest derived from the animation window frame.
    private float CurrentNormTime
    {
        get
        {
            if (_isPlaying)
            {
                float elapsed = (float)(EditorApplication.timeSinceStartup - _playStartEditorTime);
                return Mathf.Clamp01(_playStartNormTime + elapsed / _songLengthSeconds);
            }
            return (float)AnimationWindowFrame / ClipTotalFrames;
        }
    }

    // --- UI element refs ---------------------------------------------
    // Cached on CreateGUI so OnEditorUpdate can write to them directly each frame.
    private Label _timestampMeasure;
    private Label _timestampBeat;
    private Label _timestampTick;
    private Label _cueMarker;
    private Label _cueLyric;
    private Label _cueSection;
    private TextField _valMeasureIndex;
    private Label _valMeasureIndexMax;
    private TextField _valBeatIndex;
    private Label _valBeatIndexMax;
    private TextField _valTickIndex;
    private Label _valTickIndexMax;
    private TextField _valTimeMs;
    private TextField _valTimeS;
    private TextField _valTimeNorm;
    private Label _cuesPathLabel;
    private Label _statusEngine;
    private Label _animClipName;
    private Label _animClipBeatFrame;
    private Label _animClipBeatFrameMax;
    private TextField _animFrameIndex;
    private Label _animFrameIndexMax;
    private Label _animResolution;
    private Image _animResolutionIcon;
    private Button _playBtn;
    private VisualElement _markersGrid;

    // Cached per-clip resolution result: recomputed only when ClipTotalFrames changes
    private int _cachedResolutionFrames = -1;
    private int _lastAnimFrame = -1;
    private string _cachedResolutionLabel;
    private Texture2D _cachedResolutionIcon;
    private bool _cachedResolutionIsClean;

    // Note icons loaded once at CreateGUI so UpdateReadout never hits AssetDatabase per frame
    private Dictionary<string, Texture2D> _noteIcons;

    // --- Lifecycle ---------------------------------------------------

    private void OnEnable()
    {
        string saved = EditorPrefs.GetString("HeartacheEUITransport.cuesPath", "");
        if (!string.IsNullOrEmpty(saved) && File.Exists(saved))
            LoadCues(saved);
        FindAnimationWindow();
        FindMusicEngine();
        EditorApplication.update += OnEditorUpdate;
    }

    private void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
        StopPlayback();
    }

    // Returns the project-relative path to the directory containing this script.
    // Used to load sibling assets (UXML, USS, icons) without hardcoding folder paths.
    private static string ScriptDir()
    {
        foreach (var guid in AssetDatabase.FindAssets($"t:MonoScript {nameof(HeartacheEUITransport)}"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.EndsWith($"{nameof(HeartacheEUITransport)}.cs"))
                return Path.GetDirectoryName(path).Replace('\\', '/');
        }
        return "Assets/Modules/Heartache/Editor";
    }

    public void CreateGUI()
    {
        // Set window icon
        string iconPath = $"{ScriptDir()}/Resources/Icons/Icon EUI HeartacheTransport@2x.png";
        Texture2D windowIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
        titleContent = new GUIContent("Transport", windowIcon);

        var root = rootVisualElement;

        string dir = ScriptDir();
        var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>($"{dir}/HeartacheEUITransport.uxml");
        if (uxml == null)
        {
            root.Add(new Label($"HeartacheEUITransport.uxml not found in {dir}. Try reimporting the folder."));
            return;
        }
        uxml.CloneTree(root);

        // Cache element refs
        _timestampMeasure = root.Q<Label>("timestamp-measure");
        _timestampBeat = root.Q<Label>("timestamp-beat");
        _timestampTick = root.Q<Label>("timestamp-tick");
        _cueMarker = root.Q<Label>("cue-marker");
        _cueLyric = root.Q<Label>("cue-lyric");
        _cueSection = root.Q<Label>("cue-section");
        _valMeasureIndex = root.Q<TextField>("val-measure-index-numerator");
        _valMeasureIndexMax = root.Q<Label>("val-measure-index-denominator");
        _valBeatIndex = root.Q<TextField>("val-beat-index-numerator");
        _valBeatIndexMax = root.Q<Label>("val-beat-index-denominator");
        _valTickIndex = root.Q<TextField>("val-tick-index-numerator");
        _valTickIndexMax = root.Q<Label>("val-tick-index-denominator");
        _valTimeMs = root.Q<TextField>("val-time-ms");
        _valTimeS = root.Q<TextField>("val-time-s");
        _valTimeNorm = root.Q<TextField>("val-time-norm");
        _statusEngine = root.Q<Label>("status-engine");
        _animClipName = root.Q<Label>("anim-clip-name");
        _animClipBeatFrame = root.Q<Label>("anim-clip-beat-frame-numerator");
        _animClipBeatFrameMax = root.Q<Label>("anim-clip-beat-frame-denominator");
        _animFrameIndex = root.Q<TextField>("anim-frame-index");
        _animFrameIndexMax = root.Q<Label>("anim-frame-index-max");
        _animResolution = root.Q<Label>("anim-resolution");
        _animResolutionIcon = root.Q<Image>("anim-resolution-icon");
        _playBtn = root.Q<Button>("play-btn");
        _markersGrid = root.Q<VisualElement>("markers-grid");
        _cuesPathLabel = root.Q<Label>("cues-path-label");

        string iconDir = $"{dir}/Resources/Icons";
        _noteIcons = new Dictionary<string, Texture2D>
        {
            ["NoteLonga"] = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/NoteLonga.png"),
            ["NoteDoubleWhole"] = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/NoteDoubleWhole.png"),
            ["NoteWhole"] = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/NoteWhole.png"),
            ["NoteHalf"] = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/NoteHalf.png"),
            ["NoteQuarter"] = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/NoteQuarter.png"),
            ["NoteEighth"] = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/NoteEighth.png"),
            ["Note16"] = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/Note16.png"),
            ["Note32"] = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/Note32.png"),
            ["Note64"] = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/Note64.png"),
            ["Note128"] = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/Note128.png"),
            ["Note256"] = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/Note256.png"),
        };

        // Seek fields: each is editable and seeks on blur if the value changed.
        RegisterSeekField(_valMeasureIndex, v =>
        {
            if (int.TryParse(v, out int m)) SeekToSongSeconds(TickIndexToSongSeconds(m * _beatsPerMeasure * _ticksPerBeat));
        });
        RegisterSeekField(_valBeatIndex, v =>
        {
            if (int.TryParse(v, out int b)) SeekToSongSeconds(TickIndexToSongSeconds(b * _ticksPerBeat));
        });
        RegisterSeekField(_valTickIndex, v =>
        {
            if (int.TryParse(v, out int t)) SeekToSongSeconds(TickIndexToSongSeconds(t));
        });
        RegisterSeekField(_valTimeMs, v =>
        {
            // Accepts m:ss, m:ss.ms, or plain seconds
            if (TryParseTimeMs(v, out float secs)) SeekToSongSeconds(secs);
        });
        RegisterSeekField(_valTimeS, v =>
        {
            if (float.TryParse(v, out float secs)) SeekToSongSeconds(secs);
        });
        RegisterSeekField(_valTimeNorm, v =>
        {
            if (float.TryParse(v, out float norm)) SeekToNorm(norm);
        });
        RegisterSeekField(_animFrameIndex, v =>
        {
            if (int.TryParse(v, out int f)) SeekToNorm((float)f / ClipTotalFrames);
        });

        // Buttons
        root.Q<Button>("load-cues-btn").clicked += PromptLoadCues;

        root.Q<Button>("refresh-btn").clicked += () =>
        {
            FindMusicEngine();
            FindAnimationWindow();
            if (!string.IsNullOrEmpty(_cuesPath)) LoadCues(_cuesPath);
            RebuildMarkerButtons();
            _cachedResolutionFrames = -1; // invalidate so ClipResolution reruns after clip change
            UpdateReadout();
        };

        _playBtn.clicked += () =>
        {
            if (_isPlaying) StopPlayback();
            else StartPlayback();
            UpdateReadout();
        };

        root.Q<Button>("stop-btn").clicked += () => StopPlayback();
        root.Q<Button>("stop-btn").clicked += () => SeekToNorm(0f);
        root.Q<Button>("step-back-btn").clicked += () => Step(-1);
        root.Q<Button>("step-fwd-btn").clicked += () => Step(1);

        var stepAmountField = root.Q<IntegerField>("step-amount");
        stepAmountField.value = _stepAmount;
        stepAmountField.RegisterValueChangedCallback(e => _stepAmount = e.newValue);

        var stepUnitField = root.Q<EnumField>("step-unit");
        stepUnitField.Init(_stepUnit);
        stepUnitField.RegisterValueChangedCallback(e => _stepUnit = (StepUnit)e.newValue);

        UpdateCuesPathLabel();
        RebuildMarkerButtons();
        UpdateReadout();
    }

    // Populates the marker button strip from _cues. Called once on CreateGUI
    // and again after Refresh (since _cues may change if SongCues.json is edited).
    private void RebuildMarkerButtons()
    {
        if (_markersGrid == null) return;
        _markersGrid.Clear();
        foreach (var cue in _cues)
        {
            if (string.IsNullOrEmpty(cue.marker)) continue;
            var btn = new Button(() => SeekToSongSeconds(TickIndexToSongSeconds(cue.tick)))
            {
                text = cue.marker
            };
            _markersGrid.Add(btn);
        }
    }

    // Attaches focus-in/out callbacks to a seek TextField.
    // Snaps the value on focus-in so we can detect whether it actually changed on blur.
    private void RegisterSeekField(TextField field, Action<string> onCommit)
    {
        string focusedValue = null;
        field.RegisterCallback<FocusInEvent>(_ => focusedValue = field.value);
        field.RegisterCallback<FocusOutEvent>(_ =>
        {
            if (field.value != focusedValue) onCommit(field.value);
            UpdateReadout();
        });
    }

    // --- Readout -----------------------------------------------------
    // Called by OnEditorUpdate every frame while playing, and manually after seeks.
    private void UpdateReadout()
    {
        if (_timestampMeasure == null) return; // CreateGUI hasn't run yet

        float norm = CurrentNormTime;
        float songSecs = NormToSongSeconds(norm);

        // Large MBT timestamp display. Musical time, so starts counting at 1.
        TimeToMBT(songSecs, out int measure, out int beat, out int tick);
        _timestampMeasure.text = $"{measure:00}";
        _timestampBeat.text = $"{beat:00}";
        _timestampTick.text = $"{tick:00}";

        // Cue labels: active marker, lyric, and current section name.
        HeartacheEUISongCue cue = CueAt(songSecs);
        _cueMarker.text = cue != null && !string.IsNullOrEmpty(cue.marker) ? cue.marker : "-";
        _cueLyric.text = cue != null && !string.IsNullOrEmpty(cue.lyric) ? FormatLyric(cue.lyric) : "-";
        _cueSection.text = SectionAt(songSecs) is string s && !string.IsNullOrEmpty(s) ? s : "-";

        // Index readouts. All 0-indexed, derived from the tick count.
        int currentTick = TicksFromSeconds(songSecs);
        int currentBeat = currentTick / _ticksPerBeat;
        int currentMeasure = currentTick / (_ticksPerBeat * _beatsPerMeasure);
        if (_valMeasureIndex.focusController?.focusedElement != _valMeasureIndex)
            _valMeasureIndex.value = $"{currentMeasure}";
        _valMeasureIndexMax.text = $" / {SongTotalMeasures}";
        if (_valBeatIndex.focusController?.focusedElement != _valBeatIndex)
            _valBeatIndex.value = $"{currentBeat}";
        _valBeatIndexMax.text = $" / {SongTotalBeats}";
        if (_valTickIndex.focusController?.focusedElement != _valTickIndex)
            _valTickIndex.value = $"{currentTick}";
        _valTickIndexMax.text = $" / {SongTotalTicks}";

        // Wall-clock time readouts.
        TimeSpan ts = TimeSpan.FromSeconds(songSecs);
        if (_valTimeMs.focusController?.focusedElement != _valTimeMs)
            _valTimeMs.value = $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds:000}";
        if (_valTimeS.focusController?.focusedElement != _valTimeS)
            _valTimeS.value = $"{songSecs:0.000}";
        if (_valTimeNorm.focusController?.focusedElement != _valTimeNorm)
            _valTimeNorm.value = $"{norm:0.000 000 000}";

        // Status banner.
        _statusEngine.text = _musicEngine != null
            ? "<color=#00F490>●</color> Heartache engine found"
            : "✕ Heartache engine not found";

        // Animation clip panel
        var clip = ActiveClip;
        if (clip != null)
        {
            _animClipName.text = $"<color=#00F490>●</color> {clip.name}";

            // Beat-frame counter: shows which frame within the current beat we're on.
            int framesPerBeat = SongTotalBeats > 0 ? ClipTotalFrames / SongTotalBeats : 0;
            int frameWithinBeat = AnimationWindowFrame % Mathf.Max(framesPerBeat, 1) + 1;
            _animClipBeatFrame.text = framesPerBeat > 0 ? $"{frameWithinBeat:00}" : "--";
            _animClipBeatFrameMax.text = framesPerBeat > 0 ? $"{framesPerBeat:00}" : "--";

            if (_animFrameIndex.focusController?.focusedElement != _animFrameIndex)
                _animFrameIndex.value = $"{AnimationWindowFrame}";
            _animFrameIndexMax.text = $"{ClipTotalFrames}";

            // Resolution check: tells you what musical note value each frame represents.
            // Result is cached and only recomputed when the clip's frame count changes.
            int totalFrames = ClipTotalFrames;
            if (totalFrames != _cachedResolutionFrames)
            {
                (_cachedResolutionLabel, _cachedResolutionIcon, _cachedResolutionIsClean) = ClipResolution(totalFrames);
                _cachedResolutionFrames = totalFrames;
            }
            _animResolution.text = _cachedResolutionIsClean
                ? _cachedResolutionLabel
                : $"<color=#FF6B6B>{_cachedResolutionLabel}</color>";
            _animResolutionIcon.style.backgroundImage = new StyleBackground(_cachedResolutionIcon);
        }
        else
        {
            _animClipName.text = "✕ No animation clip";
            _animClipBeatFrame.text = "--";
            _animClipBeatFrameMax.text = "--";
            _animFrameIndex.value = "-";
            _animFrameIndexMax.text = "-";
            _animResolution.text = "-";
            _animResolutionIcon.image = null;
        }

        _playBtn.text = _isPlaying ? "Pause" : "Play";
    }

    // --- Transport ---------------------------------------------------

    // Fires every editor frame. Advances the animation window cursor during playback,
    // and refreshes the readout whenever the animation window is scrubbed manually.
    private void OnEditorUpdate()
    {
        if (_isPlaying)
        {
            float norm = CurrentNormTime;
            if (norm >= 1f)
            {
                StopPlayback();
                SeekToNorm(1f);
                return;
            }
            AnimationWindowFrame = Mathf.FloorToInt(norm * ClipTotalFrames);
            UpdateReadout();
        }
        else
        {
            int frame = AnimationWindowFrame;
            if (frame != _lastAnimFrame)
            {
                _lastAnimFrame = frame;
                UpdateReadout();
            }
        }
    }

    private void StartPlayback()
    {
        if (_audioClip == null) return;
        _playStartNormTime = CurrentNormTime;
        _playStartEditorTime = EditorApplication.timeSinceStartup;
        PlayClipFromTime(_audioClip, NormToSongSeconds(_playStartNormTime));
        _isPlaying = true;
    }

    private void StopPlayback()
    {
        if (!_isPlaying) return;
        StopAllClips();
        _isPlaying = false;
    }

    // Single entry point for all cursor movement. Keeps audio and animation frame in sync.
    private void SeekToNorm(float norm)
    {
        norm = Mathf.Clamp01(norm);
        if (_isPlaying)
        {
            // Restart audio from the new position without stopping playback state
            StopAllClips();
            _playStartNormTime = norm;
            _playStartEditorTime = EditorApplication.timeSinceStartup;
            PlayClipFromTime(_audioClip, NormToSongSeconds(norm));
        }
        AnimationWindowFrame = Mathf.FloorToInt(norm * ClipTotalFrames);
        UpdateReadout();
    }

    // Converts seconds to normalised float time and calls SeekToNorm.
    private void SeekToSongSeconds(float songSeconds)
    {
        if (_songLengthSeconds <= 0f) return;
        SeekToNorm(songSeconds / _songLengthSeconds);
    }

    // Steps the cursor forward or backward by the current step amount and unit.
    private void Step(int direction)
    {
        int totalFrames = ClipTotalFrames;
        if (SongTotalBeats <= 0 || totalFrames <= 0) return;

        // Operate in frame space for all units except Seconds to avoid accumulating float error.
        // Frame-to-beat ratio is derived from SongTotalBeats (which is based on musical time).
        int framesPerBeat = totalFrames / SongTotalBeats;
        int deltaFrames = 0;

        switch (_stepUnit)
        {
            case StepUnit.Frames: deltaFrames = direction * _stepAmount; break;
            case StepUnit.Beats: deltaFrames = direction * _stepAmount * framesPerBeat; break;
            case StepUnit.Measures: deltaFrames = direction * _stepAmount * _beatsPerMeasure * framesPerBeat; break;
            case StepUnit.Ticks: deltaFrames = direction * _stepAmount * (framesPerBeat / _ticksPerBeat); break;
            case StepUnit.Seconds:
                // Seconds still uses float time since stepping by fractional frames is awkward.
                float deltaSecs = _stepAmount * direction;
                SeekToSongSeconds(NormToSongSeconds(CurrentNormTime) + deltaSecs);
                return;
        }

        int newFrame = Mathf.Clamp(AnimationWindowFrame + deltaFrames, 0, totalFrames - 1);
        SeekToNorm((float)newFrame / totalFrames);
    }

    // --- Time maths ---------------------------------------------------

    // Converts seconds to measure/beat/tick display values (1-indexed, for display only).
    private void TimeToMBT(float songSeconds, out int measure, out int beat, out int tick)
    {
        int tickIndex = Mathf.FloorToInt(songSeconds / SecondsPerTick);
        int ticksPerMeasure = _ticksPerBeat * _beatsPerMeasure;
        measure = tickIndex / ticksPerMeasure + 1;
        beat = tickIndex % ticksPerMeasure / _ticksPerBeat + 1;
        tick = tickIndex % _ticksPerBeat + 1;
    }

    private float TickIndexToSongSeconds(int tickIndex) => tickIndex * SecondsPerTick;

    // Parses user time input. Accepts plain seconds ("75.5"), m:ss ("1:15"), or m:ss.ms ("1:15.500").
    private static bool TryParseTimeMs(string input, out float seconds)
    {
        input = input.Trim();
        if (float.TryParse(input, out seconds)) return true;
        string[] colonParts = input.Split(':');
        if (colonParts.Length == 2 && int.TryParse(colonParts[0], out int m))
        {
            string[] dotParts = colonParts[1].Split('.');
            if (dotParts.Length == 2
                && int.TryParse(dotParts[0], out int ss)
                && int.TryParse(dotParts[1], out int ms))
            {
                seconds = m * 60 + ss + ms / 1000f;
                return true;
            }
            if (int.TryParse(colonParts[1], out int secs))
            {
                seconds = m * 60 + secs;
                return true;
            }
        }
        seconds = 0f;
        return false;
    }

    // --- Lyric formatting --------------------------------------------

    // If the lyric contains [brackets], the bracketed portion is full-brightness
    // and everything outside is dimmed, marking the currently sung syllable/phrase.
    // Without brackets, the whole string is returned as-is.
    private static string FormatLyric(string lyric)
    {
        int open = lyric.IndexOf('[');
        int close = lyric.IndexOf(']');
        if (open < 0 || close < 0 || close < open) return lyric;

        string before = lyric[..open];
        string active = lyric[(open + 1)..close];
        string after = lyric[(close + 1)..];

        string result = "";
        if (before.Length > 0) result += $"<alpha=#77>{before}<alpha=#ff>";
        result += active;
        if (after.Length > 0) result += $"<alpha=#77>{after}<alpha=#ff>";
        return result;
    }

    // --- Cue lookup --------------------------------------------------
    // _cues is sorted by tick ascending. Both methods do a linear scan
    // and keep the last entry whose tick <= current position.

    // Returns the active cue at the given time (the most recent cue whose tick has passed).
    private HeartacheEUISongCue CueAt(float songSeconds)
    {
        int tickIndex = Mathf.FloorToInt(songSeconds / SecondsPerTick);
        HeartacheEUISongCue current = null;
        foreach (var cue in _cues)
        {
            if (cue.tick <= tickIndex) current = cue;
            else break;
        }
        return current;
    }

    // Returns the active section name at the given time (most recent cue with a non-null section).
    private string SectionAt(float songSeconds)
    {
        int tickIndex = Mathf.FloorToInt(songSeconds / SecondsPerTick);
        string current = null;
        foreach (var cue in _cues)
        {
            if (cue.tick > tickIndex) break;
            if (!string.IsNullOrEmpty(cue.section)) current = cue.section;
        }
        return current;
    }

    // --- Clip analysis -----------------------------------------------

    // Checks whether the clip's frame count maps cleanly to a musical note value
    // (e.g. 1 frame = 1/16th note). Reports the nearest candidate if not clean.
    private (string label, Texture2D icon, bool isClean) ClipResolution(int frames)
    {
        int measures = SongTotalMeasures;

        var candidates = new (int frames, string label, string icon)[]
        {
            (measures / 4,   "4 measures",   "NoteLonga"),
            (measures / 2,   "2 measures",   "NoteDoubleWhole"),
            (measures,       "1 measure",    "NoteWhole"),
            (measures * 2,   "Half note",    "NoteHalf"),
            (measures * 4,   "Quarter note", "NoteQuarter"),
            (measures * 8,   "Eighth note",  "NoteEighth"),
            (measures * 16,  "1/16th note",  "Note16"),
            (measures * 32,  "1/32nd note",  "Note32"),
            (measures * 64,  "1/64th note",  "Note64"),
            (measures * 128, "1/128th note", "Note128"),
            (measures * 256, "1/256th note", "Note256"),
        };

        // Single pass: find nearest candidate; delta == 0 means exact match
        int bestFrames = -1;
        string bestLabel = "";
        string bestIcon = null;
        int bestDelta = int.MaxValue;
        foreach (var (f, label, icon) in candidates)
        {
            if (f <= 0) continue;
            int delta = Mathf.Abs(frames - f);
            if (delta < bestDelta) { bestDelta = delta; bestFrames = f; bestLabel = label; bestIcon = icon; }
        }

        if (bestDelta == 0)
        {
            _noteIcons.TryGetValue(bestIcon, out var tex);
            return (bestLabel, tex, true);
        }

        string direction = bestFrames > frames ? "Lengthen" : "Shorten";
        return ($"No clean resolution. Nearest is {bestLabel} ({bestFrames} frames). {direction} by {bestDelta}", null, false);
    }

    // --- Scene / asset finders ---------------------------------------

    private void FindMusicEngine()
    {
        var results = FindObjectsByType<HeartacheMusicEngine>(FindObjectsSortMode.None);
        if (results.Length > 0)
        {
            _musicEngine = results[0];
            ReadMusicEngine();
        }
    }

    // Locates the Animation window via reflection and grabs its frame and clip properties.
    private void FindAnimationWindow()
    {
        var animWindowType = Type.GetType("UnityEditor.AnimationWindow, UnityEditor");
        if (animWindowType == null) return;
        var windows = Resources.FindObjectsOfTypeAll(animWindowType);
        if (windows.Length == 0) return;
        _animationWindow = windows[0] as EditorWindow;
        _animWindowFrameProp = animWindowType.GetProperty("frame",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _animWindowClipProp = animWindowType.GetProperty("animationClip",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    }

    private void PromptLoadCues()
    {
        string dir  = string.IsNullOrEmpty(_cuesPath) ? "Assets" : Path.GetDirectoryName(_cuesPath);
        string path = EditorUtility.OpenFilePanel("Open Song Cues", dir, "json");
        if (string.IsNullOrEmpty(path)) return;
        LoadCues(path);
        RebuildMarkerButtons();
        UpdateReadout();
    }

    private void LoadCues(string absolutePath)
    {
        if (!File.Exists(absolutePath)) return;
        try
        {
            string json = File.ReadAllText(absolutePath);
            var list = JsonUtility.FromJson<SongCueList>(json);
            _cues = list?.cues ?? new HeartacheEUISongCue[0];
            _cuesPath = absolutePath;
            EditorPrefs.SetString("HeartacheEUITransport.cuesPath", absolutePath);
            UpdateCuesPathLabel();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[HeartacheEUITransport] Failed to load cues from {absolutePath}: {ex.Message}");
        }
    }

    private void UpdateCuesPathLabel()
    {
        if (_cuesPathLabel == null) return;
        if (string.IsNullOrEmpty(_cuesPath))
        {
            _cuesPathLabel.text = "No cues loaded";
            return;
        }
        // Show a project-relative path if possible, otherwise the full path
        string dataPath = Application.dataPath.Replace('\\', '/');
        string absNorm  = _cuesPath.Replace('\\', '/');
        _cuesPathLabel.text = absNorm.StartsWith(dataPath)
            ? "Assets" + absNorm.Substring(dataPath.Length)
            : absNorm;
    }

    // Copies timing config and audio clip reference out of the MusicEngine component.
    private void ReadMusicEngine()
    {
        if (_musicEngine == null) return;
        _bpm = _musicEngine.BPM;
        _beatsPerMeasure = _musicEngine.BeatsPerMeasure;
        _ticksPerBeat = _musicEngine.TicksPerBeat;
        if (_musicEngine.MusicPlayer != null && _musicEngine.MusicPlayer.clip != null)
        {
            _audioClip = _musicEngine.MusicPlayer.clip;
            _songLengthSeconds = _audioClip.length;
        }
    }

    // --- Audio -------------------------------------------------------
    // PlayPreviewClip and StopAllPreviewClips are internal Unity editor API,
    // accessed via reflection since they have no public wrapper.
    private static Type AudioUtil => Type.GetType("UnityEditor.AudioUtil, UnityEditor");

    private static void PlayClipFromTime(AudioClip clip, float startTimeSecs)
    {
        var util = AudioUtil;
        if (util == null) return;
        var method = util.GetMethod("PlayPreviewClip",
            BindingFlags.Static | BindingFlags.Public,
            null,
            new Type[] { typeof(AudioClip), typeof(int), typeof(bool) },
            null);
        if (method == null) return;
        int startSample = Mathf.FloorToInt(startTimeSecs * clip.frequency);
        method.Invoke(null, new object[] { clip, startSample, false });
    }

    private static void StopAllClips()
    {
        AudioUtil?.GetMethod("StopAllPreviewClips",
            BindingFlags.Static | BindingFlags.Public)
            ?.Invoke(null, null);
    }
}
