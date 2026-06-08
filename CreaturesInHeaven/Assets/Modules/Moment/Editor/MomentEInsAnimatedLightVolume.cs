using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using VRCLightVolumes;

[CustomEditor(typeof(MomentAnimatedLightVolume))]
public class MomentEInsAnimatedLightVolume : Editor
{
    // Per-slot texture snapshot from last frame, used to detect when a flipbook's texture changes so
    // we can reload its sidecar. Indexed parallel to alv.FlipbookTextures.
    Texture3D[] _prevFlipbookTextures = new Texture3D[0];

    // Which flipbook the voxel preview / debug readout reflects. Clamped to the list each frame.
    int _previewFlipbook = 0;

    // Voxel preview GPU resources. Rebuilt when the volume, resolution, texture, or snapshot changes.
    ComputeBuffer _posBuf;
    ComputeBuffer _sh0Buf, _sh1Buf, _sh2Buf;
    ComputeBuffer _argsBuf;
    Mesh _previewMesh;
    Material _previewMaterial;
    LightVolume _prevLV;
    Vector3Int _prevRes;
    Texture3D _prevPreviewTexture;
    int _prevPreviewSnapshot = -1;
    bool _sliceX, _sliceY, _sliceZ;
    int _sliceXVal, _sliceYVal, _sliceZVal;
    bool _prevSliceX, _prevSliceY, _prevSliceZ;
    int _prevSliceXVal, _prevSliceYVal, _prevSliceZVal;

    enum SHDisplayMode { Full, L0Only, L1Only }
    SHDisplayMode _previewSHDisplay;

    void OnDisable()
    {
        ReleasePreviewBuffers();
    }

    void ReleasePreviewBuffers()
    {
        _posBuf?.Release();  _posBuf  = null;
        _sh0Buf?.Release();  _sh0Buf  = null;
        _sh1Buf?.Release();  _sh1Buf  = null;
        _sh2Buf?.Release();  _sh2Buf  = null;
        _argsBuf?.Release(); _argsBuf = null;
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        MomentAnimatedLightVolume alv = (MomentAnimatedLightVolume)target;

        // --- Setup ---------------------------------------------------
        EditorGUILayout.LabelField("Setup", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("TargetVolume"),
            new GUIContent("Target volume", "The LightVolumeInstance whose atlas region this component writes into."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("Crt"),
            new GUIContent("Render texture", "The CustomRenderTexture that runs the CRT shader. Created by the setup button below."));

        // Flipbook textures. The runtime binds one at a time and swaps via the index parameter below.
        // Per-flipbook layout lives in parallel arrays kept in sync from each texture's sidecar (see
        // SyncFlipbookSidecars); only the texture list is user-editable, so the default array drawer
        // on FlipbookTextures gives us add/remove/reorder for free.
        EditorGUILayout.PropertyField(serializedObject.FindProperty("FlipbookTextures"),
            new GUIContent("Flipbooks", "Packed SH textures, one per flipbook. Index 0 is bound on Start; swap at runtime via the index parameter below."), true);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("FlipbookIndexParameter"),
            new GUIContent("Flipbook index parameter", "Animator Float parameter that selects the active flipbook (rounded to nearest; use Constant keyframe tangents). -1 or out of range = passthrough (no contribution). Leave empty to always use index 0."));

        // Apply property edits before we inspect the arrays below, so they reflect this frame's
        // assignments (e.g. a texture just dropped into a slot).
        serializedObject.ApplyModifiedProperties();

        // Keep the parallel layout arrays sized to the texture list and reload sidecars for any texture
        // that changed.
        SyncFlipbookSidecars(alv);

        if (alv.Crt != null && alv.TargetVolume == null)
            EditorGUILayout.HelpBox("Assign a Target Volume to complete setup.", MessageType.Warning);

        // --- Shader behaviour ----------------------------------------
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Behaviour", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("Blending"),
            new GUIContent("Blending mode", "How this volume's SH contribution is composited onto the atlas bake."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("Intensity"),
            new GUIContent("Intensity", "Scales the SH contribution before blending. Used when the Animator parameter below is empty."));

        // --- Playback ------------------------------------------------
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Playback", EditorStyles.boldLabel);

        EditorGUILayout.PropertyField(serializedObject.FindProperty("AnimatorSource"),
            new GUIContent("Animator", "Animator that drives playback. Can be on any GameObject."));

        EditorGUILayout.PropertyField(serializedObject.FindProperty("Time"),
            new GUIContent("Time", "Normalised playback position. Used when the Animator parameter below is empty."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("AnimTimeParameter"),
            new GUIContent("Time parameter", "Animator float parameter that overrides Time at runtime. Leave empty to use the field value above."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("IntensityParameter"),
            new GUIContent("Intensity parameter", "Animator float parameter that overrides Intensity at runtime. Leave empty to use the field value above."));

        Animator animator = alv.AnimatorSource;
        if (animator == null)
        {
            EditorGUILayout.HelpBox("Assign an animator and create float parameters matching the names above to start animating this Light Volume.", MessageType.Info);
        }
        else
        {
            float? currentTime = MomentSceneQuery.FindAnimatorFloatParam(animator, alv.AnimTimeParameter);
            if (currentTime == null)
                EditorGUILayout.HelpBox($"Parameter \"{alv.AnimTimeParameter}\" not found on the Animator. Make sure it exists and is a Float.", MessageType.Warning);
            else
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.Slider(new GUIContent("Current time", "Current value of the Animator parameter. Read-only."), currentTime.Value, 0f, 1f);
                EditorGUI.EndDisabledGroup();
            }
        }

        // --- Voxel preview -------------------------------------------
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

        bool previewEnabled = alv.PreviewVoxels;
        bool newPreviewEnabled = EditorGUILayout.Toggle("Show voxels", previewEnabled);
        if (newPreviewEnabled != previewEnabled)
        {
            alv.PreviewVoxels = newPreviewEnabled;
            EditorUtility.SetDirty(alv);
        }

        if (alv.PreviewVoxels)
        {
            int previewIdx = ResolvePreviewFlipbook(alv);
            int fbCount = MomentFlipbookArrays.Count(alv);

            // Flipbook picker, only when there's a choice to make.
            if (fbCount > 1)
            {
                int newPreviewFb = EditorGUILayout.IntSlider("Flipbook", _previewFlipbook, 0, fbCount - 1);
                if (newPreviewFb != _previewFlipbook)
                {
                    _previewFlipbook = newPreviewFb;
                    SceneView.RepaintAll();
                }
            }

            Texture3D previewTex = previewIdx >= 0 ? alv.FlipbookTextures[previewIdx] : null;
            if (previewTex != null)
            {
                // Prefer the sidecar-derived true count over the grid product, since the last
                // column can be partial. Fallback to grid math only for unmigrated legacy data.
                int snapY         = alv.FlipbookSnapshotY[previewIdx];
                int rowsPerColumn = snapY > 0 ? previewTex.height / snapY : 1;
                int numSnapshots  = alv.FlipbookNumSnapshots[previewIdx] > 0
                    ? alv.FlipbookNumSnapshots[previewIdx]
                    : rowsPerColumn * Mathf.Max(1, alv.FlipbookNumColumns[previewIdx]);
                int newSnapshot   = EditorGUILayout.IntSlider("Snapshot", alv.PreviewSnapshot, 0, numSnapshots - 1);
                if (newSnapshot != alv.PreviewSnapshot)
                {
                    alv.PreviewSnapshot = newSnapshot;
                    EditorUtility.SetDirty(alv);
                    SceneView.RepaintAll();
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Assign a flipbook texture to preview voxels.", MessageType.Info);
            }

            SHDisplayMode newMode = (SHDisplayMode)EditorGUILayout.EnumPopup("SH display", _previewSHDisplay);
            if (newMode != _previewSHDisplay)
            {
                _previewSHDisplay = newMode;
                SceneView.RepaintAll();
            }

            // Slice controls. Only shown when a target volume is assigned so we know the resolution.
            if (alv.TargetVolume != null)
            {
                LightVolume lv = alv.TargetVolume.GetComponent<LightVolume>();
                if (lv != null)
                {
                    Vector3Int res = lv.Resolution;
                    DrawSliceRow("Slice X", ref _sliceX, ref _sliceXVal, res.x);
                    DrawSliceRow("Slice Y", ref _sliceY, ref _sliceYVal, res.y);
                    DrawSliceRow("Slice Z", ref _sliceZ, ref _sliceZVal, res.z);
                }
            }
        }

        // --- Bake settings -------------------------------------------
        // Read-only summary of what the Setup window has saved. To change these, open Tools > Moment ALV > Set up animated light volume.
        EditorGUILayout.Space(8);
        alv.BakeSettingsFoldout = EditorGUILayout.Foldout(alv.BakeSettingsFoldout, "Saved bake settings", true, EditorStyles.foldoutHeader);
        if (alv.BakeSettingsFoldout)
        {
            EditorGUI.indentLevel++;
            EditorGUI.BeginDisabledGroup(true);

            EditorGUILayout.ObjectField("Animator", alv.BakeAnimator, typeof(Animator), allowSceneObjects: true);
            EditorGUILayout.ObjectField("Animation clip", alv.BakeClip, typeof(AnimationClip), allowSceneObjects: false);
            EditorGUILayout.IntField("No. of snapshots", alv.BakeSnapshotCount);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.IntField("Start frame", alv.BakeStartFrame);
            EditorGUILayout.IntField("End frame (-1 = full)", alv.BakeEndFrame);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EnumPopup("SH mode",   alv.BakeSHMode);
            EditorGUILayout.EnumPopup("Bit depth", alv.BakeBitDepth);
            EditorGUILayout.TextField("Output name", alv.BakeOutputName);

            EditorGUI.EndDisabledGroup();
            EditorGUI.indentLevel--;
        }

        // --- Info ----------------------------------------------------
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);

        int debugIdx = ResolvePreviewFlipbook(alv);
        Texture3D debugTex = debugIdx >= 0 ? alv.FlipbookTextures[debugIdx] : null;
        if (debugTex != null)
        {
            int snapY             = alv.FlipbookSnapshotY[debugIdx];
            var shMode            = (MomentALVSHMode)alv.FlipbookSHMode[debugIdx];
            int numColumns        = Mathf.Max(1, alv.FlipbookNumColumns[debugIdx]);
            int rowsPerColumn     = snapY > 0 ? debugTex.height / snapY : 1;
            int numSnapshots      = alv.FlipbookNumSnapshots[debugIdx] > 0 ? alv.FlipbookNumSnapshots[debugIdx] : rowsPerColumn * numColumns;
            int snapshotSpatialX  = debugTex.width / numColumns;
            int snapshotSpatialZ  = debugTex.depth / MomentALVFormat.NumSlots(shMode);
            EditorGUILayout.LabelField("Snapshot size", $"{snapshotSpatialX} x {snapY} x {snapshotSpatialZ}");
            string gridSuffix = numColumns > 1 ? $"  ({numColumns} cols × {rowsPerColumn} rows)" : "";
            EditorGUILayout.LabelField("Snapshots", $"{numSnapshots}{gridSuffix}");
        }
        else
        {
            EditorGUILayout.LabelField("Snapshot size", "—");
            EditorGUILayout.LabelField("Snapshots", "—");
        }

        serializedObject.ApplyModifiedProperties();
    }

    // Keeps the parallel layout arrays sized to the texture list, then reloads the sidecar for any
    // texture slot that changed since last frame and writes its layout into the arrays. Mirrors the
    // old single-texture auto-load, per slot.
    void SyncFlipbookSidecars(MomentAnimatedLightVolume alv)
    {
        int count = MomentFlipbookArrays.Count(alv);
        MomentFlipbookArrays.EnsureLength(alv, count);

        if (_prevFlipbookTextures.Length != count)
            System.Array.Resize(ref _prevFlipbookTextures, count);

        for (int i = 0; i < count; i++)
        {
            Texture3D tex = alv.FlipbookTextures[i];
            if (tex == _prevFlipbookTextures[i]) continue;

            _prevFlipbookTextures[i] = tex;
            if (tex != null)
            {
                MomentTextureInfo info = MomentTextureInfo.Load(AssetDatabase.GetAssetPath(tex));
                info?.ApplyTo(alv, i);   // ApplyTo marks the component dirty.
            }
        }
    }

    // Returns the flipbook index the preview/debug sections should reflect, or -1 if the list is
    // empty. Clamps and stores _previewFlipbook so the picker UI stays in range.
    int ResolvePreviewFlipbook(MomentAnimatedLightVolume alv)
    {
        int count = MomentFlipbookArrays.Count(alv);
        if (count == 0) return -1;
        _previewFlipbook = Mathf.Clamp(_previewFlipbook, 0, count - 1);
        return _previewFlipbook;
    }

    void DrawSliceRow(string label, ref bool enabled, ref int value, int max)
    {
        EditorGUILayout.BeginHorizontal();
        bool newEnabled = EditorGUILayout.ToggleLeft(label, enabled, GUILayout.Width(72));
        int newValue = EditorGUILayout.IntSlider(value, 0, max - 1);
        EditorGUILayout.EndHorizontal();
        if (newEnabled != enabled || newValue != value)
        {
            enabled = newEnabled;
            value   = newValue;
            SceneView.RepaintAll();
        }
    }

    void OnSceneGUI()
    {
        MomentAnimatedLightVolume alv = (MomentAnimatedLightVolume)target;
        if (!alv.PreviewVoxels) return;
        if (alv.TargetVolume == null) return;

        LightVolume lv = alv.TargetVolume.GetComponent<LightVolume>();
        if (lv == null) return;

        Vector3Int res = lv.Resolution;
        Vector3 pos = lv.GetPosition();
        Quaternion rot = lv.GetRotation();
        Vector3 scl = lv.GetScale();

        int previewIdx = ResolvePreviewFlipbook(alv);
        Texture3D tex = previewIdx >= 0 ? alv.FlipbookTextures[previewIdx] : null;
        int previewSnapshot = alv.PreviewSnapshot;

        // Rebuild buffers when volume, resolution, texture, snapshot, or slice changes.
        bool needRebuild = _posBuf == null
            || lv != _prevLV
            || res != _prevRes
            || tex != _prevPreviewTexture
            || previewSnapshot != _prevPreviewSnapshot
            || _sliceX != _prevSliceX || _sliceXVal != _prevSliceXVal
            || _sliceY != _prevSliceY || _sliceYVal != _prevSliceYVal
            || _sliceZ != _prevSliceZ || _sliceZVal != _prevSliceZVal;

        if (needRebuild)
        {
            _prevLV               = lv;
            _prevRes              = res;
            _prevPreviewTexture   = tex;
            _prevPreviewSnapshot  = previewSnapshot;
            _prevSliceX = _sliceX; _prevSliceXVal = _sliceXVal;
            _prevSliceY = _sliceY; _prevSliceYVal = _sliceYVal;
            _prevSliceZ = _sliceZ; _prevSliceZVal = _sliceZVal;

            var positions = new System.Collections.Generic.List<Vector3>();
            var sh0 = new System.Collections.Generic.List<Vector4>();
            var sh1 = new System.Collections.Generic.List<Vector4>();
            var sh2 = new System.Collections.Generic.List<Vector4>();

            // Sample all three SH textures for the selected snapshot if available.
            // All three SH slots live in the same GetPixels() array, separated by snapshotSize.z in Z.
            Color[] pixels = null;
            Vector3Int texSize = Vector3Int.zero;
            Vector3Int snapshotSize = Vector3Int.zero;
            Vector2Int snapshotOrigin = Vector2Int.zero;
            if (tex != null)
            {
                // snapshotSize.x is the spatial X of one snapshot cell (atlas width / numColumns).
                // For legacy single-column atlases NumColumnsBaked == 1 so this matches tex.width.
                int snapY               = alv.FlipbookSnapshotY[previewIdx];
                var shMode              = (MomentALVSHMode)alv.FlipbookSHMode[previewIdx];
                int numColumns          = Mathf.Max(1, alv.FlipbookNumColumns[previewIdx]);
                int snapshotsPerColumn  = snapY > 0 ? tex.height / snapY : 1;
                snapshotSize.x  = tex.width / numColumns;
                snapshotSize.y  = snapY;
                snapshotSize.z  = tex.depth / MomentALVFormat.NumSlots(shMode);
                texSize         = new Vector3Int(tex.width, tex.height, tex.depth);
                int totalSnaps  = snapshotsPerColumn * numColumns;
                int snapshotIdx = Mathf.Clamp(previewSnapshot, 0, totalSnaps - 1);
                int col         = snapshotIdx / snapshotsPerColumn;
                int row         = snapshotIdx - col * snapshotsPerColumn;
                snapshotOrigin  = new Vector2Int(col * snapshotSize.x, row * snapshotSize.y);
                pixels          = tex.GetPixels();
            }

            Vector3 halfOffset = Vector3.one * 0.5f;
            for (int voxelX = 0; voxelX < res.x; voxelX++)
            for (int voxelY = 0; voxelY < res.y; voxelY++)
            for (int voxelZ = 0; voxelZ < res.z; voxelZ++)
            {
                if (_sliceX && voxelX != _sliceXVal) continue;
                if (_sliceY && voxelY != _sliceYVal) continue;
                if (_sliceZ && voxelZ != _sliceZVal) continue;

                Vector3 localPos = new Vector3(
                    (voxelX + 0.5f) / res.x,
                    (voxelY + 0.5f) / res.y,
                    (voxelZ + 0.5f) / res.z) - halfOffset;
                positions.Add(pos + rot * Vector3.Scale(localPos, scl));

                if (pixels != null)
                {
                    MomentTextureWriter.DecodeVoxel(
                        pixels, texSize, snapshotSize, snapshotOrigin,
                        voxelX, voxelY, voxelZ,
                        (MomentALVSHMode)alv.FlipbookSHMode[previewIdx], (MomentALVBitDepth)alv.FlipbookBitDepth[previewIdx],
                        out Vector4 s0, out Vector4 s1, out Vector4 s2);
                    sh0.Add(s0);
                    sh1.Add(s1);
                    sh2.Add(s2);
                }
                else
                {
                    sh0.Add(new Vector4(1f, 1f, 1f, 0f));
                    sh1.Add(Vector4.zero);
                    sh2.Add(Vector4.zero);
                }
            }

            int count = positions.Count;
            ReleasePreviewBuffers();
            _posBuf  = new ComputeBuffer(count, sizeof(float) * 3);
            _sh0Buf  = new ComputeBuffer(count, sizeof(float) * 4);
            _sh1Buf  = new ComputeBuffer(count, sizeof(float) * 4);
            _sh2Buf  = new ComputeBuffer(count, sizeof(float) * 4);
            _argsBuf = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
            _posBuf.SetData(positions);
            _sh0Buf.SetData(sh0);
            _sh1Buf.SetData(sh1);
            _sh2Buf.SetData(sh2);
        }

        if (_previewMesh == null)
            _previewMesh = LVUtils.GenerateIcoSphere(0.5f, 0);

        if (_previewMaterial == null)
            _previewMaterial = new Material(Shader.Find("Hidden/Moment/ALVPreview"));

        float radius = Mathf.Min(scl.x / res.x, Mathf.Min(scl.y / res.y, scl.z / res.z)) / 4f;
        _previewMaterial.SetBuffer("_Positions", _posBuf);
        _previewMaterial.SetBuffer("_SH0", _sh0Buf);
        _previewMaterial.SetBuffer("_SH1", _sh1Buf);
        _previewMaterial.SetBuffer("_SH2", _sh2Buf);
        _previewMaterial.SetFloat("_Scale", radius);
        _previewMaterial.SetInt("_SHMode", (int)_previewSHDisplay);
        _argsBuf.SetData(new uint[] {
            _previewMesh.GetIndexCount(0), (uint)_posBuf.count,
            _previewMesh.GetIndexStart(0), (uint)_previewMesh.GetBaseVertex(0), 0u });

        Bounds bounds = LVUtils.BoundsFromTRS(lv.GetMatrixTRS());
        Graphics.DrawMeshInstancedIndirect(_previewMesh, 0, _previewMaterial, bounds, _argsBuf,
            0, null, ShadowCastingMode.Off, false, alv.gameObject.layer);
    }

}
