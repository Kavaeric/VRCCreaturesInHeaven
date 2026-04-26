using UnityEngine;
using UnityEditor;
using UnityEngine.UI;

[CustomEditor(typeof(ShoeboxSky))]
public class ShoeboxSkyEditor : Editor
{
    private const int PLANE_COUNT = 10;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        ShoeboxSky sky = (ShoeboxSky)target;

        EditorGUILayout.Space();
        if (GUILayout.Button("Reset Imposters"))
        {
            if (sky.skyMaterial == null) return;

            // Zero all plane slots in the shader before re-pushing live imposter data,
            // so removed planes don't leave ghost values for pos/tangent/bitangent/size.
            for (int i = 0; i < PLANE_COUNT; i++)
            {
                string prefix = "_Plane" + i;
                sky.skyMaterial.SetVector(prefix + "Pos",       Vector4.zero);
                sky.skyMaterial.SetVector(prefix + "Tangent",   Vector4.zero);
                sky.skyMaterial.SetVector(prefix + "Bitangent", Vector4.zero);
                sky.skyMaterial.SetFloat( prefix + "Size",      0f);
                sky.skyMaterial.SetTexture(prefix + "Texture",  null);
            }

            sky.SetPlanes();
        }
    }
}

public class ShoeboxSkyGUI : ShaderGUI
{
    private const int PLANE_COUNT = 9;

    public override void OnGUI(MaterialEditor editor, MaterialProperty[] props)
    {
        Material mat = editor.target as Material;

        DrawHeader("Atmosphere");
        editor.ShaderProperty(FindProperty("_BakedTexture", props), "Baked Texture");
        editor.ShaderProperty(FindProperty("_Exposure", props), "Exposure");
        editor.ShaderProperty(FindProperty("_Rotation", props), "Rotation");

        DrawHeader("Ground");
        editor.ShaderProperty(FindProperty("_GroundEnabled", props), "Enabled");
        editor.ShaderProperty(FindProperty("_GroundTexture", props), "Texture");
        editor.ShaderProperty(FindProperty("_GroundScollX", props), "Scroll X");
        editor.ShaderProperty(FindProperty("_GroundScollY", props), "Scroll Y");
        editor.ShaderProperty(FindProperty("_Altitude", props), "Altitude (m)");

        DrawHeader("Sun disc");
        editor.ShaderProperty(FindProperty("_SunDiscRadius", props), "Radius");
        editor.ShaderProperty(FindProperty("_SunDiscBrightness", props), "Brightness");

        DrawHeader("Imposter planes");
        for (int i = 0; i < PLANE_COUNT; i++)
            DrawPlane(editor, props, mat, i);

        DrawHeader("Rendering");
        editor.ShaderProperty(FindProperty("_CullMode", props), "Cull Mode");

        EditorGUILayout.Space();
        editor.RenderQueueField();
        editor.EnableInstancingField();
    }

    private void DrawPlane(MaterialEditor editor, MaterialProperty[] props, Material mat, int index)
    {
        var texProp = FindProperty($"_Plane{index}Texture", props);

        // Show a filled dot on the header if this slot has a texture assigned.
        string label = texProp.textureValue != null ? $"● Plane {index}" : $"○ Plane {index}";

        string key = $"{mat.GetInstanceID()}_plane{index}";
        bool open = SessionState.GetBool(key, false);
        bool newOpen = EditorGUILayout.Foldout(open, label, true, EditorStyles.foldoutHeader);
        if (newOpen != open) SessionState.SetBool(key, newOpen);

        if (!newOpen) return;

        EditorGUI.indentLevel++;
        editor.ShaderProperty(texProp, "Texture");
        editor.ShaderProperty(FindProperty($"_Plane{index}Scroll", props), "Scroll");
        EditorGUI.indentLevel--;
    }

    private static void DrawHeader(string title)
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
    }
}
