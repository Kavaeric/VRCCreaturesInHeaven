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
    // This is the *real* allocation Unity makes: the column-wrap layout pads the last column up to
    // snapshotsPerColumn rows, so the texture can be larger than the raw snapshot data needs. We size
    // from the packed dimensions (PackedWidth/Height/Depth) rather than numSnapshots so the figure
    // matches what SavePackedTexture/InitialiseTexture actually create. See PackingEfficiency for the
    // ratio of useful data to allocated size.
    public static double VramMB(int w, int h, int d, int numSnapshots, MomentALVSHMode shMode, MomentALVBitDepth bitDepth)
    {
        int snapshotsPerColumn = SnapshotsPerColumn(h);
        int numColumns         = NumColumns(numSnapshots, snapshotsPerColumn);
        long totalW = PackedWidth (w, numColumns);
        long totalH = PackedHeight(h, snapshotsPerColumn);
        long totalD = PackedDepth (d, shMode);
        return totalW * totalH * totalD * BytesPerTexel(shMode, bitDepth) / (1024.0 * 1024.0);
    }

    // Fraction (0..1) of the allocated packed texture that holds real snapshot data, the rest being
    // column-wrap padding. 1.0 means the grid is exactly filled (numSnapshots is a multiple of
    // snapshotsPerColumn, or fits in a single column); lower means the last column is partial and
    // some allocated rows are wasted. Returns 0 for invalid input.
    public static double PackingEfficiency(int h, int numSnapshots)
    {
        if (h <= 0 || numSnapshots <= 0) return 0.0;
        int snapshotsPerColumn = SnapshotsPerColumn(h);
        int numColumns         = NumColumns(numSnapshots, snapshotsPerColumn);
        int gridCells          = snapshotsPerColumn * numColumns;
        return gridCells > 0 ? (double)numSnapshots / gridCells : 0.0;
    }

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

    // Flipbooks the volume can play, stored as parallel arrays indexed by flipbook. UdonSharp can't
    // read fields off elements of a serializable-class array (it throws "Field access for
    // ImportedUdonSharpFieldSymbol is not implemented"), so the flipbook data is flattened into one
    // array per field instead of an array of structs. The editor keeps these in lockstep; index i of
    // every array describes flipbook i. The custom inspector draws them as a unified list.
    [Tooltip("Packed 4D SH textures, one per flipbook. Index 0 is bound on Start. Swap at runtime via the index parameter below; an index of -1 or out of range makes the pass a passthrough (contributes nothing).")]
    public Texture3D[] FlipbookTextures;
    [HideInInspector] public int[] FlipbookSnapshotX;
    [HideInInspector] public int[] FlipbookSnapshotY;
    [HideInInspector] public int[] FlipbookSnapshotsPerColumn;
    [HideInInspector] public int[] FlipbookNumColumns;
    [HideInInspector] public int[] FlipbookNumSnapshots;
    // SH mode / bit depth stored as ints (enum cast) — Udon serialises these cleanly and the shader
    // wants the int anyway. 0/1/2 = L1/MonoL1/MonoL0; bit depth 0/1 = Depth8/Depth16.
    [HideInInspector] public int[] FlipbookSHMode;
    [HideInInspector] public int[] FlipbookBitDepth;

    [Tooltip("Name of the Animator Float parameter that selects which flipbook is active (rounded to the nearest index; use Constant keyframe tangents). -1 or any out-of-range value makes the pass a passthrough. Leave empty to always use index 0.")]
    public string FlipbookIndexParameter = "";

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
    // Which Flipbooks[] slot the Setup/Baker windows target.
    [HideInInspector] public int BakeTargetSlot = 0;
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

    // Flipbook swap state. _activeFlipbook tracks which entry is currently bound (-1 = none/passthrough),
    // so BindFlipbook can early-out when the requested index is unchanged.
    private bool _hasFlipbookParam;
    private int  _activeFlipbook = -1;

    public int NumSnapshots { get; private set; }

    void Start()
    {
        if (Crt == null || TargetVolume == null || FlipbookTextures == null || FlipbookTextures.Length == 0) return;

        // Switch the CRT to OnDemand so it stops issuing a draw call per slice every frame.
        // LightVolumeSetup forces Realtime when it (re)builds the post-processor chain, but
        // that only runs in the editor at bake time — at runtime we own the update cadence
        // and only need to refresh the atlas when our inputs actually change.
        Crt.updateMode = CustomRenderTextureUpdateMode.OnDemand;

        _animator = AnimatorSource;
        _mat = Crt.material;
        _hasAnimTimeParam  = _animator != null && AnimTimeParameter  != "";
        _hasIntensityParam = _animator != null && IntensityParameter != "";
        _hasFlipbookParam  = _animator != null && FlipbookIndexParameter != "";

        // Push static properties, though only if the volume or texture changes.
        _mat.SetVector("_UvwMin0", TargetVolume.BoundsUvwMin0);
        _mat.SetVector("_UvwMax0", TargetVolume.BoundsUvwMax0);
        _mat.SetVector("_UvwMin1", TargetVolume.BoundsUvwMin1);
        _mat.SetVector("_UvwMax1", TargetVolume.BoundsUvwMax1);
        _mat.SetVector("_UvwMin2", TargetVolume.BoundsUvwMin2);
        _mat.SetVector("_UvwMax2", TargetVolume.BoundsUvwMax2);

        _mat.SetInt("_BlendMode", (int)Blending);
        _blendMode = Blending;

        float intensity = _hasIntensityParam ? _animator.GetFloat(IntensityParameter) : Intensity;
        _mat.SetFloat("_Intensity", intensity);
        _intensity = intensity;

        float animTime = _hasAnimTimeParam ? _animator.GetFloat(AnimTimeParameter) : Time;
        _mat.SetFloat("_Time4D", animTime);
        _prevTime = animTime;

        // Bind the initial flipbook. If a param drives the index, honour it on the first frame so we
        // don't flash flipbook 0 before the swap; otherwise default to 0. The index is an Animator
        // Float parameter (Unity can't keyframe Ints) read via GetFloat and rounded to the nearest
        // index — author keys with Constant tangents so the value steps cleanly between whole numbers.
        int initialIndex = _hasFlipbookParam ? Mathf.RoundToInt(_animator.GetFloat(FlipbookIndexParameter)) : 0;
        BindFlipbook(initialIndex);

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
        // the animation is paused or holding a frame (passthrough included).
        bool dirty = false;

        if (_hasFlipbookParam)
        {
            int idx = Mathf.RoundToInt(_animator.GetFloat(FlipbookIndexParameter));
            if (idx != _activeFlipbook)
            {
                BindFlipbook(idx);
                dirty = true;
            }
        }

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

    // Binds Flipbooks[index]'s texture and layout into the CRT material and clears passthrough.
    // An index of -1, out of range, or pointing at a null entry/texture switches the pass to
    // passthrough (the frag returns the incoming atlas unchanged) so the CRT can stay in the chain
    // without contributing. Early-outs when the requested index is already active, so a constant
    // index parameter costs nothing. Caller is responsible for the Crt.Update() that flushes it.
    private void BindFlipbook(int index)
    {
        if (index == _activeFlipbook) return;

        Texture3D tex = (FlipbookTextures != null && index >= 0 && index < FlipbookTextures.Length)
            ? FlipbookTextures[index] : null;

        // No valid flipbook: become a passthrough pass. Leave the last texture/layout bound — the
        // shader ignores them while _Passthrough is set, and we avoid touching them needlessly.
        if (tex == null)
        {
            _mat.SetInt("_Passthrough", 1);
            _activeFlipbook = index;
            return;
        }

        _mat.SetTexture("_PackedTex", tex);

        // Resolve layout from the parallel arrays at this index. Sidecar values are written there at
        // setup; if an array is short or zero (in-Editor assignment before ApplyTo runs) we fall back
        // to single-column defaults / deriving from the texture's Y stack.
        int snapshotY   = ArrayGet(FlipbookSnapshotY, index, 0);
        int snapsPerCol = ArrayGet(FlipbookSnapshotsPerColumn, index, 0); snapsPerCol = snapsPerCol > 0 ? snapsPerCol : 1;
        int numCols     = ArrayGet(FlipbookNumColumns, index, 0);         numCols     = numCols     > 0 ? numCols     : 1;
        int baked       = ArrayGet(FlipbookNumSnapshots, index, 0);
        int shMode      = ArrayGet(FlipbookSHMode, index, (int)MomentALVSHMode.MonoL1);
        int bitDepth    = ArrayGet(FlipbookBitDepth, index, (int)MomentALVBitDepth.Depth8);

        // Total snapshot count: trust the sidecar value when present. The grid (snapsPerCol * numCols)
        // is an upper bound, not the actual count — the last column can be partial. The texture-height
        // fallback only fires for legacy single-column atlases where the height divides exactly.
        int numSnapshots;
        if (baked > 0)
            numSnapshots = baked;
        else
            numSnapshots = snapshotY > 0 ? (tex.height / snapshotY) * numCols : snapsPerCol * numCols;
        int numSlots = MomentALVFormat.NumSlots((MomentALVSHMode)shMode);

        _mat.SetInt  ("_NumSnapshots",       numSnapshots);
        _mat.SetInt  ("_SnapshotsPerColumn", snapsPerCol);
        _mat.SetInt  ("_NumColumns",         numCols);
        // _SnapshotScale means "1 / snapsPerCol" (V stride per snapshot within a column); the shader
        // uses _ColumnScale for the U stride between adjacent columns.
        _mat.SetFloat("_SnapshotScale", 1f / snapsPerCol);
        _mat.SetFloat("_ColumnScale",   1f / numCols);
        _mat.SetFloat("_SliceScale",    1f / numSlots);

        _mat.SetInt("_SHMode",   shMode);
        _mat.SetInt("_BitDepth", bitDepth);
        _mat.SetInt("_IsUnorm",  MomentALVFormat.IsUnorm((MomentALVSHMode)shMode, (MomentALVBitDepth)bitDepth) ? 1 : 0);

        _mat.SetInt("_Passthrough", 0);

        NumSnapshots    = numSnapshots;
        _activeFlipbook = index;
    }

    // Safe indexed read for the parallel flipbook arrays: returns fallback when the array is null,
    // too short, or the index is negative. Keeps BindFlipbook robust against arrays that haven't been
    // populated yet (e.g. a texture assigned in the inspector before the sidecar layout was applied).
    private int ArrayGet(int[] array, int index, int fallback)
    {
        if (array == null || index < 0 || index >= array.Length) return fallback;
        return array[index];
    }

}
