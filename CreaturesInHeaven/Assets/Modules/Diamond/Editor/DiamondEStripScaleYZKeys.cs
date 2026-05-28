#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// One-off cleanup utility: removes m_LocalScale.y and .z curves from
// PropsTransform bindings in selected AnimationClip assets. Used to recover
// from earlier authoring where the y/z scale channels were accidentally keyed
// before they became meaningful (spread, beam intensity).
//
// Usage: select one or more AnimationClip assets in the Project window, then
// Tools > Diamond > Strip PropsTransform Y/Z Scale Keys.
public static class DiamondEStripScaleYZKeys
{
    [MenuItem("Tools/Diamond/Strip PropsTransform Y-Z Scale Keys")]
    private static void Strip()
    {
        var clips = Selection.GetFiltered<AnimationClip>(SelectionMode.Assets);
        if (clips.Length == 0)
        {
            EditorUtility.DisplayDialog("Strip Scale Y/Z Keys",
                "Select one or more AnimationClip assets in the Project window first.", "OK");
            return;
        }

        int totalRemoved = 0;
        int clipsTouched = 0;

        foreach (var clip in clips)
        {
            int removedThisClip = StripClip(clip);
            if (removedThisClip > 0)
            {
                clipsTouched++;
                totalRemoved += removedThisClip;
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[Diamond] Stripped {totalRemoved} curve(s) across {clipsTouched} clip(s).");
    }

    // Validate menu item: only enable when at least one AnimationClip is selected.
    [MenuItem("Tools/Diamond/Strip PropsTransform Y-Z Scale Keys", validate = true)]
    private static bool StripValidate()
    {
        return Selection.GetFiltered<AnimationClip>(SelectionMode.Assets).Length > 0;
    }

    // Diagnostic: prints every binding in the selected clip(s) to the Console,
    // so we can see what paths and property names actually exist before
    // running the destructive strip.
    [MenuItem("Tools/Diamond/Log Bindings In Selected Clip")]
    private static void LogBindings()
    {
        var clips = Selection.GetFiltered<AnimationClip>(SelectionMode.Assets);
        foreach (var clip in clips)
        {
            Debug.Log($"=== Bindings in '{clip.name}' ===", clip);
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                Debug.Log($"  type={b.type.Name}  path='{b.path}'  prop='{b.propertyName}'", clip);
            }
        }
    }

    [MenuItem("Tools/Diamond/Log Bindings In Selected Clip", validate = true)]
    private static bool LogBindingsValidate()
    {
        return Selection.GetFiltered<AnimationClip>(SelectionMode.Assets).Length > 0;
    }

    // Removes matching curves from one clip. Returns the count removed.
    private static int StripClip(AnimationClip clip)
    {
        var bindings = AnimationUtility.GetCurveBindings(clip);
        int removed = 0;

        // Build a list of bindings to remove first; mutating during enumeration
        // is fine in practice (the array is a snapshot) but explicit is safer.
        foreach (var binding in bindings)
        {
            if (!IsTargetBinding(binding)) continue;

            Undo.RecordObject(clip, "Strip PropsTransform Y-Z Scale Keys");
            // Setting the curve to null removes it.
            AnimationUtility.SetEditorCurve(clip, binding, null);
            removed++;
        }

        if (removed > 0)
        {
            EditorUtility.SetDirty(clip);
        }
        return removed;
    }

    // Matches a binding that targets m_LocalScale.y or m_LocalScale.z on a
    // Transform whose path ends in "PropsTransform". The path is the GameObject
    // hierarchy below the Animator root, slash-separated.
    private static bool IsTargetBinding(EditorCurveBinding binding)
    {
        // Only Transform component bindings are relevant for localScale.
        if (binding.type != typeof(Transform)) return false;

        if (binding.propertyName != "m_LocalScale.y" &&
            binding.propertyName != "m_LocalScale.z") return false;

        // path is e.g. "Rig/Fixture01/PropsTransform". Match the leaf segment.
        string path = binding.path;
        if (string.IsNullOrEmpty(path)) return false;

        int lastSlash = path.LastIndexOf('/');
        string leaf = lastSlash < 0 ? path : path.Substring(lastSlash + 1);

        // Match the new leaf name (_LampProps) as well as the legacy ones
        // (_PropsTransform / PropsTransform) so this tool works against clips
        // both before and after the props-transform-split refactor.
        return leaf == "_LampProps"
            || leaf == "LampProps"
            || leaf == "_PropsTransform"
            || leaf == "PropsTransform";
    }
}
#endif
