using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Serialises a fixture hierarchy to a FixtureMap.json file.
// No UI dependencies; called by DiamondEWinGenerateMap.
public static class DiamondFixtureMapWriter
{
    // Crawls fixtures and groups under the given root, writes the JSON to outputPath,
    // and refreshes the AssetDatabase.
    public static void Write(
        DiamondFixtureDefinition[]      fixtures,
        DiamondFixtureGroupDefinition[] groups,
        string                          outputPath)
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

            string name        = EscapeJson(string.IsNullOrEmpty(f.DisplayName) ? f.gameObject.name : f.DisplayName);
            string fixtureGuid = GetSceneObjectGuid(f.gameObject);
            string comma       = i < fixtures.Length - 1 ? "," : "";

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
            var g         = groups[gi];
            string name   = EscapeJson(string.IsNullOrEmpty(g.DisplayName) ? g.gameObject.name : g.DisplayName);
            string gGuid  = GetSceneObjectGuid(g.gameObject);

            var memberIndices = new List<int>();
            foreach (var fd in g.GetComponentsInChildren<DiamondFixtureDefinition>())
            {
                if (fixtureIndex.TryGetValue(fd.gameObject, out int idx))
                    memberIndices.Add(idx);
            }

            string indicesJson = string.Join(", ", memberIndices);
            string comma       = gi < groups.Length - 1 ? "," : "";

            sb.AppendLine("    {");
            sb.AppendLine($"      \"name\": \"{name}\",");
            sb.AppendLine($"      \"sceneObject\": \"{gGuid}\",");
            sb.AppendLine($"      \"fixtures\": [{indicesJson}]");
            sb.AppendLine($"    }}{comma}");
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");

        string fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../", outputPath));
        string dir = Path.GetDirectoryName(fullPath);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);
        AssetDatabase.Refresh();

        Debug.Log($"[Diamond] Wrote {fixtures.Length} fixture(s) and {groups.Length} group(s) to {outputPath}");
    }

    // Returns a stable per-scene-object identifier via GlobalObjectId.
    // GlobalObjectId is not natively assigned to scene objects, but gives a
    // persistent cross-session reference when built from the local file ID.
    private static string GetSceneObjectGuid(GameObject go)
        => GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();

    private static string EscapeJson(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
}
