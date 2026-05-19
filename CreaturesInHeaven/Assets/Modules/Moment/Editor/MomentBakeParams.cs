using UnityEngine;
using VRCLightVolumes;

// Bake parameter arithmetic and validation for the AnimatedLightVolume bake tool.
public struct MomentBakeParams
{
    public AnimationClip Clip;
    public int StartFrame;
    public int EndFrame;       // -1 = use full clip length
    public int SnapshotCount;

    // Resolved bake window in seconds.
    public float BakeStart    => Clip != null ? Mathf.Clamp(StartFrame / Clip.frameRate, 0f, Clip.length) : 0f;
    public float BakeEnd      => Clip != null ? (EndFrame < 0 ? Clip.length : Mathf.Clamp(EndFrame / Clip.frameRate, BakeStart, Clip.length)) : 0f;
    public float BakeDuration => BakeEnd - BakeStart;

    // Returns the Animation window frame index for a given snapshot index (0-based).
    public int SnapshotToAnimFrame(int snapshotIndex)
    {
        if (Clip == null) return 0;
        float t = BakeStart + (SnapshotCount > 1 ? BakeDuration * snapshotIndex / (SnapshotCount - 1) : 0f);
        return Mathf.RoundToInt(t * Clip.frameRate);
    }

#if BAKERY_INCLUDED
    // Returns an error string if the params are not ready to bake, or null if valid.
    public string Validate(Animator animator, LightVolume volume)
    {
        if (animator == null) return "Assign an Animator to bake.";
        if (Clip == null)     return "Assign an Animation Clip to bake.";
        if (volume.BakeryVolume == null)
            return "Target Light Volume has no BakeryVolume child. Run a regular Bakery bake on it first to generate one.";
        return null;
    }
#endif
}
