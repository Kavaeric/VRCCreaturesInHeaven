
using UdonSharp;
using UnityEngine;
using UnityEngine.Playables;
using VRC.SDKBase;
using VRC.Udon;

// Drives a PlayableDirector from MusicEngine's normalised time.
// Add to the same GameObject as the PlayableDirector, assign MusicEngine
// in the inspector, and add this behaviour to MusicEngine's SequenceListeners.
public class SequenceDirector : UdonSharpBehaviour
{
    public MusicEngine MusicEngine;
    public PlayableDirector Director;

    // Called every frame during playback by MusicEngine.
    public void OnSequenceUpdate()
    {
        Director.time = MusicEngine.LocalAnimationTime * MusicEngine.SongLengthInSeconds;
        Director.Evaluate();
    }

    // Called by MusicEngine when playback stops.
    public void OnSequenceStop()
    {
        Director.time = 0;
        Director.Evaluate();
    }
}
