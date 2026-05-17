using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;

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
}
