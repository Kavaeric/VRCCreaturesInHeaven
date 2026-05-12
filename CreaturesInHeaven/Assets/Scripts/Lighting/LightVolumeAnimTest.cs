
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRCLightVolumes;
using VRCShader = VRC.SDKBase.VRCShader;

// LightVolumeAnimTest
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class LightVolumeAnimTest : UdonSharpBehaviour
{
    public LightVolumeInstance TargetVolume;
    // Single packed Texture3D with all three SH sub-textures stacked along Z.
    public Texture3D PackedTex;
    // Must match the number of frames baked into PackedTex.
    public int NumFrames = 2;

    void Start()
    {
        if (TargetVolume == null) return;

        VRCShader.SetGlobalVector(VRCShader.PropertyToID("_UdonLVAnimTest_UvwMin0"), TargetVolume.BoundsUvwMin0);
        VRCShader.SetGlobalVector(VRCShader.PropertyToID("_UdonLVAnimTest_UvwMax0"), TargetVolume.BoundsUvwMax0);
        VRCShader.SetGlobalVector(VRCShader.PropertyToID("_UdonLVAnimTest_UvwMin1"), TargetVolume.BoundsUvwMin1);
        VRCShader.SetGlobalVector(VRCShader.PropertyToID("_UdonLVAnimTest_UvwMax1"), TargetVolume.BoundsUvwMax1);
        VRCShader.SetGlobalVector(VRCShader.PropertyToID("_UdonLVAnimTest_UvwMin2"), TargetVolume.BoundsUvwMin2);
        VRCShader.SetGlobalVector(VRCShader.PropertyToID("_UdonLVAnimTest_UvwMax2"), TargetVolume.BoundsUvwMax2);

        if (PackedTex != null)
        {
            VRCShader.SetGlobalTexture(VRCShader.PropertyToID("_UdonLVAnimTest_PackedTex"), PackedTex);

            // Derive layout from texture dimensions.
            // Y = H * numFrames, Z = D * 3. We don't store numFrames separately
            // so the caller must set NumFrames to match what was baked.
            VRCShader.SetGlobalFloat(VRCShader.PropertyToID("_UdonLVAnimTest_FrameScaleY"), 1.0f / NumFrames);
            VRCShader.SetGlobalFloat(VRCShader.PropertyToID("_UdonLVAnimTest_SliceScaleZ"), 1.0f / 3.0f);
            VRCShader.SetGlobalFloat(VRCShader.PropertyToID("_UdonLVAnimTest_NumFrames"), NumFrames);
        }
    }
}
