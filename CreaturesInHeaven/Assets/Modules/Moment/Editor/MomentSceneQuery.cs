using UnityEngine;
using UnityEditor;

// Read-only scene and hierarchy inspection for the Moment module.
public static class MomentSceneQuery
{
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
            Debug.LogError($"[Moment] Could not find GameObject at path \"{path}\". Was it renamed or destroyed?");
            return null;
        }
        return go.GetComponent<T>();
    }

    // Scans an Animator's parameters for a float with the given name.
    // Returns its current value if found, or null if missing or the animator is null.
    public static float? FindAnimatorFloatParam(Animator animator, string paramName)
    {
        if (animator == null || string.IsNullOrEmpty(paramName)) return null;
        foreach (var param in animator.parameters)
        {
            if (param.name == paramName && param.type == AnimatorControllerParameterType.Float)
                return animator.GetFloat(paramName);
        }
        return null;
    }
}
