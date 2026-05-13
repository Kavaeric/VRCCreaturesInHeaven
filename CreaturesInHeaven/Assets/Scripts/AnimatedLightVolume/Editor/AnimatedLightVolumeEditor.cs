using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using VRCLightVolumes;

[CustomEditor(typeof(AnimatedLightVolume))]
public class AnimatedLightVolumeEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        AnimatedLightVolume alv = (AnimatedLightVolume)target;

        EditorGUILayout.Space(8);

        if (GUILayout.Button("Set Up CRT"))
        {
            Setup(alv);
        }

        if (alv.Crt != null && alv.TargetVolume == null)
        {
            EditorGUILayout.HelpBox("Assign a Target Volume to complete setup.", MessageType.Warning);
        }
    }

    static void Setup(AnimatedLightVolume alv)
    {
        string assetDir = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(alv)));

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
            string matPath = $"{assetDir}/{alv.gameObject.name}_CRT.mat";
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
            string crtPath = $"{assetDir}/{alv.gameObject.name}_CRT.asset";
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
            Debug.LogWarning("[AnimatedLightVolume] No LightVolumeSetup found in scene — register the CRT manually.");
        }

        EditorUtility.SetDirty(alv);
        EditorUtility.SetDirty(crt);
        AssetDatabase.SaveAssets();

        Debug.Log($"[AnimatedLightVolume] Setup complete for '{alv.gameObject.name}'.");
    }
}
