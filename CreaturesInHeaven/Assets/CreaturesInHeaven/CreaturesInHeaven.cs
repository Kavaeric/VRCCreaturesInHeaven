
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using UnityEngine.UI;
using System.Diagnostics.Eventing.Reader;
using VRC.Udon.Common.Interfaces;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class CreaturesInHeaven : UdonSharpBehaviour
{
    // Precalculated song and audio properties
    public float SampleRate { get; private set; } = 0;
    public float SongLengthInSeconds { get; private set; } = 0;
    public float SongSampleCount { get; private set; } = 0;
    public float SongBeats { get; private set; } = 0;
    public float SongMeasures { get; private set; } = 0;

    // Music engine state
    [UdonSynced]
    private float currentAnimationTime;
    public float networkAnimationTime => currentAnimationTime;
    public float _currentAnimationTime { get; private set; }

    [UdonSynced]
    bool playing = false;
    bool _playing = false;

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
        if (playing)
        {
            SpawnTeleporter.TriggerLocal(); // For now, exact behaviour will depend on how the animation works.
        }
        else
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
            _currentAnimationTime = 0;
            currentAnimationTime = 0;
            PlayAtSamples(SoundPlayer, 0);
            playing = true;

            SpawnTeleporter._NetworkTrigger();
        }
    }

    void PlayAtSamples(AudioSource source, int sampleIndex)
    {
        SoundPlayer.Stop();
        SoundPlayer.timeSamples = (int)(currentAnimationTime * SongSampleCount);
        SoundPlayer.Play();
        SoundPlayer.timeSamples = (int)(currentAnimationTime * SongSampleCount);

        SoundPlayerMuffled.Stop();
        SoundPlayerMuffled.timeSamples = (int)(currentAnimationTime * SongSampleCount);
        SoundPlayerMuffled.Play();
        SoundPlayerMuffled.timeSamples = (int)(currentAnimationTime * SongSampleCount);
    }

    void StopPlaying()
    {
        SoundPlayer.Stop();
        SoundPlayerMuffled.Stop();
    }

    void Update()
    {
        // maybe kind of flake-y, but good enough for now
        bool PlayerInSpawn = Vector3.Distance(Networking.LocalPlayer.GetPosition(), this.transform.position) < 2;

        SoundPlayerMuffled.volume = PlayerInSpawn ? 0.8f : 0;
        SoundPlayer.volume = PlayerInSpawn ? 0 : 0.8f;

        ButtonText.text = playing ? "Join" : "Start";

        float currentActualReallyAccurateTimeProbably = SoundPlayer.timeSamples / SampleRate;

        if (Networking.IsOwner(Networking.LocalPlayer, gameObject))
        {
            currentAnimationTime = currentActualReallyAccurateTimeProbably / SoundPlayer.clip.length;
            _currentAnimationTime = currentAnimationTime;

            animator.SetFloat("_Time", (float)currentAnimationTime);

            if (!SoundPlayer.isPlaying) // done playing!
            {
                playing = false;
            }

            RequestSerialization(); // Request serialization every frame for maximum sync speed
        }
        else
        {
            _currentAnimationTime = currentActualReallyAccurateTimeProbably / SoundPlayer.clip.length;

            animator.SetFloat("_Time", (float)_currentAnimationTime);

            if (Mathf.Abs((float)((_currentAnimationTime * SongLengthInSeconds) - (currentAnimationTime * SongLengthInSeconds))) > 1.0f) // out of sync by more than one second
            {
                PlayAtSamples(SoundPlayer, (int)(currentAnimationTime * SongSampleCount));
            }

            if (_playing != playing)
            {
                _playing = playing;
                if (playing)
                    PlayAtSamples(SoundPlayer, (int)(currentAnimationTime * SongSampleCount));
                else
                    StopPlaying();
            }
        }

        debugText.text = "";
        debugText.text += "Time [sec]: " + (currentAnimationTime * SongLengthInSeconds).ToString("0.0") + " / " + SongLengthInSeconds.ToString("0") + "\n";
        debugText.text += "Time local [sec]: " + (_currentAnimationTime * SongLengthInSeconds).ToString("0.0") + " / " + SongLengthInSeconds.ToString("0") + "\n";
        debugText.text += "Network delta [sec]: " + Mathf.Abs((_currentAnimationTime * SongLengthInSeconds) - (currentAnimationTime * SongLengthInSeconds)) + "\n";
        debugText.text += "IsOwner: " + Networking.IsOwner(Networking.LocalPlayer, gameObject) + "\n";
        debugText.text += "Time [m:b:16]: " 
            + Mathf.Floor((_currentAnimationTime * SongMeasures) + 1).ToString("0") 
            + ":" 
            + (Mathf.Floor((_currentAnimationTime * SongBeats)) % 4 + 1).ToString("0")
            + "."
            + Mathf.Floor(((_currentAnimationTime * SongBeats) - Mathf.Floor(_currentAnimationTime * SongBeats)) * 4 + 1).ToString("0")
            + "\n";
        debugText.text += "Beat index: " + Mathf.Floor(_currentAnimationTime * SongBeats).ToString("0") + " / " + SongBeats.ToString("0") + "\n";
        debugText.text += "Measure index: " + Mathf.Floor(_currentAnimationTime * SongMeasures).ToString("0") + " / " + SongMeasures.ToString("0") + "\n";
        debugText.text += "Main music current sample: " + SoundPlayer.timeSamples + "\n";
        debugText.text += "Muffled music current sample: " + SoundPlayerMuffled.timeSamples + "\n";
        debugText.text += "Main/Muffled sample delta (this should be close to 0): "+ (SoundPlayer.timeSamples - SoundPlayerMuffled.timeSamples) + " \n";

    }
}
