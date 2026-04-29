using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public class TempoRuler : EditorWindow
{
    // --- Config references -------------------------------------------
    private MusicEngine _musicEngine;
    private AudioClip _audioClip;
    private SongCue[] _cues = new SongCue[0];

    private const string CuesPath = "Assets/Editor/SongCues.json";

    // --- Timing parameters (read from MusicEngine) -------------------
    private float _bpm = 80f;
    private int _beatsPerMeasure = 4;
    private int _ticksPerBeat = 4;
    private float _songLengthSeconds = 0f;

    // Derived
    private float SecondsPerTick => 60f / (_bpm * _ticksPerBeat);
    private float SongBeats => _songLengthSeconds * _bpm / 60f;
    private float SongMeasures => SongBeats / _beatsPerMeasure;
    private float SongTicks => SongBeats * _ticksPerBeat;

    // --- Playback state ----------------------------------------------
    // Internal time is normalised song time [0, 1].
    private bool _isPlaying = false;
    private double _playStartEditorTime;
    private float _playStartNormTime;

    // --- Jump controls -----------------------------------------------
    private int _stepAmount = 1;
    private enum StepUnit { Measures, Beats, Ticks, Frames, Seconds }
    private StepUnit _stepUnit = StepUnit.Beats;

    // --- Animation window --------------------------------------------
    // Communicates via frame number, which is unambiguous regardless of
    // clip sample rate or length. animationClip.frameRate converts to seconds.
    private EditorWindow _animationWindow;
    private PropertyInfo _animWindowFrameProp;   // int frame (read/write)
    private PropertyInfo _animWindowClipProp;    // AnimationClip animationClip (read)

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

    // --- Normalised time ---------------------------------------------
    // Song position as [0, 1]. During playback advances via editor clock;
    // at rest derived from the animation window frame.
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

    private float NormToSongSeconds(float norm) => norm * _songLengthSeconds;

    // --- UI element references ---------------------------------------
    // Cached on CreateGUI so OnEditorUpdate can write to them directly.
    private Label _timestamp;
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

    private Label _statusEngine;
    private Label _animClipName;
    private TextField _animFrameIndex;
    private Label _animFrameIndexMax;
    private Label _animResolution;
    private Image _animResolutionIcon;
    private Button _playBtn;
    private VisualElement _markersGrid;

    // Cached per-clip resolution result — recomputed only when ClipTotalFrames changes
    private int _cachedResolutionFrames = -1;
    private string _cachedResolutionLabel;
    private Texture2D _cachedResolutionIcon;
    private bool _cachedResolutionIsClean;

    // Icon textures loaded once at CreateGUI
    private Dictionary<string, Texture2D> _noteIcons;

    [MenuItem("Tools/Tempo Ruler")]
    public static void Open() => GetWindow<TempoRuler>("Tempo Ruler");

    // --- Lifecycle ---------------------------------------------------

    private void OnEnable()
    {
        LoadCues();
        FindAnimationWindow();
        FindMusicEngine();
        EditorApplication.update += OnEditorUpdate;
    }

    private void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
        StopPlayback();
    }

    // CreateGUI replaces OnGUI — called once to build the element tree.
    // After this, OnEditorUpdate writes directly to the cached element refs.
    public void CreateGUI()
    {
        var root = rootVisualElement;

        // Load UXML and USS from the same folder as this script
        string dir = "Assets/Editor";
        var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>($"{dir}/TempoRuler.uxml");
        if (uxml == null)
        {
            root.Add(new Label("TempoRuler.uxml not found — reimport the Editor folder."));
            return;
        }
        uxml.CloneTree(root);

        // Apply the USS stylesheet explicitly (UXML <Style> tag also loads it,
        // this is a belt-and-suspenders guard for first-import timing issues)
        var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>($"{dir}/TempoRuler.uss");
        if (uss != null) root.styleSheets.Add(uss);

        // Cache element refs by name (equivalent to document.getElementById)
        _timestamp      = root.Q<Label>("timestamp");
        _cueMarker      = root.Q<Label>("cue-marker");
        _cueLyric       = root.Q<Label>("cue-lyric");
        _cueSection     = root.Q<Label>("cue-section");
        _valMeasureIndex = root.Q<TextField>("val-measure-index-numerator");
        _valMeasureIndexMax = root.Q<Label>("val-measure-index-denominator");
        { string focused = null;
          _valMeasureIndex.RegisterCallback<FocusInEvent>(_  => focused = _valMeasureIndex.value);
          _valMeasureIndex.RegisterCallback<FocusOutEvent>(_ =>
          {
              if (_valMeasureIndex.value != focused && int.TryParse(_valMeasureIndex.value, out int m))
                  SeekToSongSeconds(MBTToSongSeconds(m + 1, 1, 1));
              UpdateReadout();
          }); }
        _valBeatIndex    = root.Q<TextField>("val-beat-index-numerator");
        _valBeatIndexMax = root.Q<Label>("val-beat-index-denominator");
        { string focused = null;
          _valBeatIndex.RegisterCallback<FocusInEvent>(_  => focused = _valBeatIndex.value);
          _valBeatIndex.RegisterCallback<FocusOutEvent>(_ =>
          {
              if (_valBeatIndex.value != focused && int.TryParse(_valBeatIndex.value, out int b))
                  SeekToSongSeconds(b * (60f / _bpm));
              UpdateReadout();
          }); }
        _valTickIndex    = root.Q<TextField>("val-tick-index-numerator");
        _valTickIndexMax = root.Q<Label>("val-tick-index-denominator");
        { string focused = null;
          _valTickIndex.RegisterCallback<FocusInEvent>(_  => focused = _valTickIndex.value);
          _valTickIndex.RegisterCallback<FocusOutEvent>(_ =>
          {
              if (_valTickIndex.value != focused && int.TryParse(_valTickIndex.value, out int t))
                  SeekToSongSeconds(TickIndexToSongSeconds(t));
              UpdateReadout();
          }); }
        _valTimeMs      = root.Q<TextField>("val-time-ms");
        _valTimeMs.RegisterCallback<FocusOutEvent>(_ =>
        {
            // Accepts m:ss, m:ss.ms, or plain seconds
            if (TryParseTimeMs(_valTimeMs.value, out float secs))
                SeekToSongSeconds(secs);
            UpdateReadout();
        });
        _valTimeS       = root.Q<TextField>("val-time-s");
        _valTimeS.RegisterCallback<FocusOutEvent>(_ =>
        {
            if (float.TryParse(_valTimeS.value, out float secs))
                SeekToSongSeconds(secs);
            UpdateReadout();
        });
        _valTimeNorm    = root.Q<TextField>("val-time-norm");
        _valTimeNorm.RegisterCallback<FocusOutEvent>(_ =>
        {
            if (float.TryParse(_valTimeNorm.value, out float norm))
                SeekToNorm(norm);
            UpdateReadout();
        });

        _statusEngine   = root.Q<Label>("status-engine");
        _animClipName      = root.Q<Label>("anim-clip-name");
        _animFrameIndex    = root.Q<TextField>("anim-frame-index");
        _animFrameIndexMax = root.Q<Label>("anim-frame-index-max");
        _animResolution     = root.Q<Label>("anim-resolution");
        _animResolutionIcon = root.Q<Image>("anim-resolution-icon");

        // Pre-load all note icons so UpdateReadout never hits AssetDatabase per frame
        string iconDir = "Assets/Editor/Icons/Music";
        _noteIcons = new Dictionary<string, Texture2D>
        {
            ["NoteLonga"]      = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/NoteLonga.png"),
            ["NoteDoubleWhole"]= AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/NoteDoubleWhole.png"),
            ["NoteWhole"]      = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/NoteWhole.png"),
            ["NoteHalf"]       = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/NoteHalf.png"),
            ["NoteQuarter"]    = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/NoteQuarter.png"),
            ["NoteEighth"]     = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/NoteEighth.png"),
            ["Note16"]         = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/Note16.png"),
            ["Note32"]         = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/Note32.png"),
            ["Note64"]         = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/Note64.png"),
            ["Note128"]        = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/Note128.png"),
            ["Note256"]        = AssetDatabase.LoadAssetAtPath<Texture2D>($"{iconDir}/Note256.png"),
        };
        { string focused = null;
          _animFrameIndex.RegisterCallback<FocusInEvent>(_  => focused = _animFrameIndex.value);
          _animFrameIndex.RegisterCallback<FocusOutEvent>(_ =>
          {
              if (_animFrameIndex.value != focused && int.TryParse(_animFrameIndex.value, out int f))
                  SeekToNorm((float)f / ClipTotalFrames);
              UpdateReadout();
          }); }
        _markersGrid    = root.Q<VisualElement>("markers-grid");
        _playBtn        = root.Q<Button>("play-btn");

        // --- Wire up events ------------------------------------------

        root.Q<Button>("refresh-btn").clicked += () =>
        {
            FindMusicEngine();
            FindAnimationWindow();
            LoadCues();
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

        root.Q<Button>("reset-btn").clicked += () => SeekToNorm(0f);

        root.Q<Button>("step-back-btn").clicked += () => Step(-1);
        root.Q<Button>("step-fwd-btn").clicked  += () => Step(1);

        var stepAmountField = root.Q<IntegerField>("step-amount");
        stepAmountField.value = _stepAmount;
        stepAmountField.RegisterValueChangedCallback(e => _stepAmount = e.newValue);

        var stepUnitField = root.Q<EnumField>("step-unit");
        stepUnitField.Init(_stepUnit);
        stepUnitField.RegisterValueChangedCallback(e => _stepUnit = (StepUnit)e.newValue);


        RebuildMarkerButtons();
        UpdateReadout();
    }

    // --- Marker buttons ----------------------------------------------
    // Called once on CreateGUI and again after Refresh, since _cues may change.

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

    // --- Readout update ----------------------------------------------
    // Called by OnEditorUpdate every frame while playing, and manually after seeks.

    private void UpdateReadout()
    {
        if (_timestamp == null) return; // CreateGUI hasn't run yet

        float norm     = CurrentNormTime;
        float songSecs = NormToSongSeconds(norm);

        // Timestamp
        TimeToMBT(songSecs, out int measure, out int beat, out int tick);
        _timestamp.text = $"{measure:00}:{beat:00}.{tick:00}";

        // Cue info
        SongCue cue = CueAt(songSecs);
        _cueMarker.text  = cue != null && !string.IsNullOrEmpty(cue.marker)  ? cue.marker  : "-";
        _cueLyric.text   = cue != null && !string.IsNullOrEmpty(cue.lyric)   ? cue.lyric   : "-";
        _cueSection.text = SectionAt(songSecs) is string s && !string.IsNullOrEmpty(s) ? s : "-";

        // Indices
        int totalTicks    = Mathf.FloorToInt(songSecs / SecondsPerTick);
        int totalBeats    = Mathf.FloorToInt(songSecs / (60f / _bpm));
        int totalMeasures = Mathf.FloorToInt(songSecs / (60f / _bpm * _beatsPerMeasure));
        if (_valMeasureIndex.focusController?.focusedElement != _valMeasureIndex)
            _valMeasureIndex.value = $"{totalMeasures}";
        _valMeasureIndexMax.text = $" / {Mathf.FloorToInt(SongMeasures)}";
        if (_valBeatIndex.focusController?.focusedElement != _valBeatIndex)
            _valBeatIndex.value = $"{totalBeats}";
        _valBeatIndexMax.text  = $" / {Mathf.FloorToInt(SongBeats)}";
        if (_valTickIndex.focusController?.focusedElement != _valTickIndex)
            _valTickIndex.value = $"{totalTicks}";
        _valTickIndexMax.text  = $" / {Mathf.FloorToInt(SongTicks)}";

        // Wall-clock time
        TimeSpan ts = TimeSpan.FromSeconds(songSecs);
        if (_valTimeMs.focusController?.focusedElement != _valTimeMs)
            _valTimeMs.value   = $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds:000}";
        if (_valTimeS.focusController?.focusedElement != _valTimeS)
            _valTimeS.value    = $"{songSecs:0.000}";
        if (_valTimeNorm.focusController?.focusedElement != _valTimeNorm)
            _valTimeNorm.value = $"{norm:0.000 000 000}";

        // Status row
        _statusEngine.text = _musicEngine != null
            ? "<color=#00F490>●</color> MusicEngine found"
            : "✕ MusicEngine not found";

        // Animation clip panel
        var clip = ActiveClip;
        if (clip != null)
        {
            _animClipName.text   = $"<color=#00F490>●</color> {clip.name}";
            if (_animFrameIndex.focusController?.focusedElement != _animFrameIndex)
                _animFrameIndex.value = $"{AnimationWindowFrame}";
            _animFrameIndexMax.text = $" / {ClipTotalFrames}";
            int totalFrames = ClipTotalFrames;
            if (totalFrames != _cachedResolutionFrames)
            {
                (_cachedResolutionLabel, _cachedResolutionIcon, _cachedResolutionIsClean) = ClipResolution(totalFrames);
                _cachedResolutionFrames = totalFrames;
            }
            _animResolution.text = _cachedResolutionIsClean
                ? _cachedResolutionLabel
                : $"<color=#FF6B6B>{_cachedResolutionLabel}</color>";
            _animResolutionIcon.image = _cachedResolutionIcon;
        }
        else
        {
            _animClipName.text        = "✕ No animation clip";
            _animFrameIndex.value     = "-";
            _animFrameIndexMax.text   = "";
            _animResolution.text      = "-";
            _animResolutionIcon.image = null;
        }

        // Play button label
        if (_playBtn != null)
            _playBtn.text = _isPlaying ? "Pause" : "Play";
    }

    // --- Find helpers ------------------------------------------------

    private void FindMusicEngine()
    {
        var results = FindObjectsByType<MusicEngine>(FindObjectsSortMode.None);
        if (results.Length > 0)
        {
            _musicEngine = results[0];
            ReadMusicEngine();
        }
    }

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

    private void LoadCues()
    {
        string fullPath = Path.Combine(Application.dataPath, "../", CuesPath);
        if (!File.Exists(fullPath)) return;
        string json = File.ReadAllText(fullPath);
        var list = JsonUtility.FromJson<SongCueList>(json);
        _cues = list?.cues ?? new SongCue[0];
    }

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

    // --- Playback ----------------------------------------------------

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

    private void OnEditorUpdate()
    {
        if (!_isPlaying) return;
        float norm = CurrentNormTime;
        if (norm >= 1f)
        {
            StopPlayback();
            SeekToNorm(1f);
            return;
        }
        AnimationWindowFrame = Mathf.RoundToInt(norm * ClipTotalFrames);
        UpdateReadout();
    }

    // Single entry point for all cursor movement.
    private void SeekToNorm(float norm)
    {
        norm = Mathf.Clamp01(norm);
        if (_isPlaying)
        {
            StopAllClips();
            _playStartNormTime = norm;
            _playStartEditorTime = EditorApplication.timeSinceStartup;
            PlayClipFromTime(_audioClip, NormToSongSeconds(norm));
        }
        AnimationWindowFrame = Mathf.RoundToInt(norm * ClipTotalFrames);
        UpdateReadout();
    }

    private void SeekToSongSeconds(float songSeconds)
    {
        if (_songLengthSeconds <= 0f) return;
        SeekToNorm(songSeconds / _songLengthSeconds);
    }

    // --- AudioUtility reflection (internal Unity API) ----------------
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

    // --- Time parsing ------------------------------------------------
    private static bool TryParseTimeMs(string input, out float seconds)
    {
        input = input.Trim();
        // plain float or integer
        if (float.TryParse(input, out seconds)) return true;
        // m:ss or m:ss.ms
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

    // --- Clip resolution ---------------------------------------------
    private (string label, Texture2D icon, bool isClean) ClipResolution(int frames)
    {
        int measures = Mathf.RoundToInt(SongMeasures);

        var candidates = new (int frames, string label, string icon)[]
        {
            (measures / 4,   "4 measures", "NoteLonga"),
            (measures / 2,   "2 measures", "NoteDoubleWhole"),
            (measures,       "1 measure",  "NoteWhole"),
            (measures * 2,   "1/2 note",   "NoteHalf"),
            (measures * 4,   "1/4 note",   "NoteQuarter"),
            (measures * 8,   "1/8 note",   "NoteEighth"),
            (measures * 16,  "1/16 note",  "Note16"),
            (measures * 32,  "1/32 note",  "Note32"),
            (measures * 64,  "1/64 note",  "Note64"),
            (measures * 128, "1/128 note", "Note128"),
            (measures * 256, "1/256 note", "Note256"),
        };

        // Single pass: find nearest, exact match when delta == 0
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

    // --- Time conversion helpers -------------------------------------
    private void TimeToMBT(float songSeconds, out int measure, out int beat, out int tick)
    {
        int tickIndex = Mathf.FloorToInt(songSeconds / SecondsPerTick);
        int ticksPerMeasure = _ticksPerBeat * _beatsPerMeasure;
        measure = tickIndex / ticksPerMeasure + 1;
        beat    = tickIndex % ticksPerMeasure / _ticksPerBeat + 1;
        tick    = tickIndex % _ticksPerBeat + 1;
    }

    private float MBTToSongSeconds(int measure, int beat, int tick)
    {
        int totalTicks = (measure - 1) * _beatsPerMeasure * _ticksPerBeat
                       + (beat   - 1) * _ticksPerBeat
                       + (tick   - 1);
        return totalTicks * SecondsPerTick;
    }

    private float TickIndexToSongSeconds(int tickIndex) => tickIndex * SecondsPerTick;

    private SongCue CueAt(float songSeconds)
    {
        int tickIndex = Mathf.FloorToInt(songSeconds / SecondsPerTick);
        SongCue current = null;
        foreach (var cue in _cues)
        {
            if (cue.tick <= tickIndex) current = cue;
            else break;
        }
        return current;
    }

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

    private void Step(int direction)
    {
        if (_stepUnit == StepUnit.Frames)
        {
            // Operate on integer frames directly to avoid float drift
            SeekToNorm((float)(AnimationWindowFrame + direction * _stepAmount) / ClipTotalFrames);
            return;
        }
        float deltaSecs = 0f;
        switch (_stepUnit)
        {
            case StepUnit.Measures: deltaSecs = _stepAmount * _beatsPerMeasure * _ticksPerBeat * SecondsPerTick; break;
            case StepUnit.Beats:    deltaSecs = _stepAmount * _ticksPerBeat * SecondsPerTick; break;
            case StepUnit.Ticks:    deltaSecs = _stepAmount * SecondsPerTick; break;
            case StepUnit.Seconds:  deltaSecs = _stepAmount; break;
        }
        SeekToSongSeconds(NormToSongSeconds(CurrentNormTime) + direction * deltaSecs);
    }
}
