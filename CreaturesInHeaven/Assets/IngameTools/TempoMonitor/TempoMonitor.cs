using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using TMPro;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class TempoMonitor : UdonSharpBehaviour
{
    // Reference to the core music engine
    [SerializeField] private CreaturesInHeaven musicEngine;

    // Readouts
    [SerializeField] private TMP_Text ReadoutMetronome;

    private void Update()
    {
        ReadoutMetronome.text = musicEngine.SyncedAnimationTime.ToString();
    }
}