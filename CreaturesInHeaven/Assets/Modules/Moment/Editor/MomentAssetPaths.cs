using System.IO;
using UnityEditor;
using UnityEngine.SceneManagement;

// Path computation and folder creation for the Moment ALV module.
public static class MomentAssetPaths
{
    // Returns the project-relative asset directory used for generated Moment assets,
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

    // Returns the project-relative path to the directory containing the Moment editor scripts.
    public static string ScriptDir()
    {
        foreach (var guid in AssetDatabase.FindAssets($"t:MonoScript MomentTextureWriter"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.EndsWith("MomentTextureWriter.cs"))
                return Path.GetDirectoryName(path).Replace('\\', '/');
        }
        return "Assets/Modules/Moment/Editor";
    }
}
