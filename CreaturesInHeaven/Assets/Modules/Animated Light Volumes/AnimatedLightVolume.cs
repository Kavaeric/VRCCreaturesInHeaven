using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRCLightVolumes;

public enum ALVBlendingMode { Replace, Add, Subtract, Multiply }

// SH fidelity mode. Controls how many values are captured per voxel and how many
// SH textures are packed per snapshot (Z = depth × numSlots).
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

    // Packed texture layout:
    //   X = spatial width  (unchanged)
    //   Y = spatial height * numSnapshots  (snapshot index stacked along Y)
    //   Z = spatial depth  * numSlots      (slot index stacked along Z)

    public static int PackedHeight(int spatialH, int numSnapshots) => spatialH * numSnapshots;
    public static int PackedDepth(int spatialD, ALVSHMode shMode)  => spatialD * NumSlots(shMode);

    // Bytes per texel for the packed texture format. Mirrors the format selection in ALVTextureWriter.
    // Used for asset size estimation.
    // MonoL1 uses RGB formats (no alpha), all others use RGBA.
    public static int BytesPerTexel(ALVSHMode shMode, ALVBitDepth bitDepth)
    {
        if (shMode == ALVSHMode.MonoL1 && bitDepth == ALVBitDepth.Depth8) return 3; // RGB24
        if (shMode == ALVSHMode.MonoL1 && bitDepth == ALVBitDepth.Depth16) return 6; // RGB48
        if (bitDepth == ALVBitDepth.Depth8) return 4; // RGBA32
        return 8; // RGBAHalf
    }

    // VRAM occupied by a packed texture, in megabytes.
    public static double VramMB(int w, int h, int d, int numSnapshots, ALVSHMode shMode, ALVBitDepth bitDepth) =>
        (long)w * h * d * numSnapshots * (double)NumSlots(shMode) * BytesPerTexel(shMode, bitDepth) / (1024.0 * 1024.0);

    // AssetBundle compression ratios relative to uncompressed VRAM size.
    // Derived from noise (high/worst-case) and Gaussian-blob (low/realistic) bundle tests.
    // MonoL0 compresses better at the high end due to its sparser data.
    // See ALV-BUNDLE-SIZE.md at the repo root for methodology and full data.
    public const double BundleRatioLow    = 0.5;
    public const double BundleRatioHigh   = 0.9;
    public const double BundleRatioHighL0 = 0.7;

    public static double BundleHighRatio(ALVSHMode shMode) =>
        shMode == ALVSHMode.MonoL0 ? BundleRatioHighL0 : BundleRatioHigh;
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

    // Y size of one snapshot slice in the packed texture. Set from the sidecar by the editor.
    [HideInInspector] public int SnapshotY;

    // SH fidelity mode and bit depth of the packed texture. Set automatically by the
    // editor when AnimatedTexture is assigned via sidecar.
    [HideInInspector] public ALVSHMode   SHMode   = ALVSHMode.MonoL1;
    [HideInInspector] public ALVBitDepth BitDepth = ALVBitDepth.Depth8;

    // Editor-only voxel preview state. Controlled by ALVEditor inspector.
    [HideInInspector] public bool PreviewVoxels = false;
    [HideInInspector] public int PreviewSnapshot = 0;

#if UNITY_EDITOR
    // Bake settings. Persisted here so the bake window can restore them when
    // this volume is selected. Editor-only; stripped from runtime builds.
    [HideInInspector] public Animator BakeAnimator;
    [HideInInspector] public AnimationClip BakeClip;
    [HideInInspector] public int BakeSnapshotCount = 8;
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

    [Tooltip("Normalised playback position. 0 = first snapshot, 1 = last snapshot.")]
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

    public int NumSnapshots { get; private set; }

    void Start()
    {
        if (Crt == null || TargetVolume == null || AnimatedTexture == null) return;

        _animator = AnimatorSource;
        _mat = Crt.material;
        _hasAnimTimeParam  = _animator != null && AnimTimeParameter  != "";
        _hasIntensityParam = _animator != null && IntensityParameter != "";

        // Push static properties, though only if the volume or texture changes.
        _mat.SetVector("_UvwMin0", TargetVolume.BoundsUvwMin0);
        _mat.SetVector("_UvwMax0", TargetVolume.BoundsUvwMax0);
        _mat.SetVector("_UvwMin1", TargetVolume.BoundsUvwMin1);
        _mat.SetVector("_UvwMax1", TargetVolume.BoundsUvwMax1);
        _mat.SetVector("_UvwMin2", TargetVolume.BoundsUvwMin2);
        _mat.SetVector("_UvwMax2", TargetVolume.BoundsUvwMax2);

        _mat.SetTexture("_PackedTex", AnimatedTexture);

        NumSnapshots = AnimatedTexture.height / SnapshotY;
        int numSlots = ALVFormat.NumSlots(SHMode);
        _mat.SetInt("_NumSnapshots", NumSnapshots);
        _mat.SetFloat("_SnapshotScale", 1f / NumSnapshots);
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
