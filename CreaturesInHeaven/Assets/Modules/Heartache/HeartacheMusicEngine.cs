using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using UnityEngine.UI;
using VRC.Udon.Common.Interfaces;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class HeartacheMusicEngine : UdonSharpBehaviour
{
    // --- Song timing --------------------------------------------------
    // Core timing parameters for the song. Set in the inspector.
    [SerializeField] public float BPM = 80f;
    [SerializeField] public int BeatsPerMeasure = 4;
    [SerializeField] public int TicksPerBeat = 4;

    // Hack for starting playback at a specific time. Useful for debugging.
    [Tooltip("Debugging function for starting playback ahead of time. Useful for debugging.\n\n" +
             "[D1] 0.25" +
             "[F1] 0.45" +
             "[G3] 0.655")]
    public float CustomStartTime = 0f;

    // --- Song metadata ------------------------------------------------
    // Derived from the audio clip and timing parameters at Start().
    // Read-only after initialisation. Use these instead of recomputing.
    public float SampleRate { get; private set; }
    public float SongLengthInSeconds { get; private set; }
    public float SongSampleCount { get; private set; }
    public float SongBeats { get; private set; }
    public float SongMeasures { get; private set; }
    public float SongTicks { get; private set; }

    // --- Synced state -------------------------------------------------
    // Written only by the instance owner and broadcast to all players
    // via RequestSerialization(). Non-owners treat these as read-only.
    [UdonSynced] private float _syncedAnimationTime;
    public float SyncedAnimationTime => _syncedAnimationTime;

    [UdonSynced] private bool _syncedPlaying;
    public bool SyncedPlaying => _syncedPlaying;

    // --- Local state --------------------------------------------------
    // LocalAnimationTime is derived each frame from the local AudioSource
    // sample position. All players maintain this independently; the owner
    // uses it to drive _syncedAnimationTime, while non-owners use it for
    // animation and drift detection.
    public float LocalAnimationTime { get; private set; }

    // Tracks the non-owner's local view of _syncedPlaying so it can react
    // to state changes received via OnDeserialization.
    private bool _localPlaying;

    // Used by the owner to throttle RequestSerialization() calls.
    // Resets whenever a sync is sent, whether timer- or event-driven.
    private float _syncTimer = 0f;
    private bool _lastSyncedPlaying;

    // Timestamp of the last OnDeserialization call. Non-owners use this to
    // extrapolate the owner's current position from the last received sync,
    // preventing false drift corrections while syncs are infrequent.
    private float _lastDeserializationTime;

    // --- Network state -----------------------------------------------
    public bool IsOwner => Networking.IsOwner(Networking.LocalPlayer, gameObject);

    // --- Inspector references (audience) -----------------------------
    [SerializeField] private HeartacheAudienceManager AudienceManager;

    // --- Tick event system -------------------------------------------
    // Each Update(), if the tick index has advanced, all TickListeners
    // receive OnTick(). Listeners read TickIsMeasure and TickIsBeat to
    // determine the tick type without needing to do their own modulo math.
    public bool TickIsMeasure { get; private set; }
    public bool TickIsBeat { get; private set; }
    [SerializeField] private UdonBehaviour[] TickListeners;
    private int _lastTickIndex = -1;

    // --- Inspector references -----------------------------------------

    public AudioSource MusicPlayer;
    public AudioSource MusicPlayerLobby;
    [SerializeField] private Animator[] Animators;
    [SerializeField] private UdonBehaviour[] SequenceListeners;
    public AnchorTeleport StartTeleporter;
    public Button ButtonStart;
    public Button ButtonJoin;

    // Records the time of each deserialization so non-owners can extrapolate
    // the owner's position forward from the (potentially stale) synced value.
    public override void OnDeserialization()
    {
        _lastDeserializationTime = Time.time;
    }

    public void Start()
    {
        // Initialise to now so the non-owner drift check doesn't accumulate
        // a large timeSinceSync before the first deserialization arrives.
        _lastDeserializationTime = Time.time;

        SongLengthInSeconds = MusicPlayer.clip.length;
        SampleRate = MusicPlayer.clip.samples / MusicPlayer.clip.length;
        SongSampleCount = MusicPlayer.clip.samples;
        SongBeats = SongLengthInSeconds * BPM / 60f;
        SongMeasures = SongBeats / BeatsPerMeasure;
        SongTicks = SongBeats * TicksPerBeat;
    }

    // Takes ownership and starts playback from the beginning, teleporting
    // all players to the start point via network trigger.
    public void StartButtonPressed()
    {
        StartPlaybackFromTime(0.0f);
    }

    // Temporary debug function that starts playback from a given time
    // because I can't use TextField.text in Udon for some reason.
    // Separate method (not a parameter) because Udon button events can't pass arguments.
    public void StartButtonPressedForward()
    {
        StartPlaybackFromTime(CustomStartTime);
    }

    private void StartPlaybackFromTime(float time)
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        LocalAnimationTime = time;
        _syncedAnimationTime = time;
        _syncedPlaying = true;
        PlayFromTime(time);
        StartTeleporter.TeleportNetwork();
    }

    // Teleports the local player to the start point to join ongoing playback.
    public void JoinButtonPressed()
    {
        StartTeleporter.TeleportLocal();
    }

    // Seeks both audio sources to a normalised time [0, 1].
    // timeSamples is assigned both before and after Play() because Unity
    // resets the sample position when Play() is called.
    private void PlayFromTime(float normalisedTime)
    {
        int targetSample = (int)(normalisedTime * SongSampleCount);

        MusicPlayer.Stop();
        MusicPlayer.timeSamples = targetSample;
        MusicPlayer.Play();
        MusicPlayer.timeSamples = targetSample;

        MusicPlayerLobby.Stop();
        MusicPlayerLobby.timeSamples = targetSample;
        MusicPlayerLobby.Play();
        MusicPlayerLobby.timeSamples = targetSample;
    }

    // Stops all audio sources and resets animators and sequences to time zero.
    private void StopPlaying()
    {
        MusicPlayer.Stop();
        MusicPlayerLobby.Stop();
        for (int i = 0; i < Animators.Length; i++)
            if (Animators[i] != null) Animators[i].SetFloat("_Time", 0f);
        for (int i = 0; i < SequenceListeners.Length; i++)
            if (SequenceListeners[i] != null)
                SequenceListeners[i].SendCustomEvent("OnSequenceStop");
    }

    private void DriveAnimatorsAndSequences(float normTime)
    {
        for (int i = 0; i < Animators.Length; i++)
            if (Animators[i] != null) Animators[i].SetFloat("_Time", normTime);
        for (int i = 0; i < SequenceListeners.Length; i++)
            if (SequenceListeners[i] != null)
                SequenceListeners[i].SendCustomEvent("OnSequenceUpdate");
    }

    void Update()
    {
        MusicPlayerLobby.volume = AudienceManager.WatchingAnimation ? 0f : 0.6f;
        MusicPlayer.volume = AudienceManager.WatchingAnimation ? 0.8f : 0f;

        // Start is only available before playback; Join is only available during.
        ButtonStart.interactable = !_syncedPlaying;
        ButtonJoin.interactable = _syncedPlaying;

        // Derive normalised local time directly from the audio sample position.
        // More accurate than tracking elapsed time manually.
        float localTimeSeconds = MusicPlayer.timeSamples / SampleRate;
        LocalAnimationTime = localTimeSeconds / MusicPlayer.clip.length;

        if (MusicPlayer.isPlaying)
        {
            int currentTickIndex = Mathf.FloorToInt(LocalAnimationTime * SongBeats * TicksPerBeat);
            int delta = currentTickIndex - _lastTickIndex;

            // Only fire for positive deltas.
            // Zero means no change, negative means a backward seek and it'd be good
            // to avoid any double-playing issues.
            if (delta > 0)
            {
                // Set flags so listeners can identify the tick type without
                // needing to do their own modulo math.
                TickIsMeasure = currentTickIndex % (TicksPerBeat * BeatsPerMeasure) == 0;
                TickIsBeat = currentTickIndex % TicksPerBeat == 0;

                for (int i = 0; i < TickListeners.Length; i++)
                    if (TickListeners[i] != null)
                        TickListeners[i].SendCustomEvent("OnTick");
            }

            // Update on any change, including backward seeks and resync jumps,
            // so the next delta is always computed from the correct position.
            if (delta != 0)
                _lastTickIndex = currentTickIndex;
        }
        else
        {
            // Reset so the next play starts tick events from scratch.
            _lastTickIndex = -1;
        }

        if (IsOwner)
        {
            // Owner drives synced time, animators, and sequences from their local audio position.
            _syncedAnimationTime = LocalAnimationTime;
            DriveAnimatorsAndSequences(_syncedAnimationTime);

            if (!MusicPlayer.isPlaying && _syncedPlaying)
            {
                _syncedPlaying = false;
                StopPlaying();
            }

            // Sync immediately on play/stop state changes; otherwise every 2 seconds.
            // This avoids calling RequestSerialization() every frame.
            // Non-owners extrapolate the owner's position between syncs
            // to avoid false resyncs.
            bool playingChanged = _syncedPlaying != _lastSyncedPlaying;
            _lastSyncedPlaying = _syncedPlaying;
            _syncTimer += Time.deltaTime;

            if (playingChanged || _syncTimer >= 2f)
            {
                RequestSerialization();
                _syncTimer = 0f;
            }
        }
        else
        {
            // Non-owners run their own audio independently and animate from local time.
            DriveAnimatorsAndSequences(LocalAnimationTime);

            // Drift correction: compare local position against where the owner is
            // predicted to be now (synced position + time elapsed since last sync).
            // Only runs during playback so the engine doesn't try to resync if the
            // song hasn't actually started yet.
            if (_syncedPlaying)
            {
                float localSecs = LocalAnimationTime * SongLengthInSeconds;
                float timeSinceSync = Time.time - _lastDeserializationTime;
                float predictedOwnerSecs = (_syncedAnimationTime * SongLengthInSeconds) + timeSinceSync;
                if (Mathf.Abs(localSecs - predictedOwnerSecs) > 1.0f)
                    PlayFromTime(predictedOwnerSecs / SongLengthInSeconds);
            }

            // React to owner starting or stopping playback.
            if (_localPlaying != _syncedPlaying)
            {
                _localPlaying = _syncedPlaying;
                if (_syncedPlaying)
                    PlayFromTime(_syncedAnimationTime);
                else
                    StopPlaying();
            }
        }
    }
}
