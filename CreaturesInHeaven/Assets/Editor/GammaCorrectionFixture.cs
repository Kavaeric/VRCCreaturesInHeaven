using UnityEditor;
using UnityEngine;

public class GammaCorrectionFixture
{
    [MenuItem("Tools/Fix Fixture Brightness Gamma Correction")]
    private static void FixBrightnessGamma()
    {
        // Open file browser to select the animation clip
        string path = EditorUtility.OpenFilePanel("Select Animation Clip", "Assets", "anim");
        if (string.IsNullOrEmpty(path))
            return;

        // Convert to project-relative path
        string projectRelativePath = path.Replace(Application.dataPath, "Assets");
        AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(projectRelativePath);
        if (clip == null)
        {
            EditorUtility.DisplayDialog("Error", "Could not load animation clip at: " + projectRelativePath, "OK");
            return;
        }

        ConvertClipBrightness(clip);
        EditorUtility.DisplayDialog("Success", "Brightness values have been converted from gamma to linear space.", "OK");
    }

    private static void ConvertClipBrightness(AnimationClip clip)
    {
        EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);

        foreach (var binding in bindings)
        {
            // Only process localScale.x curves for any object
            if (binding.propertyName != "m_LocalScale.x")
                continue;

            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null) continue;

            // Convert all keyframe values from gamma-corrected back to linear
            Keyframe[] keyframes = curve.keys;
            for (int i = 0; i < keyframes.Length; i++)
            {
                // The stored value is the result of GammaToLinearSpace(sliderValue)
                // So to reverse it, we apply LinearToGammaSpace(storedValue) to get back to sliderValue
                keyframes[i].value = Mathf.LinearToGammaSpace(keyframes[i].value);
            }

            curve.keys = keyframes;
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }

        EditorUtility.SetDirty(clip);
        AssetDatabase.SaveAssets();
    }
}
