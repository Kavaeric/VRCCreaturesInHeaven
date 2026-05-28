#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// One-off migration utility: rewrites animation curves on _LampProps from
// m_LocalScale.x to m_LocalPosition.y. Pair with the brightness-to-position
// refactor (so _LampProps.localScale is free for a future RGB colour vector).
//
// Usage: select one or more AnimationClip assets in the Project window, then
// Tools > Diamond > Migrate _LampProps Brightness (Scale.x -> Position.y).
public static class DiamondEMigrateBrightnessCurves
{
    private const string TargetLeaf  = "_LampProps";
    private const string OldProperty = "m_LocalScale.x";
    private const string NewProperty = "m_LocalPosition.y";

    [MenuItem("Tools/Diamond/Migrate _LampProps Brightness (Scale.x to Position.y)")]
    private static void Migrate()
    {
        var clips = Selection.GetFiltered<AnimationClip>(SelectionMode.Assets);
        if (clips.Length == 0)
        {
            EditorUtility.DisplayDialog("Migrate Brightness Curves",
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
        Debug.Log($"[Diamond] Rewrote {totalRewritten} brightness curve(s) across {clipsTouched} clip(s).");
    }

    [MenuItem("Tools/Diamond/Migrate _LampProps Brightness (Scale.x to Position.y)", validate = true)]
    private static bool MigrateValidate()
    {
        return Selection.GetFiltered<AnimationClip>(SelectionMode.Assets).Length > 0;
    }

    private static int MigrateClip(AnimationClip clip)
    {
        int rewritten = 0;

        foreach (var binding in AnimationUtility.GetCurveBindings(clip))
        {
            if (!IsTargetBinding(binding)) continue;

            Undo.RecordObject(clip, "Migrate Brightness Curves");

            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            var newBinding = new EditorCurveBinding
            {
                path         = binding.path,
                type         = binding.type,
                propertyName = NewProperty,
            };

            AnimationUtility.SetEditorCurve(clip, newBinding, curve);
            AnimationUtility.SetEditorCurve(clip, binding, null);
            rewritten++;
        }

        if (rewritten > 0)
        {
            EditorUtility.SetDirty(clip);
        }
        return rewritten;
    }

    // Matches a Transform binding whose path leaf is _LampProps and whose
    // property is m_LocalScale.x (the pre-refactor brightness slot).
    private static bool IsTargetBinding(EditorCurveBinding binding)
    {
        if (binding.type != typeof(Transform)) return false;
        if (binding.propertyName != OldProperty) return false;

        string path = binding.path;
        if (string.IsNullOrEmpty(path)) return false;

        int lastSlash = path.LastIndexOf('/');
        string leaf = lastSlash < 0 ? path : path.Substring(lastSlash + 1);
        return leaf == TargetLeaf;
    }
}
#endif
