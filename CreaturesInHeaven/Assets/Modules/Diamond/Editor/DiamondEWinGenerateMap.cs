#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// RETIRED (DIAMOND-MANAGER.md, stage 3). The fixture map is now baked on-script
// onto DiamondManagerDefinition (DiamondManagerDefinition.BakeFixtures) and read
// live by DiamondEWinFixtureMap -- there is no FixtureMap.json anymore. This
// window (and its DiamondFixtureMapWriter) are kept only as a reference for how
// the JSON crawl/serialisation worked. Its menu item is disabled so nobody
// accidentally generates a now-ignored JSON file; re-enable the [MenuItem]
// below only if you deliberately want the old JSON export back.
//
// Editor window that crawls a chosen hierarchy root for FixtureDefinition components
// and writes a FixtureMap.json to the specified path.
public class DiamondEWinGenerateMap : EditorWindow
{
    private GameObject _root;
    private string     _outputFolder   = "Assets/Modules/Diamond";
    private string     _outputFileName = "FixtureMap.json";

    // Project-relative path assembled from the folder and filename fields.
    private string OutputPath => $"{_outputFolder.TrimEnd('/', '\\')}/{_outputFileName}";

    // Menu item disabled: retired path (see header). Left in place, commented,
    // so the wiring is still visible for reference.
    // [MenuItem("Tools/Diamond/Generate fixture map...")]
    private static void Open() => GetWindow<DiamondEWinGenerateMap>("Generate fixture map");

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Fixture map generator", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        _root = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Root object", "Crawl this object and all its children for FixtureDefinition components."),
            _root, typeof(GameObject), allowSceneObjects: true);

        EditorGUILayout.Space(4);

        _outputFolder = EditorGUILayout.TextField(
            new GUIContent("Output folder", "Project-relative folder for the generated JSON file."),
            _outputFolder);

        _outputFileName = EditorGUILayout.TextField(
            new GUIContent("File name", "Name of the generated JSON file, including the .json extension."),
            _outputFileName);

        EditorGUILayout.LabelField(" ", OutputPath, EditorStyles.miniLabel);

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
                         $"Output: {OutputPath}\n\nProceed?";

        bool confirmed = EditorUtility.DisplayDialog("Generate Fixture Map", summary, "Generate", "Cancel");
        if (!confirmed) return;

        Write(fixtures, groups);
    }

    private void Write(DiamondFixtureDefinition[] fixtures, DiamondFixtureGroupDefinition[] groups)
    {
        string error = DiamondFixtureMapWriter.Write(fixtures, groups, OutputPath);
        if (error != null)
        {
            EditorUtility.DisplayDialog("Cannot write fixture map", error, "OK");
            return;
        }

        EditorUtility.DisplayDialog("Done", $"Wrote {fixtures.Length} fixture(s) and {groups.Length} group(s) to:\n{OutputPath}", "OK");
    }
}
#endif
