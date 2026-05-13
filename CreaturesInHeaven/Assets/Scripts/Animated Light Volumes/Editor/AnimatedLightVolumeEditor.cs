using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using VRCLightVolumes;

[CustomEditor(typeof(AnimatedLightVolume))]
public class AnimatedLightVolumeEditor : Editor
{
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
            EditorGUILayout.HelpBox("Assign an animator and create float parameters matching the names above to start animating this Light Volume.a", MessageType.Info);
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

        // --- Info ----------------------------------------------------
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);

        if (alv.AnimatedTexture != null)
        {
            int numFrames = alv.AnimatedTexture.height / (alv.AnimatedTexture.depth / 3);
            EditorGUILayout.LabelField("Frames", numFrames.ToString());
        }
        else
        {
            EditorGUILayout.LabelField("Frames", "—");
        }

        serializedObject.ApplyModifiedProperties();
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

        // Configure CRT — the LightVolumeSetup will enforce these too, but set
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
