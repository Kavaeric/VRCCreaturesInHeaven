using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using UnityEngine.UI;
using VRC.Udon.Common.Interfaces;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class MusicEngine : UdonSharpBehaviour
{
    // --- Song timing --------------------------------------------------
    // Core timing parameters for the song. Set in the inspector.
    [SerializeField] public float BPM = 80f;
    [SerializeField] public int BeatsPerMeasure = 4;
    [SerializeField] public int TicksPerBeat = 4;

    // --- Song metadata ------------------------------------------------
    public float SampleRate { get; private set; }
    public float SongLengthInSeconds { get; private set; }
    public float SongSampleCount { get; private set; }
    public float SongBeats { get; private set; }
    public float SongMeasures { get; private set; }
    public float SongTicks { get; private set; }

    // --- Synced state -------------------------------------------------
    // Variables owned and written by the instance owner, then broadcast
    // to all other players via RequestSerialization().
    [UdonSynced] private float _syncedAnimationTime;
    public float SyncedAnimationTime => _syncedAnimationTime;

    [UdonSynced] private bool _syncedPlaying;
    public bool SyncedPlaying => _syncedPlaying;

    // --- Local state -------------------------------------------------
    // Derived each frame from the local AudioSource, independent of network.
    // Non-owners use this for display and drift detection.
    public float LocalAnimationTime { get; private set; }
    private bool _localPlaying;

    // --- Network state -----------------------------------------------
    public bool IsOwner => Networking.IsOwner(Networking.LocalPlayer, gameObject);
    public bool PlayerInSpawn { get; private set; }

    // --- Tick event system -------------------------------------------
    // Listeners receive OnTick() and read these flags to determine tick type.
    public bool TickIsMeasure { get; private set; }
    public bool TickIsBeat { get; private set; }
    public UdonBehaviour[] TickListeners;
    private int _lastTickIndex = -1;

    // --- Inspector references -----------------------------------------

    public AudioSource SoundPlayer;
    public AudioSource SoundPlayerMuffled;
    public Animator[] animators;
    public RelativeTeleport SpawnTeleporter;
    public Text ButtonText;

    public void Start()
    {
        SongLengthInSeconds = SoundPlayer.clip.length;
        SampleRate = SoundPlayer.clip.samples / SoundPlayer.clip.length;
        SongSampleCount = SoundPlayer.clip.samples;
        SongBeats = SongLengthInSeconds * BPM / 60f;
        SongMeasures = SongBeats / BeatsPerMeasure;
        SongTicks = SongBeats * TicksPerBeat;
    }

    // Event for when the start button is pressed -- currently that button serves
    // double duty as a join and start button, but may be wiser to split that
    // functionality and just hide the one that's not relevant.
    public void StartButtonPressed()
    {
        if (_syncedPlaying)
        {
            // Song already running, teleport local player to join.
            SpawnTeleporter.TriggerLocal();
        }
        else
        {
            // Take ownership so this client can write synced variables.
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
            LocalAnimationTime = 0;
            _syncedAnimationTime = 0;
            _syncedPlaying = true;
            PlayFromTime(0);
            SpawnTeleporter._NetworkTrigger();
        }
    }

    // Seeks both audio sources to a normalised animation time [0 to 1].
    private void PlayFromTime(float normalisedTime)
    {
        int targetSample = (int)(normalisedTime * SongSampleCount);

        SoundPlayer.Stop();
        SoundPlayer.timeSamples = targetSample;
        SoundPlayer.Play();
        SoundPlayer.timeSamples = targetSample;

        SoundPlayerMuffled.Stop();
        SoundPlayerMuffled.timeSamples = targetSample;
        SoundPlayerMuffled.Play();
        SoundPlayerMuffled.timeSamples = targetSample;
    }

    private void StopPlaying()
    {
        SoundPlayer.Stop();
        SoundPlayerMuffled.Stop();
    }

    void Update()
    {
        // Swap between muffled and full-volume based on proximity to the spawn point.
        // Will be replaced with something more robust in the future.
        PlayerInSpawn = Vector3.Distance(Networking.LocalPlayer.GetPosition(), transform.position) < 2f;

        // Change the audio source depending on the player's location in and out of the spawn lobby.
        SoundPlayerMuffled.volume = PlayerInSpawn ? 0.8f : 0f;
        SoundPlayer.volume = PlayerInSpawn ? 0f : 0.8f;

        ButtonText.text = _syncedPlaying ? "Join" : "Start";

        // Derive normalised local time directly from the audio sample position —
        // more accurate than tracking elapsed time manually
        float localTimeSeconds = SoundPlayer.timeSamples / SampleRate;
        LocalAnimationTime = localTimeSeconds / SoundPlayer.clip.length;

        // Fire OnTick events when the tick index advances.
        // Guards against audio resync jumps (large delta) and backward seeks (negative delta).
        if (SoundPlayer.isPlaying)
        {
            int currentTickIndex = Mathf.FloorToInt(LocalAnimationTime * SongBeats * TicksPerBeat);
            int delta = currentTickIndex - _lastTickIndex;

            if (delta > 0 && delta <= TicksPerBeat * 2)
            {
                // Set the TickIsMeasure and TickIsBeat flags so listeners can determine
                // the tick type without needing to do modulo math themselves.
                TickIsMeasure = currentTickIndex % (TicksPerBeat * BeatsPerMeasure) == 0;
                TickIsBeat = currentTickIndex % TicksPerBeat == 0;

                for (int i = 0; i < TickListeners.Length; i++)
                    if (TickListeners[i] != null)
                        TickListeners[i].SendCustomEvent("OnTick");
            }

            if (delta != 0)
                _lastTickIndex = currentTickIndex;
        }
        else
        {
            _lastTickIndex = -1;
        }

        if (Networking.IsOwner(Networking.LocalPlayer, gameObject))
        {
            // Owner drives the synced time from their local audio position,
            // then broadcasts it every frame for tight sync
            _syncedAnimationTime = LocalAnimationTime;
            for (int i = 0; i < animators.Length; i++)
                animators[i].SetFloat("_Time", _syncedAnimationTime);

            if (!SoundPlayer.isPlaying)
                _syncedPlaying = false;

            RequestSerialization();
        }
        else
        {
            // Non-owners animate from their local time, but resync audio if
            // drift against the owner exceeds one second.
            for (int i = 0; i < animators.Length; i++)
                animators[i].SetFloat("_Time", LocalAnimationTime);

            float localSecs = LocalAnimationTime * SongLengthInSeconds;
            float syncedSecs = _syncedAnimationTime * SongLengthInSeconds;
            if (Mathf.Abs(localSecs - syncedSecs) > 1.0f)
                PlayFromTime(_syncedAnimationTime);

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