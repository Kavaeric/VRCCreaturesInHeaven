
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRCLightVolumes;

public enum ALVBlendingMode { Replace, Add, Subtract, Multiply }

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

    // Editor-only voxel preview state. Controlled by ALVEditor inspector.
    [HideInInspector] public bool PreviewVoxels = false;
    [HideInInspector] public int PreviewSample = 0;
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
        // Y = spatialH * numSamples, Z = D * 3. SampleY is stored separately
        // because H and D can differ, making them impossible to separate from texture
        // dimensions alone.
        int spatialH = SampleY > 0 ? SampleY : (AnimatedTexture.depth / 3);
        NumSamples = AnimatedTexture.height / spatialH;
        _mat.SetInt("_NumSamples", NumSamples);
        _mat.SetFloat("_SampleScale", 1f / NumSamples);
        _mat.SetFloat("_SliceScale", 1f / 3f);

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
