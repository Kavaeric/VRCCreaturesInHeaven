using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRCLightVolumes;

public enum MomentALVBlendingMode { Replace, Add, Subtract, Multiply }

// SH fidelity mode. Controls how many values are captured per voxel and how many
// SH textures are packed per snapshot (Z = depth × numSlots).
public enum MomentALVSHMode
{
    [InspectorName("L1")]     L1,
    [InspectorName("MonoL1")] MonoL1,
    [InspectorName("MonoL0")] MonoL0,
}

// Bit depth for the packed SH texture. Applies to whichever SH mode is in use.
public enum MomentALVBitDepth
{
    [InspectorName("8 bits per channel")]  Depth8,
    [InspectorName("16 bits per channel")] Depth16,
}

public static class MomentALVFormat
{
    // Unity's hard cap on any single Texture3D dimension on the platforms we target.
    // Used to decide when to wrap snapshots across columns instead of stacking them all on Y.
    public const int MaxTexture3DDimension = 2048;

    // Returns the number of SH texture slots for a given mode.
    // L1 = 3 slots, MonoL1 = 2 slots, MonoL0 = 1 slot.
    public static int NumSlots(MomentALVSHMode shMode)
    {
        if (shMode == MomentALVSHMode.L1)     return 3;
        if (shMode == MomentALVSHMode.MonoL1) return 2;
        return 1;
    }

    // Returns true when the packed texture uses UNORM encoding (values remapped to [0,1]).
    // The shader decodes back with value * 2 - 1.
    public static bool IsUnorm(MomentALVSHMode shMode, MomentALVBitDepth bitDepth) =>
        bitDepth == MomentALVBitDepth.Depth8 || (shMode == MomentALVSHMode.MonoL1 && bitDepth == MomentALVBitDepth.Depth16);

    // Packed texture layout (column-wrapped 2D flipbook):
    //   X = spatialW * numColumns         (snapshots tile horizontally once the Y stack is full)
    //   Y = spatialH * snapshotsPerColumn (snapshots fill a column before wrapping to the next)
    //   Z = spatialD * numSlots           (SH slots stacked along Z)
    //
    // snapshotsPerColumn is the maximum number of vertically-stacked snapshots that fit under the
    // 2048px cap; numColumns is the count needed to hold numSnapshots given that stack height.
    // Snapshot i lives at column c = i / snapshotsPerColumn, row r = i % snapshotsPerColumn,
    // i.e. column-major. Pixel origin: (c * spatialW, r * spatialH, 0).

    // How many snapshots of height spatialH fit in one column under the texture-size cap.
    // Result is at least 1 (we never split a single snapshot across columns).
    public static int SnapshotsPerColumn(int spatialH)
    {
        if (spatialH <= 0) return 1;
        return Mathf.Max(1, MaxTexture3DDimension / spatialH);
    }

    // How many columns are needed to fit numSnapshots given a per-column capacity.
    public static int NumColumns(int numSnapshots, int snapshotsPerColumn)
    {
        if (snapshotsPerColumn <= 0) return numSnapshots;
        return (numSnapshots + snapshotsPerColumn - 1) / snapshotsPerColumn;
    }

    // Convenience: total packed dimensions for a given spatial size and snapshot count.
    public static int PackedWidth(int spatialW, int numColumns) => spatialW * numColumns;
    public static int PackedHeight(int spatialH, int snapshotsPerColumn) => spatialH * snapshotsPerColumn;
    public static int PackedDepth(int spatialD, MomentALVSHMode shMode)  => spatialD * NumSlots(shMode);

    // Bytes per texel for the packed texture format. Mirrors the format selection in MomentTextureWriter.
    // Used for asset size estimation.
    // MonoL1 uses RGB formats (no alpha), all others use RGBA.
    public static int BytesPerTexel(MomentALVSHMode shMode, MomentALVBitDepth bitDepth)
    {
        if (shMode == MomentALVSHMode.MonoL1 && bitDepth == MomentALVBitDepth.Depth8) return 3; // RGB24
        if (shMode == MomentALVSHMode.MonoL1 && bitDepth == MomentALVBitDepth.Depth16) return 6; // RGB48
        if (bitDepth == MomentALVBitDepth.Depth8) return 4; // RGBA32
        return 8; // RGBAHalf
    }

    // VRAM occupied by a packed texture, in megabytes.
    public static double VramMB(int w, int h, int d, int numSnapshots, MomentALVSHMode shMode, MomentALVBitDepth bitDepth) =>
        (long)w * h * d * numSnapshots * (double)NumSlots(shMode) * BytesPerTexel(shMode, bitDepth) / (1024.0 * 1024.0);

    // AssetBundle compression ratios relative to uncompressed VRAM size.
    // Derived from noise (high/worst-case) and Gaussian-blob (low/realistic) bundle tests.
    // MonoL0 compresses better at the high end due to its sparser data.
    // See Moment-BUNDLE-SIZE.md at the repo root for methodology and full data.
    public const double BundleRatioLow    = 0.5;
    public const double BundleRatioHigh   = 0.9;
    public const double BundleRatioHighL0 = 0.7;

    public static double BundleHighRatio(MomentALVSHMode shMode) =>
        shMode == MomentALVSHMode.MonoL0 ? BundleRatioHighL0 : BundleRatioHigh;
}

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class MomentAnimatedLightVolume : UdonSharpBehaviour
{
    [Tooltip("The LightVolumeInstance whose atlas region this component writes into.")]
    public LightVolumeInstance TargetVolume;

    [Tooltip("The CustomRenderTexture that runs the CRT shader. Created and managed by the editor setup tool.")]
    public CustomRenderTexture Crt;

    [Tooltip("Packed 4D SH texture produced by the baking tool.")]
    public Texture3D AnimatedTexture;

    // Spatial size of one snapshot slice in the packed texture. Set from the sidecar by the editor.
    // SnapshotX is needed at runtime to compute the column-wrap UV offset; older sidecars that lack
    // it leave SnapshotX = 0, in which case the runtime falls back to single-column behaviour.
    [HideInInspector] public int SnapshotX;
    [HideInInspector] public int SnapshotY;

    // Column-wrap layout. NumColumnsBaked == 1 collapses to the original single-column layout,
    // so legacy sidecars (which set this to 1 on migration) behave identically to before.
    // NumSnapshotsBaked is the true snapshot count from the sidecar — it can be less than
    // SnapshotsPerColumn * NumColumnsBaked when the last column is only partially filled.
    [HideInInspector] public int SnapshotsPerColumn  = 1;
    [HideInInspector] public int NumColumnsBaked     = 1;
    [HideInInspector] public int NumSnapshotsBaked   = 0;

    // SH fidelity mode and bit depth of the packed texture. Set automatically by the
    // editor when AnimatedTexture is assigned via sidecar.
    [HideInInspector] public MomentALVSHMode   SHMode   = MomentALVSHMode.MonoL1;
    [HideInInspector] public MomentALVBitDepth BitDepth = MomentALVBitDepth.Depth8;

    // Editor-only voxel preview state. Controlled by MomentEInsAnimatedLightVolume inspector.
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
    [HideInInspector] public MomentALVSHMode   BakeSHMode   = MomentALVSHMode.L1;
    [HideInInspector] public MomentALVBitDepth BakeBitDepth = MomentALVBitDepth.Depth8;
    [HideInInspector] public string BakeOutputName = "ALV_Bake";
    [HideInInspector] public bool BakeSettingsFoldout = false;
#endif
    [Tooltip("How this volume's SH contribution is composited onto the atlas bake.")]
    public MomentALVBlendingMode Blending = MomentALVBlendingMode.Add;

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
    private MomentALVBlendingMode _blendMode;
    private bool _hasAnimTimeParam;
    private bool _hasIntensityParam;

    public int NumSnapshots { get; private set; }

    void Start()
    {
        if (Crt == null || TargetVolume == null || AnimatedTexture == null) return;

        // Switch the CRT to OnDemand so it stops issuing a draw call per slice every frame.
        // LightVolumeSetup forces Realtime when it (re)builds the post-processor chain, but
        // that only runs in the editor at bake time — at runtime we own the update cadence
        // and only need to refresh the atlas when our inputs actually change.
        Crt.updateMode = CustomRenderTextureUpdateMode.OnDemand;

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

        // Resolve layout. Sidecar populates SnapshotsPerColumn / NumColumnsBaked / NumSnapshotsBaked
        // at setup; older sidecars (or in-Editor assignment before ApplyTo runs) leave them at the
        // defaults, in which case we fall back to deriving from the texture's Y stack.
        int snapsPerCol = SnapshotsPerColumn > 0 ? SnapshotsPerColumn : 1;
        int numCols     = NumColumnsBaked    > 0 ? NumColumnsBaked    : 1;
        // Total snapshot count: trust the sidecar value when present. The grid (snapsPerCol * numCols)
        // is an upper bound, not the actual count — the last column can be partial. The texture-height
        // fallback only fires for legacy single-column atlases where the height divides exactly.
        if (NumSnapshotsBaked > 0)
            NumSnapshots = NumSnapshotsBaked;
        else
            NumSnapshots = SnapshotY > 0 ? (AnimatedTexture.height / SnapshotY) * numCols : snapsPerCol * numCols;
        int numSlots = MomentALVFormat.NumSlots(SHMode);
        _mat.SetInt  ("_NumSnapshots",       NumSnapshots);
        _mat.SetInt  ("_SnapshotsPerColumn", snapsPerCol);
        _mat.SetInt  ("_NumColumns",         numCols);
        // _SnapshotScale now means "1 / snapsPerCol" (V stride per snapshot within a column),
        // not "1 / numSnapshots" as before. The shader uses _ColumnScale for the U stride between
        // adjacent columns. Keeping the name avoids churning the shader property table.
        _mat.SetFloat("_SnapshotScale", 1f / snapsPerCol);
        _mat.SetFloat("_ColumnScale",   1f / numCols);
        _mat.SetFloat("_SliceScale",    1f / numSlots);

        _mat.SetInt("_SHMode",   (int)SHMode);
        _mat.SetInt("_BitDepth", (int)BitDepth);

        bool isUnorm = MomentALVFormat.IsUnorm(SHMode, BitDepth);
        _mat.SetInt("_IsUnorm", isUnorm ? 1 : 0);

        _mat.SetInt("_BlendMode", (int)Blending);
        _blendMode = Blending;

        float intensity = _hasIntensityParam ? _animator.GetFloat(IntensityParameter) : Intensity;
        _mat.SetFloat("_Intensity", intensity);
        _intensity = intensity;

        float animTime = _hasAnimTimeParam ? _animator.GetFloat(AnimTimeParameter) : Time;
        _mat.SetFloat("_Time4D", animTime);
        _prevTime = animTime;

        // Kick one update so the initial frame is composited into the atlas.
        // Without this the volume reads as black until something changes.
        Crt.Update();
    }

    void Update()
    {
        if (_mat == null) return;

        // Track whether anything changed this frame. If nothing did, we skip the CRT update
        // entirely — that's the whole point of switching to OnDemand. One ALV used to issue
        // numSlices draw calls every frame regardless of activity; now it issues zero when
        // the animation is paused or holding a frame.
        bool dirty = false;

        float animTime = _hasAnimTimeParam ? _animator.GetFloat(AnimTimeParameter) : Time;
        if (animTime != _prevTime)
        {
            _mat.SetFloat("_Time4D", animTime);
            _prevTime = animTime;
            dirty = true;
        }

        if (Blending != _blendMode)
        {
            _mat.SetInt("_BlendMode", (int)Blending);
            _blendMode = Blending;
            dirty = true;
        }

        float intensity = _hasIntensityParam ? _animator.GetFloat(IntensityParameter) : Intensity;
        if (intensity != _intensity)
        {
            _mat.SetFloat("_Intensity", intensity);
            _intensity = intensity;
            dirty = true;
        }

        if (dirty) Crt.Update();
    }

}
