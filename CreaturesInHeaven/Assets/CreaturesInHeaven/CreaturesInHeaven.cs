
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using UnityEngine.UI;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class CreaturesInHeaven : UdonSharpBehaviour
{
    public float SampleRate = 10;
    public float SongLengthInSeconds = 10;
    public int SongSampleCount = 10;

    public AudioSource SoundPlayer;
    public Animator animator;

    public Text debugText;
    public bool currentlyPlaying;

    [UdonSynced]
    float currentAnimationTime;
    float _currentAnimationTime;

    [UdonSynced]
    bool playing = true;
    bool _playing = true;

    public void Start()
    {
        SongLengthInSeconds = SoundPlayer.clip.length;
        SampleRate = SoundPlayer.clip.samples / SoundPlayer.clip.length;
        SongSampleCount = SoundPlayer.clip.samples;
    }

    public void _Play()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        _currentAnimationTime = 0;
        currentAnimationTime = 0;
        SoundPlayer.Stop();
        SoundPlayer.timeSamples = (int)currentAnimationTime * SongSampleCount;
        SoundPlayer.Play();
        SoundPlayer.timeSamples = (int)currentAnimationTime * SongSampleCount;
    }

    void Update()
    {
        float currentActualReallyAccurateTimeProbably = SoundPlayer.timeSamples / SampleRate;

        if (Networking.IsOwner(Networking.LocalPlayer, gameObject))
        {
            currentAnimationTime = currentActualReallyAccurateTimeProbably / SoundPlayer.clip.length;

            animator.SetFloat("_Time", currentAnimationTime);

            RequestSerialization(); // Always request serializatio for maximum sync speed
        }
        else
        {
            _currentAnimationTime = currentActualReallyAccurateTimeProbably / SoundPlayer.clip.length;

            if (Mathf.Abs(_currentAnimationTime - currentAnimationTime) > 1.0f) // out of sync by more than one second
            {
                _currentAnimationTime = currentAnimationTime;
                SoundPlayer.Stop();
                SoundPlayer.timeSamples = (int)currentAnimationTime * SongSampleCount;
                SoundPlayer.Play();
                SoundPlayer.timeSamples = (int)currentAnimationTime * SongSampleCount;
            }
            
            if (_playing != playing)
            {
                _playing = playing;
                _currentAnimationTime = currentAnimationTime;
                SoundPlayer.Stop();
                SoundPlayer.timeSamples = (int)currentAnimationTime * SongSampleCount;
                SoundPlayer.Play();
                SoundPlayer.timeSamples = (int)currentAnimationTime * SongSampleCount;
            }
        }

        debugText.text = "";
        debugText.text += nameof(currentlyPlaying) + ": " + currentlyPlaying + "\n";
        debugText.text += nameof(currentAnimationTime) + ": " + currentAnimationTime + "\n";
    }
}
