using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using VRCLightVolumes;

// Bakes a multi-frame AnimatedLightVolume texture by:
//   1. Force-evaluating an Animator to N evenly-spaced times across an AnimationClip
//   2. Triggering a Bakery bake for each frame
//   3. Reading BakeryVolume.bakedTexture0/1/2 after each bake
//   4. Packing all frames into a Texture3D via GenerateALVTexture.SavePackedTexture
//
// Open via Tools > Lighting > Bake ALV Texture
#if BAKERY_INCLUDED
public class ALVBakeTexture : EditorWindow
{
    // --- Fields serialised in the window --------------------------------

    Animator _animator;
    AnimationClip _clip;
    int _frameCount = 8;
    LightVolume _targetVolume;
    string _outputName = "ALV_Bake";

    // --- Internal bake state --------------------------------------------

    bool _baking = false;
    int _currentFrame = 0;
    List<GenerateALVTexture.FrameSH> _collectedFrames = new();

    // Hierarchy paths resolved at bake start, used to re-find objects each frame
    // in case Bakery's scene management destroys the live references mid-bake.
    string _animatorPath;
    string _targetVolumePath;

    // Stopwatch runs for each frame; first completed frame sets _secsPerFrame for ETR display.
    System.Diagnostics.Stopwatch _frameStopwatch = new();
    double _secsPerFrame = -1;

    // 0-based index of the currently previewed bake frame
    int _previewFrame = 0;

    // --- Animation window (reflection) ----------------------------------
    // The Animation window has no public API for setting the current frame,
    // so it's accessed via reflection.

    EditorWindow _animationWindow;
    PropertyInfo _animWindowFrameProp;

    int AnimationWindowFrame
    {
        set
        {
            if (_animationWindow == null || _animWindowFrameProp == null) FindAnimationWindow();
            _animWindowFrameProp?.SetValue(_animationWindow, value, null);
        }
    }

    void FindAnimationWindow()
    {
        var type = System.Type.GetType("UnityEditor.AnimationWindow, UnityEditor");
        if (type == null) return;
        var windows = Resources.FindObjectsOfTypeAll(type);
        if (windows.Length == 0) return;
        _animationWindow = windows[0] as EditorWindow;
        _animWindowFrameProp = type.GetProperty("frame",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    }

    // Returns the animation window frame index for a given bake frame index (0-based).
    int BakeFrameToAnimFrame(int bakeFrame)
    {
        if (_clip == null) return 0;
        float t = _frameCount > 1
            ? _clip.length * bakeFrame / (_frameCount - 1)
            : 0f;
        return Mathf.RoundToInt(t * _clip.frameRate);
    }

    // --- Window lifecycle -----------------------------------------------

    [MenuItem("Tools/Lighting/Bake animated light volume...")]
    static void Open() => GetWindow<ALVBakeTexture>("Bake animated light volume");

    void OnEnable()
    {
        ftRenderLightmap.OnFinishedFullRender += OnBakeFinished;
        FindAnimationWindow();
    }

    void OnDisable()
    {
        ftRenderLightmap.OnFinishedFullRender -= OnBakeFinished;
        if (_baking) AbortBake("Window closed");
    }

    // Returns the project-relative path to the directory containing this script.
    static string ScriptDir()
    {
        foreach (var guid in AssetDatabase.FindAssets($"t:MonoScript {nameof(ALVBakeTexture)}"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.EndsWith($"{nameof(ALVBakeTexture)}.cs"))
                return Path.GetDirectoryName(path).Replace('\\', '/');
        }
        return "Assets/Modules/Animated Light Volumes/Editor";
    }

    // --- UI -------------------------------------------------------------

    // Cached element refs, written to by CreateGUI and read/updated by bake logic.
    HelpBox _validationBox;
    HelpBox _bakeProgressBox;
    Button _bakeBtn;
    Button _cancelBtn;
    IntegerField _previewFrameField;
    Label _previewFrameMax;
    Label _animFrameLabel;
    VisualElement _previewControls;
    ObjectField _animatorField;
    Label _outputResLabel;
    Label _vramSizeLabel;
    Label _bundleSizeLabel;
    ObjectField _volumeField;

    public void CreateGUI()
    {
        string dir = ScriptDir();
        var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>($"{dir}/ALVBakeTexture.uxml");
        if (uxml == null)
        {
            rootVisualElement.Add(new Label($"ALVBakeTexture.uxml not found in {dir}."));
            return;
        }
        uxml.CloneTree(rootVisualElement);

        // --- Setup fields ---

        _animatorField = rootVisualElement.Q<ObjectField>("animator-field");
        var animatorField = _animatorField;
        animatorField.objectType = typeof(Animator);
        animatorField.value = _animator;
        animatorField.RegisterValueChangedCallback(e =>
        {
            _animator = e.newValue as Animator;
            UpdateUI();
        });

        var clipField = rootVisualElement.Q<ObjectField>("clip-field");
        clipField.objectType = typeof(AnimationClip);
        clipField.value = _clip;
        clipField.RegisterValueChangedCallback(e =>
        {
            _clip = e.newValue as AnimationClip;
            _previewFrame = 0;
            UpdatePreviewReadout();
            UpdateUI();
        });

        _volumeField = rootVisualElement.Q<ObjectField>("volume-field");
        var volumeField = _volumeField;
        volumeField.objectType = typeof(LightVolume);
        volumeField.value = _targetVolume;
        volumeField.RegisterValueChangedCallback(e =>
        {
            _targetVolume = e.newValue as LightVolume;
            UpdateUI();
        });

        // --- Bake fields ---

        var frameCountField = rootVisualElement.Q<IntegerField>("frame-count-field");
        frameCountField.value = _frameCount;
        frameCountField.RegisterCallback<FocusOutEvent>(_ =>
        {
            _frameCount = Mathf.Max(frameCountField.value, 2);
            frameCountField.SetValueWithoutNotify(_frameCount);
            _previewFrame = Mathf.Clamp(_previewFrame, 0, _frameCount - 1);
            UpdatePreviewReadout();
            UpdateUI();
        });

        var outputNameField = rootVisualElement.Q<TextField>("output-name-field");
        outputNameField.value = _outputName;
        outputNameField.RegisterValueChangedCallback(e => _outputName = e.newValue);

        // --- Preview controls ---

        _previewControls  = rootVisualElement.Q<VisualElement>("preview-controls");
        _previewFrameField = rootVisualElement.Q<IntegerField>("preview-frame-field");
        _previewFrameMax  = rootVisualElement.Q<Label>("preview-frame-max");
        _animFrameLabel   = rootVisualElement.Q<Label>("anim-frame-label");

        _previewFrameField.RegisterValueChangedCallback(e =>
        {
            // Field is 1-indexed; clamp to valid range then push back if corrected.
            _previewFrame = Mathf.Clamp(e.newValue - 1, 0, _frameCount - 1);
            _previewFrameField.SetValueWithoutNotify(_previewFrame + 1);
            UpdatePreviewReadout();
        });

        rootVisualElement.Q<Button>("prev-btn").clicked += () =>
        {
            if (_previewFrame > 0) _previewFrame--;
            UpdatePreviewReadout();
            AnimationWindowFrame = BakeFrameToAnimFrame(_previewFrame);
        };

        rootVisualElement.Q<Button>("next-btn").clicked += () =>
        {
            if (_previewFrame < _frameCount - 1) _previewFrame++;
            UpdatePreviewReadout();
            AnimationWindowFrame = BakeFrameToAnimFrame(_previewFrame);
        };

        _previewFrameField.RegisterCallback<FocusOutEvent>(_ =>
            AnimationWindowFrame = BakeFrameToAnimFrame(_previewFrame));

        // --- Status / bake buttons ---

        _validationBox   = rootVisualElement.Q<HelpBox>("validation-box");
        _bakeProgressBox = rootVisualElement.Q<HelpBox>("bake-progress-box");
        _bakeBtn         = rootVisualElement.Q<Button>("bake-btn");
        _cancelBtn       = rootVisualElement.Q<Button>("cancel-btn");

        _bakeBtn.clicked   += StartBake;
        _cancelBtn.clicked += () => AbortBake("Cancelled by user");

        // --- Estimate labels ---

        _outputResLabel  = rootVisualElement.Q<Label>("output-res");
        _vramSizeLabel   = rootVisualElement.Q<Label>("vram-size");
        _bundleSizeLabel = rootVisualElement.Q<Label>("estimated-bundle-size");

        UpdatePreviewReadout();
        UpdateUI();
    }

    // Refreshes the preview frame field and anim-frame readout label.
    void UpdatePreviewReadout()
    {
        if (_previewFrameField == null) return;

        bool canPreview = _clip != null && _frameCount >= 2;
        _previewControls?.SetEnabled(canPreview);

        _previewFrameField.SetValueWithoutNotify(_previewFrame + 1);
        _previewFrameMax.text  = $"/ {_frameCount}";
        _animFrameLabel.text   = canPreview ? $"f{BakeFrameToAnimFrame(_previewFrame)}" : "—";
    }

    // Refreshes validation message and bake/cancel button visibility.
    void UpdateUI()
    {
        if (_bakeBtn == null) return;

        string error = Validate();

        // Validation box: only shown when there's an error and not currently baking
        bool showError = error != null && !_baking;
        _validationBox.text = error ?? "";
        _validationBox.style.display = showError ? DisplayStyle.Flex : DisplayStyle.None;

        // Progress box: only shown while baking
        _bakeProgressBox.style.display = _baking ? DisplayStyle.Flex : DisplayStyle.None;
        if (_baking)
        {
            int remaining = _frameCount - _currentFrame;
            string etr = _secsPerFrame >= 0
                ? $"\n(~{System.TimeSpan.FromSeconds(_secsPerFrame * (remaining + 2)):m\\:ss} remaining)"
                : "";
            _bakeProgressBox.text = $"Baking frame {_currentFrame + 1} / {_frameCount}…{etr}";
        }

        _bakeBtn.style.display   = _baking ? DisplayStyle.None : DisplayStyle.Flex;
        _cancelBtn.style.display = _baking ? DisplayStyle.Flex : DisplayStyle.None;
        _bakeBtn.SetEnabled(error == null);

        UpdateEstimates();
    }

    void UpdateEstimates()
    {
        if (_outputResLabel == null) return;

        if (_targetVolume == null)
        {
            _outputResLabel.text  = "—";
            _vramSizeLabel.text   = "—";
            _bundleSizeLabel.text = "—";
            return;
        }

        // Resolution uses the volume's voxel grid.
        Vector3Int res = _targetVolume.Resolution;
        int w = res.x;
        int h = res.y;
        int d = res.z;

        // Packed texture dimensions: X unchanged, Y stacked by numFrames, Z tripled for 3 SH slots.
        int packedH = h * _frameCount;
        int packedD = d * 3;
        _outputResLabel.text = $"{w} × {packedH} × {packedD}";

        // Same formula as LightVolumeEditor: voxelCount * 3 SH textures * 8 bytes (Half4).
        // Multiply by numFrames since we're stacking all frames.
        long voxels = (long)w * h * d;
        double vram   = voxels * _frameCount * 3.0 * 8 / (1024.0 * 1024.0);
        double bundle = vram * 0.315;
        _vramSizeLabel.text   = $"{vram:0.00} MB";
        _bundleSizeLabel.text = $"{bundle:0.00} MB";
    }

    // --- Validation -----------------------------------------------------

    string Validate()
    {
        if (_animator == null)     return "Assign an Animator.";
        if (_clip == null)         return "Assign an Animation Clip.";
        if (_targetVolume == null) return "Assign a Target Volume.";
        if (_targetVolume.BakeryVolume == null)
            return "Target Volume has no BakeryVolume child. Run a regular Bakery bake on it first to generate one.";
        return null;
    }

    // --- Bake loop ------------------------------------------------------

    void StartBake()
    {
        _baking = true;
        _currentFrame = 0;
        _secsPerFrame = -1;
        _collectedFrames.Clear();

        // Cache hierarchy paths now while references are guaranteed live.
        _animatorPath     = GetHierarchyPath(_animator.gameObject);
        _targetVolumePath = GetHierarchyPath(_targetVolume.gameObject);

        UpdateUI();
        BakeNextFrame();
    }

    // Returns the full hierarchy path of a GameObject (e.g. "Root/Child/Leaf").
    static string GetHierarchyPath(GameObject go)
    {
        string path = go.name;
        Transform t = go.transform.parent;
        while (t != null)
        {
            path = t.name + "/" + path;
            t = t.parent;
        }
        return path;
    }

    // Re-finds a component by hierarchy path after Bakery may have reloaded the scene.
    static T FindByPath<T>(string path) where T : Component
    {
        GameObject go = GameObject.Find(path);
        if (go == null)
        {
            Debug.LogError($"[ALVBakeTexture] Could not find GameObject at path \"{path}\" — was it renamed or destroyed?");
            return null;
        }
        return go.GetComponent<T>();
    }

    bool RefreshReferences()
    {
        Animator animator = FindByPath<Animator>(_animatorPath);
        if (animator == null) { AbortBake("Animator lost after scene reload!"); return false; }

        LightVolume volume = FindByPath<LightVolume>(_targetVolumePath);
        if (volume == null) { AbortBake("Target LightVolume lost after scene reload!"); return false; }

        _animator     = animator;
        _targetVolume = volume;

        // Keep the UI fields in sync so they don't show "Missing" after Bakery's scene reload.
        _animatorField?.SetValueWithoutNotify(_animator);
        _volumeField?.SetValueWithoutNotify(_targetVolume);
        return true;
    }

    void BakeNextFrame()
    {
        if (!RefreshReferences()) return;

        // Frame 0 = t=0, frame N-1 = t=clip.length (last frame inclusive).
        float t = _frameCount > 1
            ? _clip.length * _currentFrame / (_frameCount - 1)
            : 0f;

        // Force-evaluate the animator to the target time.
        _animator.Play(_clip.name, 0, t / _clip.length);
        _animator.Update(0f); 

        // Trigger a full Bakery bake. OnBakeFinished fires when it completes.
        _frameStopwatch.Restart();
        EditorWindow.GetWindow<ftRenderLightmap>().RenderButton(showMsgWindows: false);
    
        UpdateUI();
    }

    void OnBakeFinished(object sender, System.EventArgs e)
    {
        if (!_baking) return;

        _frameStopwatch.Stop();
        if (_secsPerFrame < 0)
            _secsPerFrame = _frameStopwatch.Elapsed.TotalSeconds;

        BakeryVolume bv = _targetVolume.BakeryVolume;
        if (bv.bakedTexture0 == null || bv.bakedTexture1 == null || bv.bakedTexture2 == null)
        {
            AbortBake($"BakeryVolume textures are null after bake on frame {_currentFrame}. Check Bakery output.");
            return;
        }

        _collectedFrames.Add(DeringFrame(
            bv.bakedTexture0.GetPixels(),
            bv.bakedTexture1.GetPixels(),
            bv.bakedTexture2.GetPixels()));

        _currentFrame++;

        if (_currentFrame < _frameCount)
            BakeNextFrame();
        else
            FinishBake();

        UpdateUI();
    }

    void FinishBake()
    {
        _baking = false;

        BakeryVolume bv = _targetVolume.BakeryVolume;
        int w = bv.bakedTexture0.width;
        int h = bv.bakedTexture0.height;
        int d = bv.bakedTexture0.depth;

        string scenePath = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
        string sceneName = Path.GetFileNameWithoutExtension(scenePath);
        string sceneDir  = Path.GetDirectoryName(scenePath);
        string assetDir  = $"{sceneDir}/{sceneName}/AnimatedLV";
        ALVEditor.CreateDirectory(assetDir);

        string assetPath = $"{assetDir}/{_outputName}.asset";
        GenerateALVTexture.SavePackedTexture(_collectedFrames.ToArray(), w, h, d, assetPath);

        new ALVTextureInfo { frameX = w, frameY = h, frameZ = d, numFrames = _frameCount }.Save(assetPath);

        // Reload references to make it faster to re-bake a sequence if needed.
        RefreshReferences();

        Debug.Log($"[ALVBakeTexture] Done. {_frameCount} frames baked into {assetPath} (frameX={w} frameY={h} frameZ={d})");
        UpdateUI();
    }

    // Applies SH dering per voxel to suppress L1 ringing from area lights.
    // Mirrors LVUtils.DeringSingleSH: clamps each channel's L1 magnitude to L0 * 1.13.
    static GenerateALVTexture.FrameSH DeringFrame(Color[] t0, Color[] t1, Color[] t2)
    {
        int count = t0.Length;
        Color[] r0 = new Color[count], r1 = new Color[count], r2 = new Color[count];
        for (int i = 0; i < count; i++)
        {
            // Packing layout:
            //   tex0: (L0.r, L0.g, L0.b, L1r.z)
            //   tex1: (L1r.x, L1g.x, L1b.x, L1g.z)
            //   tex2: (L1r.y, L1g.y, L1b.y, L1b.z)
            Vector3 L1r = LVUtils.DeringSingleSH(t0[i].r, new Vector3(t1[i].r, t2[i].r, t0[i].a));
            Vector3 L1g = LVUtils.DeringSingleSH(t0[i].g, new Vector3(t1[i].g, t2[i].g, t1[i].a));
            Vector3 L1b = LVUtils.DeringSingleSH(t0[i].b, new Vector3(t1[i].b, t2[i].b, t2[i].a));
            r0[i] = new Color(t0[i].r, t0[i].g, t0[i].b, L1r.z);
            r1[i] = new Color(L1r.x,   L1g.x,   L1b.x,   L1g.z);
            r2[i] = new Color(L1r.y,   L1g.y,   L1b.y,   L1b.z);
        }
        return new GenerateALVTexture.FrameSH { tex0 = r0, tex1 = r1, tex2 = r2 };
    }

    void AbortBake(string reason)
    {
        _baking = false;
        Debug.LogError($"[ALVBakeTexture] Bake aborted: {reason}");
        UpdateUI();
    }
}
#endif
