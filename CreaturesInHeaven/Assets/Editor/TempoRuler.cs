using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

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
    private enum StepUnit { Measures, Beats, Ticks, Seconds }
    private StepUnit _stepUnit = StepUnit.Beats;
    private string _jumpInput = "";
    private bool _debugFoldout = false;

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

    [MenuItem("Window/Tempo Ruler")]
    public static void Open() => GetWindow<TempoRuler>("Tempo Ruler");

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
            Repaint();
            return;
        }
        AnimationWindowFrame = Mathf.FloorToInt(norm * ClipTotalFrames);
        Repaint();
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
        AnimationWindowFrame = Mathf.FloorToInt(norm * ClipTotalFrames);
        Repaint();
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

    // --- Time conversion helpers -------------------------------------

    private void TimeToMBT(float songSeconds, out int measure, out int beat, out int tick)
    {
        int tickIndex = Mathf.FloorToInt(songSeconds / SecondsPerTick);
        int ticksPerMeasure = _ticksPerBeat * _beatsPerMeasure;
        measure = tickIndex / ticksPerMeasure + 1;
        beat    = (tickIndex % ticksPerMeasure) / _ticksPerBeat + 1;
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

    // --- GUI ---------------------------------------------------------

    private void OnGUI()
    {
        if (_musicEngine == null) FindMusicEngine();
        if (_animationWindow == null) FindAnimationWindow();

        var clip = ActiveClip;
        int totalFrames = ClipTotalFrames;
        float norm = _isPlaying ? CurrentNormTime : (float)AnimationWindowFrame / totalFrames;
        float songSecs = NormToSongSeconds(norm);

        TimeToMBT(songSecs, out int measure, out int beat, out int tick);
        int totalTicks    = Mathf.FloorToInt(songSecs / SecondsPerTick);
        int totalBeats    = Mathf.FloorToInt(songSecs / (60f / _bpm));
        int totalMeasures = Mathf.FloorToInt(songSecs / (60f / _bpm * _beatsPerMeasure));

        // --- Status row ----------------------------------------------
        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        string engineLabel = _musicEngine != null ? "MusicEngine found" : "MusicEngine not found";
        string clipLabel   = clip != null ? $"{clip.name}  {totalFrames}f  {clip.length:0.000}s" : "no clip";
        EditorGUILayout.LabelField($"{engineLabel}   {clipLabel}", EditorStyles.miniLabel);
        if (GUILayout.Button("Refresh", GUILayout.Width(60)))
        {
            FindMusicEngine();
            FindAnimationWindow();
            LoadCues();
        }
        EditorGUILayout.EndHorizontal();


        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

        // --- Timestamp readout -----------------------------------------
        EditorGUILayout.Space(8);

        var bigStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 40,
            alignment = TextAnchor.MiddleCenter
        };
        EditorGUILayout.LabelField($"{measure:00}:{beat:00}.{tick:00}", bigStyle, GUILayout.Height(40));
        EditorGUILayout.Space(4);

        // --- Lyric readout ---------------------------------------------
        var lyricStyle = new GUIStyle(EditorStyles.label)
        {
            fontSize = 18,
            alignment = TextAnchor.MiddleCenter
        };

        SongCue cue = CueAt(songSecs);
        if (cue != null)
        {
            if (!string.IsNullOrEmpty(cue.lyric))
                EditorGUILayout.SelectableLabel(cue.lyric, lyricStyle, GUILayout.Height(24));
            else
                EditorGUILayout.LabelField("-", lyricStyle, GUILayout.Height(24));
        }    
        EditorGUILayout.Space(4);

        // --- Cue & section readout ----------------------------------
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(!string.IsNullOrEmpty(cue.marker) ? cue.marker : "-");
        string section = SectionAt(songSecs);
        EditorGUILayout.LabelField(!string.IsNullOrEmpty(section) ? section : "-");
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(8);

        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

        // --- Auxiliary index readout ---------------------------------
        EditorGUILayout.BeginHorizontal();
        
        EditorGUILayout.BeginVertical();
        EditorGUILayout.LabelField("Measure index", EditorStyles.boldLabel);
        EditorGUILayout.SelectableLabel($"{totalMeasures} / {Mathf.FloorToInt(SongMeasures)}", GUILayout.Height(15));
        EditorGUILayout.EndVertical();

        EditorGUILayout.BeginVertical();
        EditorGUILayout.LabelField("Beat index", EditorStyles.boldLabel);
        EditorGUILayout.SelectableLabel($"{totalBeats} / {Mathf.FloorToInt(SongBeats)}", GUILayout.Height(15));
        EditorGUILayout.EndVertical();

        EditorGUILayout.BeginVertical();
        EditorGUILayout.LabelField("Tick index", EditorStyles.boldLabel);
        EditorGUILayout.SelectableLabel($"{totalTicks} / {Mathf.FloorToInt(SongTicks)}", GUILayout.Height(15));
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

        // --- Time readout --------------------------------------------
        TimeSpan ts = TimeSpan.FromSeconds(songSecs);

        EditorGUILayout.BeginHorizontal();

        EditorGUILayout.BeginVertical();
        EditorGUILayout.LabelField("Time", EditorStyles.boldLabel);
        EditorGUILayout.SelectableLabel($"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds:000}", GUILayout.Height(15));
        EditorGUILayout.EndVertical();

        EditorGUILayout.BeginVertical();
        EditorGUILayout.LabelField("Time (s)", EditorStyles.boldLabel);
        EditorGUILayout.SelectableLabel($"{songSecs:0.000} s", GUILayout.Height(15));
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

        // --- Playback controls ---------------------------------------
        EditorGUILayout.BeginHorizontal();
        GUI.enabled = _audioClip != null;
        if (GUILayout.Button(_isPlaying ? "⏸ Pause" : "▶ Play"))
        {
            if (_isPlaying) StopPlayback();
            else StartPlayback();
        }
        GUI.enabled = true;
        if (GUILayout.Button("⏮ Reset"))
            SeekToNorm(0f);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);

        // --- Step controls -------------------------------------------
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("◀", GUILayout.Width(30))) Step(-1);
        if (GUILayout.Button("▶", GUILayout.Width(30))) Step(1);
        _stepAmount = EditorGUILayout.IntField(_stepAmount, GUILayout.Width(40));
        _stepUnit   = (StepUnit)EditorGUILayout.EnumPopup(_stepUnit);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);

        // --- Jump to input -------------------------------------------
        EditorGUILayout.BeginHorizontal();
        _jumpInput = EditorGUILayout.TextField("Jump to", _jumpInput);
        if (GUILayout.Button("Go", GUILayout.Width(36)))
            TryJump(_jumpInput);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);

        // --- Marker buttons ------------------------------------------
        EditorGUILayout.LabelField("Markers", EditorStyles.boldLabel);
        int cols = Mathf.Max(1, Mathf.FloorToInt(position.width / 72));
        int col = 0;
        EditorGUILayout.BeginHorizontal();
        foreach (var cueEntry in _cues)
        {
            if (string.IsNullOrEmpty(cueEntry.marker)) continue;
            if (col >= cols)
            {
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                col = 0;
            }
            if (GUILayout.Button(cueEntry.marker, GUILayout.Width(60)))
                SeekToSongSeconds(TickIndexToSongSeconds(cueEntry.tick));
            col++;
        }
        EditorGUILayout.EndHorizontal();

        // --- Debug foldout -------------------------------------------
        EditorGUILayout.Space(20);

        _debugFoldout = EditorGUILayout.Foldout(_debugFoldout, "Debug");
        if (_debugFoldout)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUI.indentLevel++;
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Normalised time", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel($"{norm:0.000000}", GUILayout.Height(15));

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Song time (s)", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel($"{songSecs:0.000000}", GUILayout.Height(15));

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Animation frame", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel($"{AnimationWindowFrame} / {totalFrames}", GUILayout.Height(15));

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Playing state", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel($"{_isPlaying}", GUILayout.Height(15));

            EditorGUILayout.Space(8);
            EditorGUI.indentLevel--;
            EditorGUILayout.EndVertical();
        }
    }

    private void Step(int direction)
    {
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

    private void TryJump(string input)
    {
        input = input.Trim();
        if (string.IsNullOrEmpty(input)) return;

        // Plain integer: tick index
        if (int.TryParse(input, out int tickIdx))
        {
            SeekToSongSeconds(TickIndexToSongSeconds(tickIdx));
            return;
        }

        string[] mbtParts = input.Split(':');

        // m:ss or m:ss.ms
        if (mbtParts.Length == 2 && int.TryParse(mbtParts[0], out int m))
        {
            string[] subParts = mbtParts[1].Split('.');
            if (subParts.Length == 2
                && int.TryParse(subParts[0], out int ss)
                && int.TryParse(subParts[1], out int ms))
            {
                SeekToSongSeconds(m * 60 + ss + ms / 1000f);
                return;
            }
            if (int.TryParse(mbtParts[1], out int secs))
            {
                SeekToSongSeconds(m * 60 + secs);
                return;
            }
        }

        // m:b:t (1-indexed)
        if (mbtParts.Length == 3
            && int.TryParse(mbtParts[0], out int mb)
            && int.TryParse(mbtParts[1], out int b)
            && int.TryParse(mbtParts[2].Split('.')[0], out int tk))
        {
            SeekToSongSeconds(MBTToSongSeconds(mb, b, tk));
            return;
        }

        Debug.LogWarning($"[TempoRuler] Could not parse jump input: '{input}'");
    }
}
