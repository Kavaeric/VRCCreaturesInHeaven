
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;
using VRC.SDK3.UdonNetworkCalling;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class RandomAudioClip : UdonSharpBehaviour
{
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip[] clips;

    public override void Interact()
    {
        SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestPlay));
    }

    [NetworkCallable]
    public void RequestPlay()
    {
        if (audioSource.isPlaying)
        {
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(StopClip));
            return;
        }

        int index = Random.Range(0, clips.Length);
        SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayClip), index);
    }

    [NetworkCallable]
    public void PlayClip(int index)
    {
        audioSource.clip = clips[index];
        audioSource.Play();
    }

    [NetworkCallable]
    public void StopClip()
    {
        audioSource.Stop();
    }
}
