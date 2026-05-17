using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using VRCLightVolumes;

[CustomEditor(typeof(AnimatedLightVolume))]
public class ALVEditor : Editor
{
    Texture3D _prevTexture;

    // Voxel preview GPU resources. Rebuilt when the volume, resolution, texture, or frame changes.
    ComputeBuffer _posBuf;
    ComputeBuffer _sh0Buf, _sh1Buf, _sh2Buf;
    ComputeBuffer _argsBuf;
    Mesh _previewMesh;
    Material _previewMaterial;
    LightVolume _prevLV;
    Vector3Int _prevRes;
    Texture3D _prevPreviewTexture;
    int _prevPreviewFrame = -1;
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
        _sh0Buf?.Release(); _sh0Buf = null;
        _sh1Buf?.Release(); _sh1Buf = null;
        _sh2Buf?.Release(); _sh2Buf = null;
        _argsBuf?.Release(); _argsBuf = null;
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        AnimatedLightVolume alv = (AnimatedLightVolume)target;

        // --- Setup ---------------------------------------------------
        EditorGUILayout.LabelField("Setup", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("TargetVolume"),
            new GUIContent("Target volume", "The LightVolumeInstance whose atlas region this component writes into."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("Crt"),
            new GUIContent("Render texture", "The CustomRenderTexture that runs the CRT shader. Created by the setup button below."));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("AnimatedTexture"),
            new GUIContent("Animation texture", "Packed 4D SH texture produced by the baking tool."));

        // When a new texture is assigned, read the sidecar JSON and populate FrameY.
        if (alv.AnimatedTexture != _prevTexture)
        {
            _prevTexture = alv.AnimatedTexture;
            if (alv.AnimatedTexture != null)
            {
                string texPath = AssetDatabase.GetAssetPath(alv.AnimatedTexture);
                ALVTextureInfo info = ALVTextureInfo.Load(texPath);
                if (info != null)
                {
                    alv.SampleY          = info.sampleY;
                    alv.SHMode     = info.shMode;   // shMode/bitDepth fields required; old sidecars are not supported
                    alv.BitDepth   = info.bitDepth;
                    EditorUtility.SetDirty(alv);
                }
            }
        }

        EditorGUILayout.Space(4);

        if (GUILayout.Button("Set Up CRT", GUILayout.Height(32)))
            Setup(alv);

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
            // Read the current value of the parameter from the animator, if it exists.
            float currentTime = 0f;
            bool paramFound = false;
            foreach (var param in animator.parameters)
            {
                if (param.name == alv.AnimTimeParameter && param.type == AnimatorControllerParameterType.Float)
                {
                    currentTime = animator.GetFloat(alv.AnimTimeParameter);
                    paramFound = true;
                    break;
                }
            }

            if (!paramFound)
                EditorGUILayout.HelpBox($"Parameter \"{alv.AnimTimeParameter}\" not found on the Animator. Make sure it exists and is a Float.", MessageType.Warning);
            else
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.Slider(new GUIContent("Current time", "Current value of the Animator parameter. Read-only."), currentTime, 0f, 1f);
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
            if (alv.AnimatedTexture != null)
            {
                int numSlots    = alv.SHMode == ALVSHMode.L1 ? 3 : alv.SHMode == ALVSHMode.MonoL1 ? 2 : 1;
                int sampleSizeY = alv.SampleY > 0 ? alv.SampleY : (alv.AnimatedTexture.depth / numSlots);
                int numSamples = alv.AnimatedTexture.height / sampleSizeY;
                int newFrame = EditorGUILayout.IntSlider("Sample", alv.PreviewSample, 0, numSamples - 1);
                if (newFrame != alv.PreviewSample)
                {
                    alv.PreviewSample = newFrame;
                    EditorUtility.SetDirty(alv);
                    SceneView.RepaintAll();
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Assign an Animation texture to preview voxels.", MessageType.Info);
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
        EditorGUILayout.Space(8);
        alv.BakeSettingsFoldout = EditorGUILayout.Foldout(alv.BakeSettingsFoldout, "Saved bake settings", true, EditorStyles.foldoutHeader);
        if (alv.BakeSettingsFoldout)
        {
            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();

            alv.BakeAnimator  = (Animator)EditorGUILayout.ObjectField("Animator", alv.BakeAnimator, typeof(Animator), allowSceneObjects: true);
            alv.BakeClip      = (AnimationClip)EditorGUILayout.ObjectField("Animation clip", alv.BakeClip, typeof(AnimationClip), allowSceneObjects: false);
            alv.BakeSampleCount = Mathf.Max(2, EditorGUILayout.IntField("No. of samples", alv.BakeSampleCount));

            EditorGUILayout.BeginHorizontal();
            alv.BakeStartFrame = Mathf.Max(0, EditorGUILayout.IntField("Start frame", alv.BakeStartFrame));
            alv.BakeEndFrame   = EditorGUILayout.IntField("End frame (-1 = full)", alv.BakeEndFrame);
            EditorGUILayout.EndHorizontal();

            alv.BakeSHMode   = (ALVSHMode)  EditorGUILayout.EnumPopup("SH mode",   alv.BakeSHMode);
            alv.BakeBitDepth = (ALVBitDepth)EditorGUILayout.EnumPopup("Bit depth", alv.BakeBitDepth);
            alv.BakeOutputName = EditorGUILayout.TextField("Output name", alv.BakeOutputName);

            if (EditorGUI.EndChangeCheck())
                EditorUtility.SetDirty(alv);

            EditorGUI.indentLevel--;
        }

        // --- Info ----------------------------------------------------
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);

        if (alv.AnimatedTexture != null)
        {
            int numSlots    = alv.SHMode == ALVSHMode.L1 ? 3 : alv.SHMode == ALVSHMode.MonoL1 ? 2 : 1;
            int sampleSizeY = alv.SampleY > 0 ? alv.SampleY : (alv.AnimatedTexture.depth / numSlots);
            int numSamples = alv.AnimatedTexture.height / sampleSizeY;
            EditorGUILayout.LabelField("Sample size", $"{alv.AnimatedTexture.width} x {sampleSizeY} x {alv.AnimatedTexture.depth / numSlots}");
            EditorGUILayout.LabelField("Samples", numSamples.ToString());
        }
        else
        {
            EditorGUILayout.LabelField("Sample size", "—");
            EditorGUILayout.LabelField("Samples", "—");
        }

        serializedObject.ApplyModifiedProperties();
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

    static void Setup(AnimatedLightVolume alv)
    {
        // Mirror the Light Volumes package convention: store generated assets
        // alongside the scene they belong to, not next to the script.
        string scenePath = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
        string sceneName = System.IO.Path.GetFileNameWithoutExtension(scenePath);
        string sceneDir  = System.IO.Path.GetDirectoryName(scenePath);
        string assetDir  = $"{sceneDir}/{sceneName}/AnimatedLV";
        CreateDirectory(assetDir);

        // Create material using the AnimatedLightVolume shader.
        Shader shader = Shader.Find("Hidden/AnimatedLightVolume");
        if (shader == null)
        {
            Debug.LogError("[AnimatedLightVolume] Could not find shader Hidden/AnimatedLightVolume.");
            return;
        }

        // Reuse existing material if already set up, otherwise create one.
        Material mat;
        if (alv.Crt != null && alv.Crt.material != null)
        {
            mat = alv.Crt.material;
        }
        else
        {
            mat = new Material(shader);
            string matPath = $"{assetDir}/{alv.gameObject.name}_ALVRenderTexture.mat";
            AssetDatabase.CreateAsset(mat, matPath);
        }

        // Reuse existing CRT if already set up, otherwise create one.
        CustomRenderTexture crt;
        if (alv.Crt != null)
        {
            crt = alv.Crt;
        }
        else
        {
            crt = new CustomRenderTexture(1, 1, RenderTextureFormat.ARGBHalf);
            string crtPath = $"{assetDir}/{alv.gameObject.name}_ALVRenderTexture.asset";
            AssetDatabase.CreateAsset(crt, crtPath);
        }

        // Configure the CRT. The LightVolumeSetup will enforce these too, but set
        // them here so the asset is in the right state before the scene is run.
        crt.dimension = TextureDimension.Tex3D;
        crt.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
        crt.updateMode = CustomRenderTextureUpdateMode.Realtime;
        crt.material = mat;

        alv.Crt = crt;

        // Register with the LightVolumeSetup post-processor chain.
        LightVolumeSetup setup = FindObjectOfType<LightVolumeSetup>();
        if (setup != null)
        {
            setup.RegisterPostProcessorCRT(crt);
            EditorUtility.SetDirty(setup);
            Debug.Log($"[AnimatedLightVolume] Registered CRT with LightVolumeSetup on '{setup.gameObject.name}'.");
        }
        else
        {
            Debug.LogWarning("[AnimatedLightVolume] No LightVolumeSetup found in scene. Register the CRT manually by adding it to the list of AtlasPostProcessors in the scene's Light Volume Manager.");
        }

        EditorUtility.SetDirty(alv);
        EditorUtility.SetDirty(crt);
        AssetDatabase.SaveAssets();

        Debug.Log($"[AnimatedLightVolume] Setup complete for '{alv.gameObject.name}'.");
    }

    void OnSceneGUI()
    {
        AnimatedLightVolume alv = (AnimatedLightVolume)target;
        if (!alv.PreviewVoxels) return;
        if (alv.TargetVolume == null) return;

        LightVolume lv = alv.TargetVolume.GetComponent<LightVolume>();
        if (lv == null) return;

        Vector3Int res = lv.Resolution;
        Vector3 pos = lv.GetPosition();
        Quaternion rot = lv.GetRotation();
        Vector3 scl = lv.GetScale();

        Texture3D tex = alv.AnimatedTexture;
        int previewFrame = alv.PreviewSample;

        // Rebuild buffers when volume, resolution, texture, frame, or slice changes.
        bool needRebuild = _posBuf == null
            || lv != _prevLV
            || res != _prevRes
            || tex != _prevPreviewTexture
            || previewFrame != _prevPreviewFrame
            || _sliceX != _prevSliceX || _sliceXVal != _prevSliceXVal
            || _sliceY != _prevSliceY || _sliceYVal != _prevSliceYVal
            || _sliceZ != _prevSliceZ || _sliceZVal != _prevSliceZVal;

        if (needRebuild)
        {
            _prevLV             = lv;
            _prevRes            = res;
            _prevPreviewTexture = tex;
            _prevPreviewFrame   = previewFrame;
            _prevSliceX = _sliceX; _prevSliceXVal = _sliceXVal;
            _prevSliceY = _sliceY; _prevSliceYVal = _sliceYVal;
            _prevSliceZ = _sliceZ; _prevSliceZVal = _sliceZVal;

            var positions = new System.Collections.Generic.List<Vector3>();
            var sh0 = new System.Collections.Generic.List<Vector4>();
            var sh1 = new System.Collections.Generic.List<Vector4>();
            var sh2 = new System.Collections.Generic.List<Vector4>();

            // Sample all three SH textures for the selected sample if available.
            // All three SH slots live in the same GetPixels() array, separated by sampleSize.z in Z.
            Color[] pixels = null;
            Vector3Int texSize = Vector3Int.zero;
            Vector3Int sampleSize = Vector3Int.zero;
            int sampleOrigin = 0;
            if (tex != null)
            {
                int numSlots  = alv.SHMode == ALVSHMode.L1 ? 3 : alv.SHMode == ALVSHMode.MonoL1 ? 2 : 1;
                sampleSize.y  = alv.SampleY > 0 ? alv.SampleY : (tex.depth / numSlots);
                sampleSize.x  = tex.width;
                sampleSize.z  = tex.depth / numSlots;
                texSize       = new Vector3Int(tex.width, tex.height, tex.depth);
                int sampleIdx = Mathf.Clamp(previewFrame, 0, texSize.y / sampleSize.y - 1);
                sampleOrigin  = sampleIdx * sampleSize.y;
                pixels        = tex.GetPixels();
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
                    // Packed layout: x + (y + sampleOrigin) * texSize.x + z * texSize.x * texSize.y
                    // SH slot 0 at z=voxelZ, slot 1 at z=voxelZ+sampleSize.z, slot 2 at z=voxelZ+sampleSize.z*2
                    int p0 = voxelX + (voxelY + sampleOrigin) * texSize.x + voxelZ                       * texSize.x * texSize.y;
                    int p1 = voxelX + (voxelY + sampleOrigin) * texSize.x + (voxelZ + sampleSize.z)      * texSize.x * texSize.y;
                    int p2 = voxelX + (voxelY + sampleOrigin) * texSize.x + (voxelZ + sampleSize.z * 2)  * texSize.x * texSize.y;
                    Color c0 = pixels[p0], c1 = pixels[p1], c2 = pixels[p2];
                    sh0.Add(new Vector4(c0.r, c0.g, c0.b, c0.a));
                    sh1.Add(new Vector4(c1.r, c1.g, c1.b, c1.a));
                    sh2.Add(new Vector4(c2.r, c2.g, c2.b, c2.a));
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
            _previewMaterial = new Material(Shader.Find("Hidden/ALVPreview"));

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

    // Creates each segment of a folder path that doesn't already exist.
    internal static void CreateDirectory(string path)
    {
        string[] parts = path.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = $"{current}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }
}
