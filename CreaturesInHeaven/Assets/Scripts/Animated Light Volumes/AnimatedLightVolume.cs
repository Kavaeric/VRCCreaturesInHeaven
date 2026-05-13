
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRCLightVolumes;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class AnimatedLightVolume : UdonSharpBehaviour
{
    [Tooltip("The LightVolumeInstance whose atlas region this component writes into.")]
    public LightVolumeInstance TargetVolume;

    [Tooltip("The CustomRenderTexture that runs the CRT shader. Created and managed by the editor setup tool.")]
    public CustomRenderTexture Crt;

    [Tooltip("Packed 4D SH texture produced by the baking tool. X = spatial, Y = time frames, Z = SH sub-textures.")]
    public Texture3D PackedTex;

    [Tooltip("Normalised playback position. 0 = first baked frame, 1 = last baked frame. Set this each frame from your driver script.")]
    [Range(0f, 1f)]
    public float AnimTime = 0f;

    [Tooltip("If true, SH contribution is added on top of the existing atlas bake. If false, it replaces it.")]
    public bool Additive = true;

    private Material _mat;
    private float _prevTime = -1f;
    private bool _prevAdditive;

    public int NumFrames { get; private set; }

    void Start()
    {
        if (Crt == null || TargetVolume == null || PackedTex == null) return;

        _mat = Crt.material;

        // Push static properties — these only change if the volume or texture changes.
        _mat.SetVector("_UvwMin0", TargetVolume.BoundsUvwMin0);
        _mat.SetVector("_UvwMax0", TargetVolume.BoundsUvwMax0);
        _mat.SetVector("_UvwMin1", TargetVolume.BoundsUvwMin1);
        _mat.SetVector("_UvwMax1", TargetVolume.BoundsUvwMax1);
        _mat.SetVector("_UvwMin2", TargetVolume.BoundsUvwMin2);
        _mat.SetVector("_UvwMax2", TargetVolume.BoundsUvwMax2);

        _mat.SetTexture("_PackedTex", PackedTex);

        // Derive layout from texture dimensions.
        // Y = H * numFrames, Z = D * 3.
        NumFrames = PackedTex.height / (PackedTex.depth / 3);
        _mat.SetFloat("_FrameScaleY", 1f / NumFrames);
        _mat.SetFloat("_SliceScaleZ", 1f / 3f);

        _mat.SetFloat("_Additive", Additive ? 1f : 0f);
        _prevAdditive = Additive;

        _mat.SetFloat("_Time4D", AnimTime);
        _prevTime = AnimTime;
    }

    void Update()
    {
        if (_mat == null) return;

        // Only push to the material when values actually change.
        if (AnimTime != _prevTime)
        {
            _mat.SetFloat("_Time4D", AnimTime);
            _prevTime = AnimTime;
        }

        if (Additive != _prevAdditive)
        {
            _mat.SetFloat("_Additive", Additive ? 1f : 0f);
            _prevAdditive = Additive;
        }
    }
}
