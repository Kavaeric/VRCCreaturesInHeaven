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
    private string     _outputPath = "Assets/Modules/Diamond/FixtureMap.json";

    [MenuItem("Tools/Diamond/Generate fixture map...")]
    private static void Open() => GetWindow<GenerateFixtureMap>("Generate fixture map");

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
        var fixtures = _root.GetComponentsInChildren<DiamondFixtureDefinition>();
        var groups   = _root.GetComponentsInChildren<DiamondFixtureGroupDefinition>();

        if (fixtures.Length == 0)
        {
            EditorUtility.DisplayDialog(
                "No fixtures found",
                $"No FixtureDefinition components were found under '{_root.name}'.",
                "OK");
            return;
        }

        string summary = $"Found {fixtures.Length} fixture{(fixtures.Length == 1 ? "" : "s")} " +
                         $"and {groups.Length} group{(groups.Length == 1 ? "" : "s")} under '{_root.name}'.\n\n" +
                         $"Output: {_outputPath}\n\nProceed?";

        bool confirmed = EditorUtility.DisplayDialog("Generate Fixture Map", summary, "Generate", "Cancel");
        if (!confirmed) return;

        Write(fixtures, groups);
    }

    private void Write(DiamondFixtureDefinition[] fixtures, DiamondFixtureGroupDefinition[] groups)
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

        // Build a lookup from fixture GameObject to its index in the fixtures array.
        var fixtureIndex = new Dictionary<GameObject, int>(fixtures.Length);
        for (int i = 0; i < fixtures.Length; i++)
            fixtureIndex[fixtures[i].gameObject] = i;

        var sb = new StringBuilder();
        sb.AppendLine("{");

        // --- fixtures ---
        sb.AppendLine("  \"items\": [");
        for (int i = 0; i < fixtures.Length; i++)
        {
            var f   = fixtures[i];
            var pos = f.transform.position;

            // XZ world-space mapped to canvas XY, centred on the rig bounding box.
            float cx = pos.x - centreX;
            float cy = pos.z - centreZ;

            string name  = EscapeJson(string.IsNullOrEmpty(f.DisplayName) ? f.gameObject.name : f.DisplayName);
            string fixtureGuid = GetSceneObjectGuid(f.gameObject);
            string comma = i < fixtures.Length - 1 ? "," : "";

            // Physical dimensions from profile: width = long axis (X), depth = short axis (Z).
            float nodeW = f.Profile != null ? f.Profile.FixtureWidth  : 0f;
            float nodeD = f.Profile != null ? f.Profile.FixtureHeight : 0f;

            sb.AppendLine("    {");
            sb.AppendLine($"      \"name\": \"{name}\",");
            sb.AppendLine($"      \"sceneObject\": \"{fixtureGuid}\",");
            sb.AppendLine($"      \"position\": {{ \"x\": {cx:F3}, \"y\": {cy:F3} }},");
            sb.AppendLine($"      \"size\": {{ \"x\": {nodeW:F3}, \"y\": {nodeD:F3} }}");
            sb.AppendLine($"    }}{comma}");
        }
        sb.AppendLine("  ],");

        // --- groups ---
        // For each FixtureGroupDefinition, collect the indices of all FixtureDefinition
        // descendants (at any depth within the group) that appear in our fixtures array.
        sb.AppendLine("  \"groups\": [");
        for (int gi = 0; gi < groups.Length; gi++)
        {
            var g = groups[gi];
            string groupName = EscapeJson(string.IsNullOrEmpty(g.DisplayName) ? g.gameObject.name : g.DisplayName);
            string groupGuid = GetSceneObjectGuid(g.gameObject);

            var memberIndices = new List<int>();
            foreach (var fd in g.GetComponentsInChildren<DiamondFixtureDefinition>())
            {
                if (fixtureIndex.TryGetValue(fd.gameObject, out int idx))
                    memberIndices.Add(idx);
            }

            string indicesJson = string.Join(", ", memberIndices);
            string comma = gi < groups.Length - 1 ? "," : "";

            sb.AppendLine("    {");
            sb.AppendLine($"      \"name\": \"{groupName}\",");
            sb.AppendLine($"      \"sceneObject\": \"{groupGuid}\",");
            sb.AppendLine($"      \"fixtures\": [{indicesJson}]");
            sb.AppendLine($"    }}{comma}");
        }
        sb.AppendLine("  ]");

        sb.AppendLine("}");

        string fullPath = Path.Combine(Application.dataPath, "../", _outputPath);
        fullPath = Path.GetFullPath(fullPath);

        string dir = Path.GetDirectoryName(fullPath);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);
        AssetDatabase.Refresh();

        Debug.Log($"  [GenerateFixtureMap] Wrote {fixtures.Length} fixture(s) and {groups.Length} group(s) to {_outputPath}");
        EditorUtility.DisplayDialog("Done", $"Wrote {fixtures.Length} fixture(s) and {groups.Length} group(s) to:\n{_outputPath}", "OK");
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
