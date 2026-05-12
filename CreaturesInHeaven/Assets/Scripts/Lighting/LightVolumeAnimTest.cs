
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

    void Start()
    {
        if (TargetVolume == null) return;

        VRCShader.SetGlobalVector(VRCShader.PropertyToID("_UdonLVAnimTest_UvwMin0"), TargetVolume.BoundsUvwMin0);
        VRCShader.SetGlobalVector(VRCShader.PropertyToID("_UdonLVAnimTest_UvwMax0"), TargetVolume.BoundsUvwMax0);
        VRCShader.SetGlobalVector(VRCShader.PropertyToID("_UdonLVAnimTest_UvwMin1"), TargetVolume.BoundsUvwMin1);
        VRCShader.SetGlobalVector(VRCShader.PropertyToID("_UdonLVAnimTest_UvwMax1"), TargetVolume.BoundsUvwMax1);
        VRCShader.SetGlobalVector(VRCShader.PropertyToID("_UdonLVAnimTest_UvwMin2"), TargetVolume.BoundsUvwMin2);
        VRCShader.SetGlobalVector(VRCShader.PropertyToID("_UdonLVAnimTest_UvwMax2"), TargetVolume.BoundsUvwMax2);
    }
}
