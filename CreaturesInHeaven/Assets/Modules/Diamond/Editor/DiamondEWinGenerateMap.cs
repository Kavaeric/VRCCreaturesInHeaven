#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// Editor window that crawls a chosen hierarchy root for FixtureDefinition components
// and writes a FixtureMap.json to the specified path.
//
// Open via: Window > Lighting > Generate Fixture Map
public class DiamondEWinGenerateMap : EditorWindow
{
    private GameObject _root;
    private string     _outputPath = "Assets/Modules/Diamond/FixtureMap.json";

    [MenuItem("Tools/Diamond/Generate fixture map...")]
    private static void Open() => GetWindow<DiamondEWinGenerateMap>("Generate fixture map");

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Fixture map generator", EditorStyles.boldLabel);
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
        DiamondFixtureMapWriter.Write(fixtures, groups, _outputPath);
        EditorUtility.DisplayDialog("Done", $"Wrote {fixtures.Length} fixture(s) and {groups.Length} group(s) to:\n{_outputPath}", "OK");
    }
}
#endif
