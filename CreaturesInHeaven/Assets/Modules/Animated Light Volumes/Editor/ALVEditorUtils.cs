using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using UnityEngine.SceneManagement;
using VRCLightVolumes;

// Shared editor utilities for the AnimatedLightVolume module.
public static class ALVEditorUtils
{
    // Returns the project-relative asset directory used for generated ALV assets,
    // derived from the currently active scene path.
    public static string SceneAssetDir()
    {
        string scenePath = SceneManager.GetActiveScene().path;
        string sceneName = Path.GetFileNameWithoutExtension(scenePath);
        string sceneDir  = Path.GetDirectoryName(scenePath).Replace('\\', '/');
        return $"{sceneDir}/{sceneName}/AnimatedLV";
    }

    // Creates each segment of a folder path that doesn't already exist.
    public static void CreateDirectory(string path)
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

    // Returns the full hierarchy path of a GameObject (e.g. "Root/Child/Leaf").
    public static string GetHierarchyPath(GameObject go)
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

    // Returns the project-relative path to the directory containing the ALV editor scripts.
    public static string ScriptDir()
    {
        foreach (var guid in AssetDatabase.FindAssets($"t:MonoScript ALVTextureBaker"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.EndsWith("ALVTextureBaker.cs"))
                return Path.GetDirectoryName(path).Replace('\\', '/');
        }
        return "Assets/Modules/Animated Light Volumes/Editor";
    }

    // Re-finds a component by hierarchy path after a scene reload.
    public static T FindByPath<T>(string path) where T : Component
    {
        GameObject go = GameObject.Find(path);
        if (go == null)
        {
            Debug.LogError($"[ALV] Could not find GameObject at path \"{path}\". Was it renamed or destroyed?");
            return null;
        }
        return go.GetComponent<T>();
    }

    // Creates or reconfigures the CRT and material assets for an AnimatedLightVolume,
    // then registers the CRT with the scene's LightVolumeSetup post-processor chain.
    public static void SetupCRT(AnimatedLightVolume alv)
    {
        // Mirror the Light Volumes package convention: store generated assets
        // alongside the scene they belong to, not next to the script.
        string assetDir = SceneAssetDir();
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
        crt.dimension      = TextureDimension.Tex3D;
        crt.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
        crt.updateMode     = CustomRenderTextureUpdateMode.Realtime;
        crt.material       = mat;

        alv.Crt = crt;

        // Register with the LightVolumeSetup post-processor chain.
        LightVolumeSetup setup = Object.FindObjectOfType<LightVolumeSetup>();
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
}
