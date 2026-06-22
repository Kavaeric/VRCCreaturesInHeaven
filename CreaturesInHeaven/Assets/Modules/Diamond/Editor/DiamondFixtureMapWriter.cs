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
    // and refreshes the AssetDatabase. Returns null on success, or a human-readable
    // error message if the path is invalid (so callers can show a clear dialog
    // instead of letting File.WriteAllText throw a cryptic UnauthorizedAccessException).
    public static string Write(
        DiamondFixtureDefinition[]      fixtures,
        DiamondFixtureGroupDefinition[] groups,
        string                          outputPath)
    {
        // Validate the output path before doing any work. The most common mistake is
        // pointing at a folder (no filename), which makes WriteAllText throw "access
        // denied" rather than anything informative.
        string pathError = ValidateOutputPath(outputPath);
        if (pathError != null)
            return pathError;

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

            // Beam cross-section shape: "rect" or "round". Drives how the fixture map
            // renderer draws the node. Defaults to rect when no profile is assigned.
            bool   isRound = f.Profile != null && f.Profile.Shape == DiamondFixtureProfile.BeamShape.Round;
            string shape   = isRound ? "round" : "rect";

            // Physical dimensions from profile: width = long axis (X), depth = short axis (Z).
            // Round fixtures use FixtureWidth as the emitter diameter and leave FixtureHeight
            // unused, so emit a square (diameter x diameter) rather than a zero-height node.
            float nodeW = f.Profile != null ? f.Profile.FixtureWidth  : 0f;
            float nodeD = isRound ? nodeW : (f.Profile != null ? f.Profile.FixtureHeight : 0f);

            // World yaw about Y, in degrees clockwise from world +X looking down the map.
            // The fixture map draws nodes in the XZ plane, so only the yaw matters here;
            // it rotates both the node footprint and the head-tilt indicator.
            float yaw = GetMapYaw(f.transform);

            sb.AppendLine("    {");
            sb.AppendLine($"      \"name\": \"{name}\",");
            sb.AppendLine($"      \"sceneObject\": \"{fixtureGuid}\",");
            sb.AppendLine($"      \"shape\": \"{shape}\",");
            sb.AppendLine($"      \"position\": {{ \"x\": {cx:F3}, \"y\": {cy:F3} }},");
            sb.AppendLine($"      \"size\": {{ \"x\": {nodeW:F3}, \"y\": {nodeD:F3} }},");
            sb.AppendLine($"      \"yaw\": {yaw:F3}");
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
        return null;
    }

    // Checks that outputPath is something we can actually write a JSON file to.
    // Returns null when valid, otherwise a message explaining what's wrong.
    private static string ValidateOutputPath(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            return "Output path is empty. Provide a folder and a .json filename.";

        if (!outputPath.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase))
            return $"Output path must end in .json, but was:\n{outputPath}";

        string fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../", outputPath));

        // If the path already exists as a directory, WriteAllText would throw an
        // unhelpful "access denied". Catch that here with a clear message.
        if (Directory.Exists(fullPath))
            return $"Output path points at an existing folder, not a file:\n{outputPath}";

        return null;
    }

    // Yaw of the fixture about world Y, in degrees, as used by the map renderer.
    //
    // The map draws the footprint in the XZ plane with the node's width along map +X.
    // That width is the fixture's local +X axis, so we measure how that axis is oriented
    // in world XZ. Using the axis projection (rather than transform.eulerAngles.y) keeps
    // the value stable when the fixture is also tilted/rolled on its root. Only the
    // in-plane component contributes, matching what the 2D map can actually show.
    //
    // Returned angle is clockwise from map +X (i.e. screen-space), since the writer maps
    // world +Z to map +Y (downward on screen). A fixture with no yaw returns 0.
    private static float GetMapYaw(Transform t)
    {
        Vector3 right = t.right;                       // fixture local +X in world space
        Vector2 inPlane = new Vector2(right.x, right.z);
        if (inPlane.sqrMagnitude < 1e-8f) return 0f;   // width axis is near-vertical; no meaningful yaw
        // atan2(z, x): angle from world +X toward +Z, which is the map's clockwise screen angle.
        return Mathf.Atan2(inPlane.y, inPlane.x) * Mathf.Rad2Deg;
    }

    // Returns a stable per-scene-object identifier via GlobalObjectId.
    // GlobalObjectId is not natively assigned to scene objects, but gives a
    // persistent cross-session reference when built from the local file ID.
    private static string GetSceneObjectGuid(GameObject go)
        => GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();

    private static string EscapeJson(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
}
