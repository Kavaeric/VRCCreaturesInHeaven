using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRCLightVolumes;

public enum ALVBlendingMode { Replace, Add, Subtract, Multiply }

// SH fidelity mode. Controls how many values are captured per voxel and how many
// SH textures are packed per sample (Z = depth × numSlots).
public enum ALVSHMode
{
    [InspectorName("L1")]     L1,
    [InspectorName("MonoL1")] MonoL1,
    [InspectorName("MonoL0")] MonoL0,
}

// Bit depth for the packed SH texture. Applies to whichever SH mode is in use.
public enum ALVBitDepth
{
    [InspectorName("8 bits per channel")]  Depth8,
    [InspectorName("16 bits per channel")] Depth16,
}

public static class ALVFormat
{
    // Returns the number of SH texture slots for a given mode.
    // L1 = 3 slots, MonoL1 = 2 slots, MonoL0 = 1 slot.
    public static int NumSlots(ALVSHMode shMode)
    {
        if (shMode == ALVSHMode.L1)     return 3;
        if (shMode == ALVSHMode.MonoL1) return 2;
        return 1;
    }

    // Returns true when the packed texture uses UNORM encoding (values remapped to [0,1]).
    // The shader decodes back with value * 2 - 1.
    public static bool IsUnorm(ALVSHMode shMode, ALVBitDepth bitDepth) =>
        bitDepth == ALVBitDepth.Depth8 || (shMode == ALVSHMode.MonoL1 && bitDepth == ALVBitDepth.Depth16);
}

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class AnimatedLightVolume : UdonSharpBehaviour
{
    [Tooltip("The LightVolumeInstance whose atlas region this component writes into.")]
    public LightVolumeInstance TargetVolume;

    [Tooltip("The CustomRenderTexture that runs the CRT shader. Created and managed by the editor setup tool.")]
    public CustomRenderTexture Crt;

    [Tooltip("Packed 4D SH texture produced by the baking tool.")]
    public Texture3D AnimatedTexture;

    // Y size of one sample slice in the packed texture (== ALVTextureInfo.sampleY).
    // Stored separately because height = sampleY * numSamples, and the two can't
    // be separated from texture dimensions alone when H != D.
    // Set automatically by the editor when AnimatedTexture is assigned via sidecar.
    [HideInInspector] public int SampleY;

    // SH fidelity mode and bit depth of the packed texture. Set automatically by the
    // editor when AnimatedTexture is assigned via sidecar.
    [HideInInspector] public ALVSHMode   SHMode   = ALVSHMode.MonoL1;
    [HideInInspector] public ALVBitDepth BitDepth = ALVBitDepth.Depth8;

    // Editor-only voxel preview state. Controlled by ALVEditor inspector.
    [HideInInspector] public bool PreviewVoxels = false;
    [HideInInspector] public int PreviewSample = 0;

#if UNITY_EDITOR
    // Bake settings — persisted here so the bake window can restore them when
    // this volume is selected. Editor-only; stripped from runtime builds.
    [HideInInspector] public Animator BakeAnimator;
    [HideInInspector] public AnimationClip BakeClip;
    [HideInInspector] public int BakeSampleCount = 8;
    [HideInInspector] public int BakeStartFrame = 0;
    [HideInInspector] public int BakeEndFrame = -1;
    [HideInInspector] public ALVSHMode   BakeSHMode   = ALVSHMode.L1;
    [HideInInspector] public ALVBitDepth BakeBitDepth = ALVBitDepth.Depth8;
    [HideInInspector] public string BakeOutputName = "ALV_Bake";
    [HideInInspector] public bool BakeSettingsFoldout = false;
#endif
    [Tooltip("How this volume's SH contribution is composited onto the atlas bake.")]
    public ALVBlendingMode Blending = ALVBlendingMode.Add;

    [Tooltip("Animator that drives playback.")]
    public Animator AnimatorSource;

    [Tooltip("Normalised playback position. 0 = first sample, 1 = last sample.")]
    [Range(0f, 1f)]
    public float Time = 0f;

    [Tooltip("Name of the float parameter on the Animator that overrides Time at runtime. Leave empty to use the field value above.")]
    public string AnimTimeParameter = "";

    [Tooltip("Intensity of the SH contribution. 0 = no contribution, 1 = full strength.")]
    public float Intensity = 1f;

    [Tooltip("Name of the float parameter on the Animator that overrides Intensity at runtime. Leave empty to use the field value above.")]
    public string IntensityParameter = "";

    private Animator _animator;
    private Material _mat;
    private float _prevTime = -1f;
    private float _intensity = -1f;
    private ALVBlendingMode _blendMode;
    private bool _hasAnimTimeParam;
    private bool _hasIntensityParam;

    public int NumSamples { get; private set; }

    void Start()
    {
        if (Crt == null || TargetVolume == null || AnimatedTexture == null) return;

        _animator = AnimatorSource;
        _mat = Crt.material;
        _hasAnimTimeParam  = _animator != null && AnimTimeParameter  != "";
        _hasIntensityParam = _animator != null && IntensityParameter != "";

        // Push static properties — these only change if the volume or texture changes.
        _mat.SetVector("_UvwMin0", TargetVolume.BoundsUvwMin0);
        _mat.SetVector("_UvwMax0", TargetVolume.BoundsUvwMax0);
        _mat.SetVector("_UvwMin1", TargetVolume.BoundsUvwMin1);
        _mat.SetVector("_UvwMax1", TargetVolume.BoundsUvwMax1);
        _mat.SetVector("_UvwMin2", TargetVolume.BoundsUvwMin2);
        _mat.SetVector("_UvwMax2", TargetVolume.BoundsUvwMax2);

        _mat.SetTexture("_PackedTex", AnimatedTexture);

        // Derive layout from texture dimensions.
        // Y = sampleY * numSamples. SampleY is stored separately because H and D
        // can differ, making them impossible to separate from texture dimensions alone.
        // Z = spatialD * numSlots, where numSlots = 3 (L1), 2 (MonoL1), or 1 (MonoL0).
        int numSlots = ALVFormat.NumSlots(SHMode);
        int sampleY  = SampleY > 0 ? SampleY : (AnimatedTexture.depth / numSlots);
        NumSamples = AnimatedTexture.height / sampleY;
        _mat.SetInt("_NumSamples", NumSamples);
        _mat.SetFloat("_SampleScale", 1f / NumSamples);
        _mat.SetFloat("_SliceScale", 1f / numSlots);

        _mat.SetInt("_SHMode",   (int)SHMode);
        _mat.SetInt("_BitDepth", (int)BitDepth);

        bool isUnorm = ALVFormat.IsUnorm(SHMode, BitDepth);
        _mat.SetInt("_IsUnorm", isUnorm ? 1 : 0);

        _mat.SetInt("_BlendMode", (int)Blending);
        _blendMode = Blending;

        float intensity = _hasIntensityParam ? _animator.GetFloat(IntensityParameter) : Intensity;
        _mat.SetFloat("_Intensity", intensity);
        _intensity = intensity;

        float animTime = _hasAnimTimeParam ? _animator.GetFloat(AnimTimeParameter) : Time;
        _mat.SetFloat("_Time4D", animTime);
        _prevTime = animTime;
    }

    void Update()
    {
        if (_mat == null) return;

        // Only push to the material when values actually change.
        float animTime = _hasAnimTimeParam ? _animator.GetFloat(AnimTimeParameter) : Time;
        if (animTime != _prevTime)
        {
            _mat.SetFloat("_Time4D", animTime);
            _prevTime = animTime;
        }

        if (Blending != _blendMode)
        {
            _mat.SetInt("_BlendMode", (int)Blending);
            _blendMode = Blending;
        }

        float intensity = _hasIntensityParam ? _animator.GetFloat(IntensityParameter) : Intensity;
        if (intensity != _intensity)
        {
            _mat.SetFloat("_Intensity", intensity);
            _intensity = intensity;
        }
    }

}
