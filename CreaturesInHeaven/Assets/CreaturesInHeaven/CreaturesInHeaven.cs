
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
    float SampleRate = 0;
    float SongLengthInSeconds = 0;
    int SongSampleCount = 0;
    int SongBeats = 0;
    int SongMeasures = 0;
    
    public Text ButtonText;

    public AudioSource SoundPlayer;
    public AudioSource SoundPlayerMuffled;

    public Animator animator;

    public RelativeTeleport SpawnTeleporter;

    public Text debugText;

    [UdonSynced]
    float currentAnimationTime;
    float _currentAnimationTime;

    [UdonSynced]
    bool playing = false;
    bool _playing = false;

    public void Start()
    {
        SongLengthInSeconds = SoundPlayer.clip.length;
        SampleRate = SoundPlayer.clip.samples / SoundPlayer.clip.length;
        SongSampleCount = SoundPlayer.clip.samples;
        SongBeats = (int)(SongLengthInSeconds * 1000.0f / 750.0f);
        SongMeasures = SongBeats / 4;
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
        SoundPlayer.timeSamples = (int)currentAnimationTime * SongSampleCount;
        SoundPlayer.Play();
        SoundPlayer.timeSamples = (int)currentAnimationTime * SongSampleCount;

        SoundPlayerMuffled.Stop();
        SoundPlayerMuffled.timeSamples = (int)currentAnimationTime * SongSampleCount;
        SoundPlayerMuffled.Play();
        SoundPlayerMuffled.timeSamples = (int)currentAnimationTime * SongSampleCount;
    }

    void Update()
    {
        // maybe kind of flake-y, but good enough for now
        bool PlayerInSpawn = Vector3.Distance(Networking.LocalPlayer.GetPosition(), this.transform.position) < 2;

        SoundPlayerMuffled.volume = PlayerInSpawn ? 0.5f : 0;
        SoundPlayer.volume = PlayerInSpawn ? 0 : 1;

        ButtonText.text = playing ? "Join" : "Start";


        float currentActualReallyAccurateTimeProbably = SoundPlayer.timeSamples / SampleRate;

        if (Networking.IsOwner(Networking.LocalPlayer, gameObject))
        {
            currentAnimationTime = currentActualReallyAccurateTimeProbably / SoundPlayer.clip.length;

            animator.SetFloat("_Time", currentAnimationTime);

            RequestSerialization(); // Request serialization every frame for maximum sync speed
        }
        else
        {
            _currentAnimationTime = currentActualReallyAccurateTimeProbably / SoundPlayer.clip.length;

            animator.SetFloat("_Time", _currentAnimationTime);

            if (Mathf.Abs(_currentAnimationTime - currentAnimationTime) > 1.0f) // out of sync by more than one second
            {
                _currentAnimationTime = currentAnimationTime;
                PlayAtSamples(SoundPlayer, (int)currentAnimationTime * SongSampleCount);
            }

            if (_playing != playing)
            {
                _playing = playing;
                _currentAnimationTime = currentAnimationTime;
                PlayAtSamples(SoundPlayer, (int)currentAnimationTime * SongSampleCount);
            }
        }

        debugText.text = "";
        debugText.text += "Time [sec]: " + (currentAnimationTime * SongLengthInSeconds).ToString("0.0") + " / "+ SongLengthInSeconds.ToString("0") + "\n";
        debugText.text += "Time [m:b:16]: " 
            + Mathf.Floor(currentAnimationTime * SongMeasures + 1).ToString("0") 
            + ":" 
            + (Mathf.Floor(currentAnimationTime * SongBeats) % 4 + 1).ToString("0")
            + "."
            + Mathf.Floor(((currentAnimationTime * SongBeats) - Mathf.Floor(currentAnimationTime * SongBeats)) * 4 + 1).ToString("0")
            + "\n";
        debugText.text += "Beat index: " + Mathf.Floor(currentAnimationTime * SongBeats).ToString("0") + " / " + SongBeats.ToString("0") + "\n";
        debugText.text += "Measure index: " + Mathf.Floor(currentAnimationTime * SongMeasures).ToString("0") + " / " + SongMeasures.ToString("0") + "\n";
    }
}
