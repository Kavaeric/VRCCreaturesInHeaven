
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using TMPro;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class TempoMonitor : UdonSharpBehaviour
{
    private float SampleRate = 0;
    private float SongLengthInSeconds = 0;
    private float SongSampleCount = 0;
    private float SongBeats = 0;
    private float SongMeasures = 0;
    private float networkAnimationTime = 0;
    private float _currentAnimationTime = 0;

    [SerializeField] private CreaturesInHeaven musicEngine;

    [SerializeField] private TMP_Text ReadoutMetronome;

    void Start()
    {
        // Some stuff in here mayhaps
    }
    private void Update()
    {
        networkAnimationTime = musicEngine.networkAnimationTime;
        _currentAnimationTime = musicEngine._currentAnimationTime;

        ReadoutMetronome.text = networkAnimationTime.ToString();
    }
}
