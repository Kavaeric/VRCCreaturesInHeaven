#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Editor window that crawls a chosen hierarchy root for FixtureDefinition components
// and writes a FixtureMap.json to the specified path.
//
// Open via: Window > Lighting > Generate Fixture Map
public class GenerateFixtureMap : EditorWindow
{
    private GameObject _root;
    private string     _outputPath = "Assets/Scripts/Lighting/FixtureMap.json";

    [MenuItem("Tools/Generate Fixture Map")]
    private static void Open() => GetWindow<GenerateFixtureMap>("Generate Fixture Map");

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Fixture Map Generator", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        _root = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Root object", "Crawl this object and all its children for FixtureDefinition components."),
            _root, typeof(GameObject), allowSceneObjects: true);

        EditorGUILayout.Space(4);

        _outputPath = EditorGUILayout.TextField(
            new GUIContent("Output path", "Project-relative path for the generated JSON file."),
            _outputPath);

        EditorGUILayout.Space(12);

        EditorGUI.BeginDisabledGroup(_root == null);
        if (GUILayout.Button("Generate…"))
            TryGenerate();
        EditorGUI.EndDisabledGroup();

        if (_root == null)
            EditorGUILayout.HelpBox("Assign a root object to enable generation.", MessageType.Info);
    }

    private void TryGenerate()
    {
        var fixtures = _root.GetComponentsInChildren<FixtureDefinition>();

        if (fixtures.Length == 0)
        {
            EditorUtility.DisplayDialog(
                "No fixtures found",
                $"No FixtureDefinition components were found under '{_root.name}'.",
                "OK");
            return;
        }

        string summary = $"Found {fixtures.Length} fixture{(fixtures.Length == 1 ? "" : "s")} under '{_root.name}'.\n\n" +
                         $"Output: {_outputPath}\n\n" +
                         "Proceed?";

        bool confirmed = EditorUtility.DisplayDialog("Generate Fixture Map", summary, "Generate", "Cancel");
        if (!confirmed) return;

        Write(fixtures);
    }

    private void Write(FixtureDefinition[] fixtures)
    {
        // Compute the XZ bounding box so we can centre the canvas layout at 0,0.
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;

        foreach (var f in fixtures)
        {
            Vector3 pos = f.transform.position;
            if (pos.x < minX) minX = pos.x;
            if (pos.x > maxX) maxX = pos.x;
            if (pos.z < minZ) minZ = pos.z;
            if (pos.z > maxZ) maxZ = pos.z;
        }

        float centreX = (minX + maxX) * 0.5f;
        float centreZ = (minZ + maxZ) * 0.5f;

        var sb = new StringBuilder();
        sb.AppendLine("[");

        for (int i = 0; i < fixtures.Length; i++)
        {
            var f = fixtures[i];
            var pos = f.transform.position;

            // XZ world-space mapped to canvas XY, centred on the rig bounding box.
            float cx = pos.x - centreX;
            float cy = pos.z - centreZ;

            string name = EscapeJson(string.IsNullOrEmpty(f.DisplayName) ? f.gameObject.name : f.DisplayName);
            string guid = GetSceneObjectGuid(f.gameObject);
            string comma = i < fixtures.Length - 1 ? "," : "";

            // Physical dimensions from profile: width = long axis (X), depth = short axis (Z).
            float nodeW = f.Profile != null ? f.Profile.FixtureWidth : 0f;
            float nodeD = f.Profile != null ? f.Profile.FixtureHeight : 0f;

            sb.AppendLine("  {");
            sb.AppendLine($"    \"name\": \"{name}\",");
            sb.AppendLine($"    \"sceneObject\": \"{guid}\",");
            sb.AppendLine($"    \"position\": {{ \"x\": {cx:F3}, \"y\": {cy:F3} }},");
            sb.AppendLine($"    \"size\": {{ \"x\": {nodeW:F3}, \"y\": {nodeD:F3} }}");
            sb.AppendLine($"  }}{comma}");
        }

        sb.AppendLine("]");

        string fullPath = Path.Combine(Application.dataPath, "../", _outputPath);
        fullPath = Path.GetFullPath(fullPath);

        string dir = Path.GetDirectoryName(fullPath);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);
        AssetDatabase.Refresh();

        Debug.Log($"[GenerateFixtureMap] Wrote {fixtures.Length} fixture(s) to {_outputPath}");
        EditorUtility.DisplayDialog("Done", $"Wrote {fixtures.Length} fixture(s) to:\n{_outputPath}", "OK");
    }

    // Returns a stable per-scene-object identifier by combining the scene GUID
    // with the GameObject's local file identifier (GUID is not assigned to scene objects
    // natively, but GlobalObjectId gives us a persistent cross-session reference).
    private static string GetSceneObjectGuid(GameObject go)
    {
        var id = GlobalObjectId.GetGlobalObjectIdSlow(go);
        return id.ToString();
    }

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
}
#endif
