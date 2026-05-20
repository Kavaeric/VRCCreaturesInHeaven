using System;
using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class HeartacheTempoMonitor : UdonSharpBehaviour
{
    [SerializeField] private HeartacheMusicEngine MusicEngine;
    [SerializeField] private HeartacheAudienceManager AudienceManager;

    // --- Inspector references -----------------------------------------
    [Header("Readout")]
    [SerializeField] private TMP_Text ReadoutMetronome;

    [Header("Measure index")]
    [SerializeField] private TMP_Text ReadoutMeasureIndex;
    [SerializeField] private TMP_Text ReadoutMeasureIndexMax;

    [Header("Beat index")]
    [SerializeField] private TMP_Text ReadoutBeatIndex;
    [SerializeField] private TMP_Text ReadoutBeatIndexMax;

    [Header("Tick index")]
    [SerializeField] private TMP_Text ReadoutTickIndex;
    [SerializeField] private TMP_Text ReadoutTickIndexMax;

    [Space(12)]

    [Header("Progress bar")]
    [SerializeField] private TMP_Text ProgressBarTimeElapsed;
    [SerializeField] private TMP_Text ProgressBarTimeRemaining;
    [SerializeField] private TMP_Text ProgressBarTimeTotal;
    [Space(8)]
    [SerializeField] private RectTransform ProgressBarTransform;

    [Space(12)]

    [Header("Seconds elapsed")]
    [SerializeField] private Image PanelSecondsElapsedInstance;
    [SerializeField] private Image PanelSecondsElapsedLocal;
    [Space(4)]
    [SerializeField] private TMP_Text ReadoutSecondsElapsedInstance;
    [SerializeField] private TMP_Text ReadoutSecondsElapsedInstanceMax;
    [Space(4)]
    [SerializeField] private TMP_Text ReadoutSecondsElapsedLocal;
    [SerializeField] private TMP_Text ReadoutSecondsElapsedLocalMax;
    [Space(4)]
    [SerializeField] private TMP_Text ReadoutSecondsElapsedDelta;

    [Space(12)]

    [Header("Audio sample")]
    [SerializeField] private Image PanelAudioSampleMain;
    [SerializeField] private Image PanelAudioSampleLobby;
    [Space(8)]
    [SerializeField] private TMP_Text ReadoutAudioSampleMain;
    [SerializeField] private TMP_Text ReadoutAudioSampleLobby;
    [SerializeField] private TMP_Text ReadoutAudioSampleDelta;

    [Space(12)]

    [Header("Metronome")]
    [SerializeField] private AudioSource MetronomeSpeaker;
    [SerializeField] private AudioClip MetronomeClickMeasure;
    [SerializeField] private AudioClip MetronomeClickBeat;
    [SerializeField] private AudioClip MetronomeOn;
    [SerializeField] private AudioClip MetronomeOff;

    [Header("Other indicators")]
    [SerializeField] private GameObject IndicatorIsMetronomeOn;
    [SerializeField] private GameObject IndicatorIsOwner;

    private bool _metronomeEnabled = false;

    public override void Interact()
    {
        _metronomeEnabled = !_metronomeEnabled;
        MetronomeSpeaker.PlayOneShot(_metronomeEnabled ? MetronomeOn : MetronomeOff);
    }

    public void OnTick()
    {
        if (!_metronomeEnabled) return;
        if (MusicEngine.TickIsMeasure)
        {
            MetronomeSpeaker.PlayOneShot(MetronomeClickMeasure);
        }
        else if (MusicEngine.TickIsBeat)
        {
            MetronomeSpeaker.PlayOneShot(MetronomeClickBeat);
        }
    }

    // Formats a string, dimming its leading zeroes.
    private string DimLeadingZeros(string formatted)
    {
        int decimalPos = formatted.IndexOf('.');
        int cutoff = formatted.Length - 1; // default: dim all but last char

        for (int i = 0; i < formatted.Length - 1; i++)
        {
            char c = formatted[i];
            if (c == ' ' || c == '.') continue;
            if (c != '0')
            {
                cutoff = i;
                break;
            }
            // If we're about to hit the decimal, stop dimming here
            if (decimalPos != -1 && i == decimalPos - 1)
            {
                cutoff = i;
                break;
            }
        }

        return "<color=#FFFFFF10>" + formatted.Substring(0, cutoff) + "</color>" + formatted.Substring(cutoff);
    }

    private void Update()
    {
        float localTime = MusicEngine.LocalAnimationTime;
        float songSecs = localTime * MusicEngine.SongLengthInSeconds;

        // Metronome readout
        ReadoutMetronome.text = DimLeadingZeros(Mathf.Floor((localTime * MusicEngine.SongMeasures) + 1).ToString("000"))
            + "<color=#FFFFFF10>:</color>"
            + (Mathf.Floor((localTime * MusicEngine.SongBeats)) % MusicEngine.BeatsPerMeasure + 1).ToString("0")
            + "<color=#FFFFFF10>.</color>"
            + DimLeadingZeros((Mathf.Floor(localTime * MusicEngine.SongTicks) % MusicEngine.TicksPerBeat + 1).ToString("00"));

        // Measure index
        ReadoutMeasureIndex.text = DimLeadingZeros(Mathf.Floor(localTime * MusicEngine.SongMeasures).ToString("000"));
        ReadoutMeasureIndexMax.text = DimLeadingZeros((Mathf.Floor(MusicEngine.SongMeasures).ToString("000")));

        // Beat index
        ReadoutBeatIndex.text = DimLeadingZeros(Mathf.Floor(localTime * MusicEngine.SongBeats).ToString("000"));
        ReadoutBeatIndexMax.text = DimLeadingZeros(Mathf.Floor(MusicEngine.SongBeats).ToString("000"));

        // Tick index
        ReadoutTickIndex.text = DimLeadingZeros(Mathf.Floor(localTime * MusicEngine.SongTicks).ToString("0 000"));
        ReadoutTickIndexMax.text = DimLeadingZeros(Mathf.Floor(MusicEngine.SongTicks).ToString("0 000"));

        // Progress bar
        ProgressBarTimeElapsed.text = TimeSpan.FromSeconds(songSecs).ToString(@"m\:ss");
        ProgressBarTimeRemaining.text = "-" + TimeSpan.FromSeconds(MusicEngine.SongLengthInSeconds - songSecs).ToString(@"m\:ss");
        ProgressBarTimeTotal.text = TimeSpan.FromSeconds(MusicEngine.SongLengthInSeconds).ToString(@"m\:ss");
        ProgressBarTransform.transform.localScale = new Vector3(localTime, 1f, 1f);

        // Highlight seconds elapsed panels depending on ownership
        PanelSecondsElapsedInstance.enabled = MusicEngine.IsOwner;
        PanelSecondsElapsedLocal.enabled = !MusicEngine.IsOwner;

        float syncedSecs = MusicEngine.SyncedAnimationTime * MusicEngine.SongLengthInSeconds;
        ReadoutSecondsElapsedInstance.text = DimLeadingZeros(syncedSecs.ToString("000.000"));
        ReadoutSecondsElapsedInstanceMax.text = DimLeadingZeros(MusicEngine.SongLengthInSeconds.ToString("000.000"));

        ReadoutSecondsElapsedLocal.text = DimLeadingZeros(songSecs.ToString("000.000"));
        ReadoutSecondsElapsedLocalMax.text = DimLeadingZeros(MusicEngine.SongLengthInSeconds.ToString("000.000"));

        ReadoutSecondsElapsedDelta.text = Mathf.Abs(songSecs - syncedSecs).ToString("0.000");

        // Highlight audio sample panels depending on player location
        PanelAudioSampleMain.enabled = AudienceManager.WatchingAnimation;
        PanelAudioSampleLobby.enabled = !AudienceManager.WatchingAnimation;

        ReadoutAudioSampleMain.text = DimLeadingZeros(MusicEngine.MusicPlayer.timeSamples.ToString("00 000 000"));
        ReadoutAudioSampleLobby.text = DimLeadingZeros(MusicEngine.MusicPlayerLobby.timeSamples.ToString("00 000 000"));
        ReadoutAudioSampleDelta.text = (MusicEngine.MusicPlayer.timeSamples - MusicEngine.MusicPlayerLobby.timeSamples).ToString();

        // Turn on metronome indicator light depending on metronome state
        IndicatorIsMetronomeOn.SetActive(_metronomeEnabled);

        // Turn on ownership indicator light depending on IsOwner
        IndicatorIsOwner.SetActive(MusicEngine.IsOwner);
    }
}
