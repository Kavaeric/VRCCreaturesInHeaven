using System;
using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class TempoMonitor : UdonSharpBehaviour
{
    // Reference to the core music engine
    [SerializeField] private CreaturesInHeaven musicEngine;

    // --- Inspector references -----------------------------------------
    [SerializeField] private TMP_Text ReadoutMetronome;

    [SerializeField] private TMP_Text ReadoutMeasureIndex;
    [SerializeField] private TMP_Text ReadoutMeasureIndexMax;

    [SerializeField] private TMP_Text ReadoutBeatIndex;
    [SerializeField] private TMP_Text ReadoutBeatIndexMax;

    [SerializeField] private TMP_Text ReadoutStepIndex;
    [SerializeField] private TMP_Text ReadoutStepIndexMax;

    [SerializeField] private TMP_Text ProgressBarTimeElapsed;
    [SerializeField] private TMP_Text ProgressBarTimeRemaining;
    [SerializeField] private TMP_Text ProgressBarTimeTotal;
    [SerializeField] private RectTransform ProgressBarTransform;

    [SerializeField] private Image PanelSecondsElapsedInstance;
    [SerializeField] private Image PanelSecondsElapsedLocal;

    [SerializeField] private TMP_Text ReadoutSecondsElapsedInstance;
    [SerializeField] private TMP_Text ReadoutSecondsElapsedInstanceMax;
    [SerializeField] private TMP_Text ReadoutSecondsElapsedLocal;
    [SerializeField] private TMP_Text ReadoutSecondsElapsedLocalMax;
    [SerializeField] private TMP_Text ReadoutSecondsElapsedDelta;

    [SerializeField] private Image PanelAudioSampleMain;
    [SerializeField] private Image PanelAudioSampleLobby;

    [SerializeField] private TMP_Text ReadoutAudioSampleMain;
    [SerializeField] private TMP_Text ReadoutAudioSampleLobby;
    [SerializeField] private TMP_Text ReadoutAudioSampleDelta;

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
        ReadoutMetronome.text = DimLeadingZeros(Mathf.Floor((musicEngine.LocalAnimationTime * musicEngine.SongMeasures) + 1).ToString("000"))
            + "<color=#FFFFFF10>:</color>"
            + (Mathf.Floor((musicEngine.LocalAnimationTime * musicEngine.SongBeats)) % 4 + 1).ToString("0")
            + "<color=#FFFFFF10>.</color>"
            + DimLeadingZeros(Mathf.Floor(((musicEngine.LocalAnimationTime * musicEngine.SongBeats) - Mathf.Floor(musicEngine.LocalAnimationTime * musicEngine.SongBeats)) * 4 + 1).ToString("00"));

        ReadoutMeasureIndex.text = DimLeadingZeros(Mathf.Floor(musicEngine.LocalAnimationTime * musicEngine.SongMeasures).ToString("000"));
        ReadoutMeasureIndexMax.text = DimLeadingZeros((Mathf.Floor(musicEngine.SongMeasures).ToString("000")));

        ReadoutBeatIndex.text = DimLeadingZeros(Mathf.Floor(musicEngine.LocalAnimationTime * musicEngine.SongBeats).ToString("000"));
        ReadoutBeatIndexMax.text = DimLeadingZeros(Mathf.Floor(musicEngine.SongBeats).ToString("000"));

        ReadoutStepIndex.text = DimLeadingZeros(Mathf.Floor(musicEngine.LocalAnimationTime * musicEngine.SongBeats).ToString("000"));
        ReadoutStepIndexMax.text = DimLeadingZeros(Mathf.Floor(musicEngine.SongBeats).ToString("000"));

        ProgressBarTimeElapsed.text = TimeSpan.FromSeconds(musicEngine.LocalAnimationTime * musicEngine.SongLengthInSeconds).ToString(@"m\:ss");
        ProgressBarTimeRemaining.text = "-" + TimeSpan.FromSeconds(musicEngine.SongLengthInSeconds - musicEngine.LocalAnimationTime * musicEngine.SongLengthInSeconds).ToString(@"m\:ss");
        ProgressBarTimeTotal.text = TimeSpan.FromSeconds(musicEngine.SongLengthInSeconds).ToString(@"m\:ss");
        ProgressBarTransform.transform.localScale = new Vector3(musicEngine.LocalAnimationTime, 1f, 1f);

        PanelSecondsElapsedInstance.enabled = musicEngine.IsOwner;
        PanelSecondsElapsedLocal.enabled = !musicEngine.IsOwner;

        ReadoutSecondsElapsedInstance.text = DimLeadingZeros((musicEngine.SyncedAnimationTime * musicEngine.SongLengthInSeconds).ToString("000.000"));
        ReadoutSecondsElapsedInstanceMax.text = DimLeadingZeros((musicEngine.SongLengthInSeconds).ToString("000.000"));

        ReadoutSecondsElapsedLocal.text = DimLeadingZeros((musicEngine.LocalAnimationTime * musicEngine.SongLengthInSeconds).ToString("000.000"));
        ReadoutSecondsElapsedLocalMax.text = DimLeadingZeros((musicEngine.SongLengthInSeconds).ToString("000.000"));

        ReadoutSecondsElapsedDelta.text = Mathf.Abs((musicEngine.LocalAnimationTime * musicEngine.SongLengthInSeconds) - (musicEngine.SyncedAnimationTime * musicEngine.SongLengthInSeconds)).ToString("0.000");

        PanelAudioSampleMain.enabled = !musicEngine.PlayerInSpawn;
        PanelAudioSampleLobby.enabled = musicEngine.PlayerInSpawn;

        ReadoutAudioSampleMain.text = DimLeadingZeros(musicEngine.SoundPlayer.timeSamples.ToString("00 000 000"));
        ReadoutAudioSampleLobby.text = DimLeadingZeros(musicEngine.SoundPlayerMuffled.timeSamples.ToString("00 000 000"));
        ReadoutAudioSampleDelta.text = (musicEngine.SoundPlayer.timeSamples - musicEngine.SoundPlayerMuffled.timeSamples).ToString();
    }
}