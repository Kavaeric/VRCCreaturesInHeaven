using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Domain types and layout computation for the Diamond fixture map.
// No UI dependencies; called by DiamondEWinFixtureMap.
public static class DiamondFixtureMapLayout
{
    // --- Data types --------------------------------------------------

    [Serializable]
    public struct FixtureEntry
    {
        public string          name;
        public string          sceneObject;
        public FixturePosition position;
        public FixturePosition size;  // width (X, long axis) and depth (Y, short axis) in metres
    }

    [Serializable]
    public struct GroupEntry
    {
        public string     name;
        public string     sceneObject;
        public List<int>  fixtures;  // indices into the fixtures array
    }

    [Serializable]
    public struct FixturePosition
    {
        public float x;
        public float y;
    }

    [Serializable]
    public struct SelectionGroup
    {
        public string    name;
        public List<int> fixtures;
    }

    [Serializable]
    public struct SelectionGroupFile
    {
        public List<SelectionGroup> groups;
    }

    // Precomputed layout for a single fixture node (logical space).
    public struct FixtureLayout
    {
        public Vector2 centre;   // logical-space centre
        public Vector2 halfExt;  // half-width and half-height
    }

    // Precomputed layout for a group bounding box (logical space).
    public struct GroupLayout
    {
        public Rect rect;   // bounding rect including padding
        public bool valid;  // false if the group has no valid member fixtures
    }

    // Result bundle from ComputeLogicalLayout.
    public struct LayoutResult
    {
        public List<FixtureLayout> fixtureLayouts;
        public List<GroupLayout>   groupLayouts;
        public Rect                logicalBounds;
    }

    // --- JSON parsing ------------------------------------------------

    [Serializable]
    private struct MapWrapper
    {
        public List<FixtureEntry> items;
        public List<GroupEntry>   groups;
    }

    public static void ParseMap(string json, out List<FixtureEntry> fixtures, out List<GroupEntry> groups)
    {
        var wrapper = JsonUtility.FromJson<MapWrapper>(json);
        fixtures = wrapper.items  ?? new List<FixtureEntry>();
        groups   = wrapper.groups ?? new List<GroupEntry>();
    }

    // --- Layout computation ------------------------------------------

    // Margin around outermost node edges when drawing a group box.
    public const float GroupMargin = 0.1f;

    // Recompute logical layout from fixture data and current layout parameters.
    public static LayoutResult ComputeLogicalLayout(
        List<FixtureEntry> fixtures,
        List<GroupEntry>   groups,
        float minGap,
        float gapCompressionK,
        float nodeCompressionK,
        float nodeCompressionThreshold,
        bool  flipY)
    {
        var result = new LayoutResult
        {
            fixtureLayouts = new List<FixtureLayout>(fixtures.Count),
            groupLayouts   = new List<GroupLayout>(groups.Count),
            logicalBounds  = new Rect(),
        };

        if (fixtures == null || fixtures.Count == 0) return result;

        // Build sorted lists of unique X and Y world positions.
        var uniqueX = new List<float>();
        var uniqueY = new List<float>();
        foreach (var f in fixtures)
        {
            if (!uniqueX.Contains(f.position.x)) uniqueX.Add(f.position.x);
            if (!uniqueY.Contains(f.position.y)) uniqueY.Add(f.position.y);
        }
        uniqueX.Sort();
        uniqueY.Sort();

        int cols = uniqueX.Count;
        int rows = uniqueY.Count;

        // Generate node sizes from fixture data, keeping raw world sizes for gap calculation.
        var worldSizesX = new float[cols];
        var worldSizesY = new float[rows];
        var nodeSizesX  = new float[cols];
        var nodeSizesY  = new float[rows];
        foreach (var f in fixtures)
        {
            int xi = uniqueX.IndexOf(f.position.x);
            int yi = uniqueY.IndexOf(f.position.y);
            worldSizesX[xi] = f.size.x;
            worldSizesY[yi] = f.size.y;
            var nodeSize = CompressNodeSize(f.size.x, f.size.y, nodeCompressionThreshold, nodeCompressionK);
            nodeSizesX[xi] = nodeSize.x;
            nodeSizesY[yi] = nodeSize.y;
        }

        // Sweep each axis independently using real world positions and fixture dimensions.
        var centresX = SweepAxis(uniqueX.ToArray(), worldSizesX, nodeSizesX, minGap, gapCompressionK);
        var centresY = SweepAxis(uniqueY.ToArray(), worldSizesY, nodeSizesY, minGap, gapCompressionK);

        // Compute logical-space bounding box.
        float layoutMinX = centresX[0]        - nodeSizesX[0]        * 0.5f;
        float layoutMaxX = centresX[cols - 1] + nodeSizesX[cols - 1] * 0.5f;
        float layoutMinY = centresY[0]        - nodeSizesY[0]        * 0.5f;
        float layoutMaxY = centresY[rows - 1] + nodeSizesY[rows - 1] * 0.5f;
        result.logicalBounds = new Rect(layoutMinX, layoutMinY, layoutMaxX - layoutMinX, layoutMaxY - layoutMinY);

        // Create fixture layouts in logical space (no viewport scaling).
        foreach (var f in fixtures)
        {
            int xi = uniqueX.IndexOf(f.position.x);
            int yi = uniqueY.IndexOf(f.position.y);
            int yiFlipped = flipY ? rows - 1 - yi : yi;

            result.fixtureLayouts.Add(new FixtureLayout
            {
                centre  = new Vector2(centresX[xi], centresY[yiFlipped]),
                halfExt = new Vector2(nodeSizesX[xi] * 0.5f, nodeSizesY[yi] * 0.5f),
            });
        }

        // Create group layouts in logical space.
        foreach (var g in groups)
        {
            if (g.fixtures == null || g.fixtures.Count == 0)
            {
                result.groupLayouts.Add(new GroupLayout { valid = false });
                continue;
            }

            float gMinX = float.MaxValue, gMaxX = float.MinValue;
            float gMinY = float.MaxValue, gMaxY = float.MinValue;
            bool any = false;

            foreach (int fi in g.fixtures)
            {
                if (fi < 0 || fi >= result.fixtureLayouts.Count) continue;
                var fl = result.fixtureLayouts[fi];
                float flMinX = fl.centre.x - fl.halfExt.x;
                float flMaxX = fl.centre.x + fl.halfExt.x;
                float flMinY = fl.centre.y - fl.halfExt.y;
                float flMaxY = fl.centre.y + fl.halfExt.y;

                if (flMinX < gMinX) gMinX = flMinX;
                if (flMaxX > gMaxX) gMaxX = flMaxX;
                if (flMinY < gMinY) gMinY = flMinY;
                if (flMaxY > gMaxY) gMaxY = flMaxY;
                any = true;
            }

            if (!any)
            {
                result.groupLayouts.Add(new GroupLayout { valid = false });
                continue;
            }

            result.groupLayouts.Add(new GroupLayout
            {
                valid = true,
                rect  = new Rect(
                    gMinX - GroupMargin,
                    gMinY - GroupMargin,
                    gMaxX - gMinX + GroupMargin * 2f,
                    gMaxY - gMinY + GroupMargin * 2f
                ),
            });
        }

        return result;
    }

    // --- Math helpers ------------------------------------------------

    // Compresses a distance, preserving values up to minDistance and soft-capping beyond it.
    // k controls compression strength: 0 = passthrough, higher = more aggressive.
    // The compressed value asymptotically approaches minDistance + 1/k for large distances.
    public static float CompressDistance(float distance, float minDistance, float k)
    {
        if (distance <= minDistance) return minDistance;
        float excess = distance - minDistance;
        return minDistance + excess / (1f + excess * k);
    }

    // Maps a fixture's raw world-space dimensions to layout-space node dimensions.
    // Handles particularly long and narrow fixtures by crushing their long axis past a threshold.
    public static Vector2 CompressNodeSize(float sizeX, float sizeY, float minAspect, float k)
    {
        float longAxis  = Mathf.Max(sizeX, sizeY);
        float shortAxis = Mathf.Min(sizeX, sizeY);

        // Don't handle cases where the ratio isn't too extreme.
        if (longAxis / shortAxis <= minAspect) return new(sizeX, sizeY);

        // For extreme aspect ratios, compress the long axis past the threshold.
        float compressed = CompressDistance(longAxis, shortAxis * minAspect, k);

        // Reconstruct with the same orientation as the input.
        return sizeX >= sizeY ? new(compressed, shortAxis) : new(shortAxis, compressed);
    }

    // Returns layout-space centre positions for n nodes swept along one axis.
    // worldPositions[i] is the world coordinate of node i (sorted ascending).
    // worldSizes[i] is the raw world-space size, used to compute edge-to-edge gaps.
    // nodeSizes[i] is the layout-space size (may differ from worldSizes after compression).
    public static float[] SweepAxis(float[] worldPositions, float[] worldSizes, float[] nodeSizes, float minGap, float k)
    {
        int n = nodeSizes.Length;
        var centres = new float[n];
        float cursor = 0f;
        for (int i = 0; i < n; i++)
        {
            centres[i] = cursor + nodeSizes[i] * 0.5f;
            cursor += nodeSizes[i];
            if (i < n - 1)
            {
                // Edge-to-edge gap in world space, using raw sizes to avoid compression artifacts.
                float worldGap = worldPositions[i + 1] - worldPositions[i]
                                 - worldSizes[i] * 0.5f - worldSizes[i + 1] * 0.5f;
                cursor += CompressDistance(worldGap, minGap, k);
            }
        }
        return centres;
    }

    // --- Path utilities ----------------------------------------------

    public static string ToProjectRelative(string absolutePath)
    {
        string dataPath = Application.dataPath.Replace('\\', '/');
        string absNorm  = absolutePath.Replace('\\', '/');
        if (absNorm.StartsWith(dataPath))
            return "Assets" + absNorm.Substring(dataPath.Length);
        return null;
    }
}
