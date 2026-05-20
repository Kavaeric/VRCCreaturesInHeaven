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
    [SerializeField] private HeartacheMusicEngine _musicEngine;
    [SerializeField] private HeartacheAudienceManager _audienceManager;

    // --- Inspector references -----------------------------------------
    [Header("Readout")]
    [SerializeField] private TMP_Text _readoutMetronome;

    [Header("Measure index")]
    [SerializeField] private TMP_Text _readoutMeasureIndex;
    [SerializeField] private TMP_Text _readoutMeasureIndexMax;

    [Header("Beat index")]
    [SerializeField] private TMP_Text _readoutBeatIndex;
    [SerializeField] private TMP_Text _readoutBeatIndexMax;

    [Header("Tick index")]
    [SerializeField] private TMP_Text _readoutTickIndex;
    [SerializeField] private TMP_Text _readoutTickIndexMax;

    [Space(12)]

    [Header("Progress bar")]
    [SerializeField] private TMP_Text _progressBarTimeElapsed;
    [SerializeField] private TMP_Text _progressBarTimeRemaining;
    [SerializeField] private TMP_Text _progressBarTimeTotal;
    [Space(8)]
    [SerializeField] private RectTransform _progressBarTransform;

    [Space(12)]

    [Header("Seconds elapsed")]
    [SerializeField] private Image _panelSecondsElapsedInstance;
    [SerializeField] private Image _panelSecondsElapsedLocal;
    [Space(4)]
    [SerializeField] private TMP_Text _readoutSecondsElapsedInstance;
    [SerializeField] private TMP_Text _readoutSecondsElapsedInstanceMax;
    [Space(4)]
    [SerializeField] private TMP_Text _readoutSecondsElapsedLocal;
    [SerializeField] private TMP_Text _readoutSecondsElapsedLocalMax;
    [Space(4)]
    [SerializeField] private TMP_Text _readoutSecondsElapsedDelta;

    [Space(12)]

    [Header("Audio sample")]
    [SerializeField] private Image _panelAudioSampleMain;
    [SerializeField] private Image _panelAudioSampleLobby;
    [Space(8)]
    [SerializeField] private TMP_Text _readoutAudioSampleMain;
    [SerializeField] private TMP_Text _readoutAudioSampleLobby;
    [SerializeField] private TMP_Text _readoutAudioSampleDelta;

    [Space(12)]

    [Header("Metronome")]
    [SerializeField] private AudioSource _metronomeSpeaker;
    [SerializeField] private AudioClip _metronomeClickMeasure;
    [SerializeField] private AudioClip _metronomeClickBeat;
    [SerializeField] private AudioClip _metronomeOn;
    [SerializeField] private AudioClip _metronomeOff;

    [Header("Other indicators")]
    [SerializeField] private GameObject _indicatorIsMetronomeOn;
    [SerializeField] private GameObject _indicatorIsOwner;

    private bool _metronomeEnabled = false;

    public override void Interact()
    {
        _metronomeEnabled = !_metronomeEnabled;
        _metronomeSpeaker.PlayOneShot(_metronomeEnabled ? _metronomeOn : _metronomeOff);
    }

    public void OnTick()
    {
        if (!_metronomeEnabled) return;
        if (_musicEngine.TickIsMeasure)
        {
            _metronomeSpeaker.PlayOneShot(_metronomeClickMeasure);
        }
        else if (_musicEngine.TickIsBeat)
        {
            _metronomeSpeaker.PlayOneShot(_metronomeClickBeat);
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
        float localTime = _musicEngine.LocalAnimationTime;
        float songSecs = localTime * _musicEngine.SongLengthInSeconds;

        // Metronome readout
        _readoutMetronome.text = DimLeadingZeros(Mathf.Floor((localTime * _musicEngine.SongMeasures) + 1).ToString("000"))
            + "<color=#FFFFFF10>:</color>"
            + (Mathf.Floor(localTime * _musicEngine.SongBeats) % _musicEngine.BeatsPerMeasure + 1).ToString("0")
            + "<color=#FFFFFF10>.</color>"
            + DimLeadingZeros((Mathf.Floor(localTime * _musicEngine.SongTicks) % _musicEngine.TicksPerBeat + 1).ToString("00"));

        // Measure index
        _readoutMeasureIndex.text = DimLeadingZeros(Mathf.Floor(localTime * _musicEngine.SongMeasures).ToString("000"));
        _readoutMeasureIndexMax.text = DimLeadingZeros((Mathf.Floor(_musicEngine.SongMeasures).ToString("000")));

        // Beat index
        _readoutBeatIndex.text = DimLeadingZeros(Mathf.Floor(localTime * _musicEngine.SongBeats).ToString("000"));
        _readoutBeatIndexMax.text = DimLeadingZeros(Mathf.Floor(_musicEngine.SongBeats).ToString("000"));

        // Tick index
        _readoutTickIndex.text = DimLeadingZeros(Mathf.Floor(localTime * _musicEngine.SongTicks).ToString("0 000"));
        _readoutTickIndexMax.text = DimLeadingZeros(Mathf.Floor(_musicEngine.SongTicks).ToString("0 000"));

        // Progress bar
        _progressBarTimeElapsed.text = TimeSpan.FromSeconds(songSecs).ToString(@"m\:ss");
        _progressBarTimeRemaining.text = "-" + TimeSpan.FromSeconds(_musicEngine.SongLengthInSeconds - songSecs).ToString(@"m\:ss");
        _progressBarTimeTotal.text = TimeSpan.FromSeconds(_musicEngine.SongLengthInSeconds).ToString(@"m\:ss");
        _progressBarTransform.transform.localScale = new Vector3(localTime, 1f, 1f);

        // Highlight seconds elapsed panels depending on ownership
        _panelSecondsElapsedInstance.enabled = _musicEngine.IsOwner;
        _panelSecondsElapsedLocal.enabled = !_musicEngine.IsOwner;

        float syncedSecs = _musicEngine.SyncedAnimationTime * _musicEngine.SongLengthInSeconds;
        _readoutSecondsElapsedInstance.text = DimLeadingZeros(syncedSecs.ToString("000.000"));
        _readoutSecondsElapsedInstanceMax.text = DimLeadingZeros(_musicEngine.SongLengthInSeconds.ToString("000.000"));

        _readoutSecondsElapsedLocal.text = DimLeadingZeros(songSecs.ToString("000.000"));
        _readoutSecondsElapsedLocalMax.text = DimLeadingZeros(_musicEngine.SongLengthInSeconds.ToString("000.000"));

        _readoutSecondsElapsedDelta.text = Mathf.Abs(songSecs - syncedSecs).ToString("0.000");

        // Highlight audio sample panels depending on player location
        _panelAudioSampleMain.enabled = _audienceManager.WatchingAnimation;
        _panelAudioSampleLobby.enabled = !_audienceManager.WatchingAnimation;

        _readoutAudioSampleMain.text = DimLeadingZeros(_musicEngine.MusicPlayer.timeSamples.ToString("00 000 000"));
        _readoutAudioSampleLobby.text = DimLeadingZeros(_musicEngine.MusicPlayerLobby.timeSamples.ToString("00 000 000"));
        _readoutAudioSampleDelta.text = (_musicEngine.MusicPlayer.timeSamples - _musicEngine.MusicPlayerLobby.timeSamples).ToString();

        // Turn on metronome indicator light depending on metronome state
        _indicatorIsMetronomeOn.SetActive(_metronomeEnabled);

        // Turn on ownership indicator light depending on IsOwner
        _indicatorIsOwner.SetActive(_musicEngine.IsOwner);
    }
}
