#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// One-off migration utility: rewrites animation curve paths from
// _PropsTransform -> _LampProps for selected AnimationClips. Pair with the
// prefab rename done in step 5 of the props-transform-split refactor.
//
// Usage: select one or more AnimationClip assets in the Project window, then
// Tools > Diamond > Migrate _PropsTransform Paths to _LampProps.
public static class DiamondEMigratePropsPaths
{
    private const string OldLeaf = "_PropsTransform";
    private const string NewLeaf = "_LampProps";

    [MenuItem("Tools/Diamond/Migrate _PropsTransform Paths to _LampProps")]
    private static void Migrate()
    {
        var clips = Selection.GetFiltered<AnimationClip>(SelectionMode.Assets);
        if (clips.Length == 0)
        {
            EditorUtility.DisplayDialog("Migrate Props Paths",
                "Select one or more AnimationClip assets in the Project window first.", "OK");
            return;
        }

        int totalRewritten = 0;
        int clipsTouched = 0;

        foreach (var clip in clips)
        {
            int rewrittenThisClip = MigrateClip(clip);
            if (rewrittenThisClip > 0)
            {
                clipsTouched++;
                totalRewritten += rewrittenThisClip;
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[Diamond] Rewrote {totalRewritten} curve binding(s) across {clipsTouched} clip(s).");
    }

    [MenuItem("Tools/Diamond/Migrate _PropsTransform Paths to _LampProps", validate = true)]
    private static bool MigrateValidate()
    {
        return Selection.GetFiltered<AnimationClip>(SelectionMode.Assets).Length > 0;
    }

    private static int MigrateClip(AnimationClip clip)
    {
        int rewritten = 0;

        // Float curves.
        foreach (var binding in AnimationUtility.GetCurveBindings(clip))
        {
            if (!IsTargetBinding(binding)) continue;

            Undo.RecordObject(clip, "Migrate Props Paths");

            var curve     = AnimationUtility.GetEditorCurve(clip, binding);
            var newBinding = RewriteBinding(binding);

            AnimationUtility.SetEditorCurve(clip, newBinding, curve);
            AnimationUtility.SetEditorCurve(clip, binding, null);
            rewritten++;
        }

        // Object reference curves (defensive -- no Diamond bindings use these
        // today, but iterate so we don't silently leave them behind).
        foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
        {
            if (!IsTargetBinding(binding)) continue;

            Undo.RecordObject(clip, "Migrate Props Paths");

            var keys       = AnimationUtility.GetObjectReferenceCurve(clip, binding);
            var newBinding = RewriteBinding(binding);

            AnimationUtility.SetObjectReferenceCurve(clip, newBinding, keys);
            AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
            rewritten++;
        }

        if (rewritten > 0)
        {
            EditorUtility.SetDirty(clip);
        }
        return rewritten;
    }

    // Matches a binding whose path's last segment is the old leaf name.
    // We don't restrict by property name or component type -- we want to move
    // every curve on the renamed object, not just scale ones.
    private static bool IsTargetBinding(EditorCurveBinding binding)
    {
        string path = binding.path;
        if (string.IsNullOrEmpty(path)) return false;

        int lastSlash = path.LastIndexOf('/');
        string leaf = lastSlash < 0 ? path : path.Substring(lastSlash + 1);
        return leaf == OldLeaf;
    }

    private static EditorCurveBinding RewriteBinding(EditorCurveBinding source)
    {
        string path = source.path;
        int lastSlash = path.LastIndexOf('/');
        string newPath = lastSlash < 0
            ? NewLeaf
            : path.Substring(0, lastSlash + 1) + NewLeaf;

        return new EditorCurveBinding
        {
            path         = newPath,
            type         = source.type,
            propertyName = source.propertyName,
        };
    }
}
#endif
