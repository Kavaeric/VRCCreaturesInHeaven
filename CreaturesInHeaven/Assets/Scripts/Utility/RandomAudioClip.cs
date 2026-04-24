
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;
using VRC.SDK3.UdonNetworkCalling;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class RandomAudioClip : UdonSharpBehaviour
{
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioSource tapeDeckAudioSource;
    [SerializeField] private AudioClip[] clips;
    [SerializeField] private AudioClip startOneshot;
    [SerializeField] private AudioClip stopOneshot;

    [SerializeField] private float oneShotVolume = 0.5f;

    [SerializeField] private float endCheckInterval = 0.5f;

    private bool wasPlaying;
    private float checkTimer;

    private void Update()
    {
        checkTimer -= Time.deltaTime;
        if (checkTimer > 0f) return;
        checkTimer = endCheckInterval;

        bool isPlaying = audioSource.isPlaying;
        if (wasPlaying && !isPlaying)
        {
            audioSource.PlayOneShot(stopOneshot, oneShotVolume);
            tapeDeckAudioSource.gameObject.SetActive(false);
        }
        wasPlaying = isPlaying;
    }

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

        // Grab a random clip from the array and play it.
        int index = Random.Range(0, clips.Length);
        SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayClip), index);
    }

    [NetworkCallable]
    public void PlayClip(int index)
    {
        audioSource.clip = clips[index];
        audioSource.Play();
        audioSource.PlayOneShot(startOneshot, oneShotVolume);
        tapeDeckAudioSource.gameObject.SetActive(true);
        wasPlaying = true;
    }

    [NetworkCallable]
    public void StopClip()
    {
        audioSource.Stop();
        audioSource.PlayOneShot(stopOneshot, oneShotVolume);
        tapeDeckAudioSource.gameObject.SetActive(false);
        wasPlaying = false;
        checkTimer = endCheckInterval;
    }
}
