
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

    // Shuffle-bag state (owner-only). 'order' is a shuffled list of clip
    // indices; we walk through it start to finish, then reshuffle. This
    // guarantees every track plays once per cycle instead of true-random
    // picking, which clumps and starves tracks, and is the technique used
    // for actual music shuffling in music players.
    private int[] order;
    private int position;

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

        // Pull the next clip from the shuffle bag rather than picking
        // truly at random, so every track gets played once per cycle.
        int index = NextIndex();
        SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayClip), index);
    }

    // Returns the next clip index from the shuffled order, reshuffling and
    // starting a fresh cycle whenever the current order is exhausted.
    private int NextIndex()
    {
        // (Re)build the order if it's missing or the clip list changed size.
        if (order == null || order.Length != clips.Length)
        {
            BuildShuffle();
        }
        else if (position >= order.Length)
        {
            // Reached the end of the cycle: reshuffle for a new pass.
            BuildShuffle();
        }

        int index = order[position];
        position++;
        return index;
    }

    // Fills 'order' with a freshly shuffled list of clip indices using a
    // Fisher-Yates shuffle, and resets the cursor to the start.
    private void BuildShuffle()
    {
        order = new int[clips.Length];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        // Fisher-Yates: walk from the end, swapping each slot with a random
        // earlier-or-equal slot. Produces an unbiased uniform shuffle.
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            int temp = order[i];
            order[i] = order[j];
            order[j] = temp;
        }

        position = 0;
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
