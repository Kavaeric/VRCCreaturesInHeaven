using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using UnityEngine.UI;
using VRC.Udon.Common.Interfaces;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class CreaturesInHeaven : UdonSharpBehaviour
{
    // -- Song metadata ------------------------------------------------
    public float SampleRate { get; private set; }
    public float SongLengthInSeconds { get; private set; }
    public float SongSampleCount { get; private set; }
    public float SongBeats { get; private set; }
    public float SongMeasures { get; private set; }

    // -- Synced state -------------------------------------------------
    // Variables owned and written by the instance owner, then broadcast
    // to all other players via RequestSerialization().

    [UdonSynced] private float _syncedAnimationTime;
    public float SyncedAnimationTime => _syncedAnimationTime;

    [UdonSynced] private bool _syncedPlaying;
    public bool SyncedPlaying => _syncedPlaying;

    // -- Local state -------------------------------------------------
    // Derived each frame from the local AudioSource, independent of network.
    // Non-owners use this for display and drift detection.

    public float LocalAnimationTime { get; private set; }
    private bool _localPlaying;

    // -- Inspector references -----------------------------------------

    public AudioSource SoundPlayer;
    public AudioSource SoundPlayerMuffled;
    public Animator animator;
    public RelativeTeleport SpawnTeleporter;
    public Text ButtonText;
    public Text debugText;

    public void Start()
    {
        SongLengthInSeconds = SoundPlayer.clip.length;
        SampleRate = SoundPlayer.clip.samples / SoundPlayer.clip.length;
        SongSampleCount = SoundPlayer.clip.samples;
        SongBeats = SongLengthInSeconds * 1000.0f / 750.0f;
        SongMeasures = SongBeats / 4.0f;
    }

    public void _StartButtonPressed()
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
        bool playerInSpawn = Vector3.Distance(Networking.LocalPlayer.GetPosition(), transform.position) < 2f;
        SoundPlayerMuffled.volume = playerInSpawn ? 0.8f : 0f;
        SoundPlayer.volume = playerInSpawn ? 0f : 0.8f;

        ButtonText.text = _syncedPlaying ? "Join" : "Start";

        // Derive normalised local time directly from the audio sample position —
        // more accurate than tracking elapsed time manually
        float localTimeSeconds = SoundPlayer.timeSamples / SampleRate;
        LocalAnimationTime = localTimeSeconds / SoundPlayer.clip.length;

        if (Networking.IsOwner(Networking.LocalPlayer, gameObject))
        {
            // Owner drives the synced time from their local audio position,
            // then broadcasts it every frame for tight sync
            _syncedAnimationTime = LocalAnimationTime;
            animator.SetFloat("_Time", _syncedAnimationTime);

            if (!SoundPlayer.isPlaying)
                _syncedPlaying = false;

            RequestSerialization();
        }
        else
        {
            // Non-owners animate from their local time, but resync audio if
            // drift against the owner exceeds one second.
            animator.SetFloat("_Time", LocalAnimationTime);

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

        // Debug display nonsense
        debugText.text = "";
        debugText.text += "Time [sec]: " + (_syncedAnimationTime * SongLengthInSeconds).ToString("0.0") + " / " + SongLengthInSeconds.ToString("0") + "\n";
        debugText.text += "Time local [sec]: " + (LocalAnimationTime * SongLengthInSeconds).ToString("0.0") + " / " + SongLengthInSeconds.ToString("0") + "\n";
        debugText.text += "Network delta [sec]: " + Mathf.Abs((LocalAnimationTime * SongLengthInSeconds) - (_syncedAnimationTime * SongLengthInSeconds)) + "\n";
        debugText.text += "IsOwner: " + Networking.IsOwner(Networking.LocalPlayer, gameObject) + "\n";
        debugText.text += "Time [m:b:16]: "
            + Mathf.Floor((LocalAnimationTime * SongMeasures) + 1).ToString("0")
            + ":"
            + (Mathf.Floor((LocalAnimationTime * SongBeats)) % 4 + 1).ToString("0")
            + "."
            + Mathf.Floor(((LocalAnimationTime * SongBeats) - Mathf.Floor(LocalAnimationTime * SongBeats)) * 4 + 1).ToString("0")
            + "\n";
        debugText.text += "Beat index: " + Mathf.Floor(LocalAnimationTime * SongBeats).ToString("0") + " / " + SongBeats.ToString("0") + "\n";
        debugText.text += "Measure index: " + Mathf.Floor(LocalAnimationTime * SongMeasures).ToString("0") + " / " + SongMeasures.ToString("0") + "\n";
        debugText.text += "Main music current sample: " + SoundPlayer.timeSamples + "\n";
        debugText.text += "Muffled music current sample: " + SoundPlayerMuffled.timeSamples + "\n";
        debugText.text += "Main/Muffled sample delta (this should be close to 0): " + (SoundPlayer.timeSamples - SoundPlayerMuffled.timeSamples) + " \n";
    }
}