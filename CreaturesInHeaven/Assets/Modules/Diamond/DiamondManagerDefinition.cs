using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// Edit-time authoring / bake layer for a DiamondManager. Attach alongside a
// DiamondManager on the manager root. This component is a plain MonoBehaviour
// (not Udon) and does no runtime work -- its job is to crawl the fixtures under
// the root and populate the manager's parallel arrays, so that at runtime the
// manager just reads serialized data.
//
// This is the "entities don't have inspectors, tooling projects a view onto the
// data" seam (see DIAMOND-MANAGER.md, stage 3). For stage 1 the bake is
// deliberately minimal: it crawls DiamondFixtureDefinition components (the same
// crawl DiamondFixtureMapWriter uses) and reads each fixture's object graph and
// derived values straight off DiamondFixtureDefinition (which sources them from
// the profile and beam material). No DiamondFixtureDriver dependency. Stable
// identity / index tracking is stage 3.
//
// Bake is manual (a "Bake fixtures" inspector button / context menu), never
// automatic, so it can't clobber the arrays mid-edit.
public class DiamondManagerDefinition : MonoBehaviour
{
    // Display label for this manager (multi-manager organisation, presets, etc).
    public string DisplayName;

    // --- Baked fixture map (edit-time only) --------------------------
    // The fixture map's presentation data, baked here by the same crawl that
    // fills the runtime DiamondManager arrays. Index-aligned with the manager's
    // arrays: MapPositions[i] describes the same fixture as DiamondManager's
    // LampProps[i]. This is the on-script replacement for the old
    // FixtureMap.json -- it lives on the Definition (not the Udon manager) so it
    // is stripped at build and never ships into the runtime world. See
    // DIAMOND-MANAGER.md, stage 3.

    // Per-fixture display name (DisplayName override, else GameObject name).
    public string[]  MapNames;
    // XZ world position mapped to canvas XY, centred on the rig bounding box.
    public Vector2[] MapPositions;
    // Node footprint size: width (long axis) and depth (short axis), metres.
    public Vector2[] MapSizes;
    // World yaw about Y, degrees clockwise from map +X (see DiamondFixtureMapWriter.GetMapYaw).
    public float[]   MapYaw;
    // Beam cross-section: true = round (draw a disc), false = rect.
    public bool[]    MapRound;

    // Fixture groups, captured from DiamondFixtureGroupDefinition components under
    // the root. Members are stored by stable identity (GlobalObjectId string), not
    // array index, so a re-bake that reorders fixtures can't misassign them -- the
    // map window resolves identity -> current index at load time. Mirrors how the
    // map already persists selection groups by GID.
    [System.Serializable]
    public struct BakedGroup
    {
        public string       name;
        public string       sceneId;      // the group object's own identity
        public List<string> memberIds;    // member fixtures, by GlobalObjectId
    }
    public List<BakedGroup> Groups = new List<BakedGroup>();

    // Persisted selection groups (the map window's user-authored groupings). Also
    // by identity, replacing the old FixtureMap.json.selections.json sidecar.
    [System.Serializable]
    public struct SelectionGroup
    {
        public string       name;
        public List<string> memberIds;    // by GlobalObjectId
    }
    public List<SelectionGroup> SelectionGroups = new List<SelectionGroup>();

#if UNITY_EDITOR
    // Crawls the fixtures under this root and fills the sibling DiamondManager's
    // arrays index-aligned, then bakes each beam renderer's culling bounds.
    // Editor-only: the whole point is that runtime never re-derives this.
    [ContextMenu("Bake fixtures")]
    public void BakeFixtures()
    {
        var manager = GetComponent<DiamondManager>();
        if (manager == null)
        {
            Debug.LogError("[Diamond] DiamondManagerDefinition needs a DiamondManager on the same object.", this);
            return;
        }

        // Same crawl as the fixture map: every DiamondFixtureDefinition under the
        // root, in hierarchy order. Order defines the fixture indices for now
        // (stage 3 replaces this with a stable identity -> index scheme).
        var defs = GetComponentsInChildren<DiamondFixtureDefinition>();
        int count = defs.Length;

        var fixtures = new GameObject[count];
        var fixtureEmitterSizes = new Vector2[count];
        var lampProps     = new Transform[count];
        var beamProps     = new Transform[count];
        var heads         = new Transform[count];
        var headRenderers = new Renderer[count];
        var beamRenderers = new Renderer[count];
        var symmetricBeam = new bool[count];
        var sceneIds      = new string[count];

        // Map (presentation) data, index-aligned with the arrays above. Baked in
        // the same crawl so the runtime graph and the map view share one index
        // space -- that shared index space is the whole point of folding the map
        // onto the manager (see DIAMOND-MANAGER.md, stage 3). These land on this
        // Definition, not the runtime manager, so they're build-stripped.
        var mapNames     = new string[count];
        var mapPositions = new Vector2[count];
        var mapSizes     = new Vector2[count];
        var mapYaw       = new float[count];
        var mapRound     = new bool[count];

        // Rig bounding-box centre, so map positions are centred on the rig rather
        // than world origin. Pre-pass over the fixtures' XZ, mirroring the old
        // DiamondFixtureMapWriter. Guard the empty case so the min/max sentinels
        // don't produce a NaN centre.
        float centreX = 0f, centreZ = 0f;
        if (count > 0)
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                Vector3 pos = defs[i].transform.position;
                if (pos.x < minX) minX = pos.x;
                if (pos.x > maxX) maxX = pos.x;
                if (pos.z < minZ) minZ = pos.z;
                if (pos.z > maxZ) maxZ = pos.z;
            }
            centreX = (minX + maxX) * 0.5f;
            centreZ = (minZ + maxZ) * 0.5f;
        }

        // Bounds ceilings for animated atmosphere. When haze/scatter animate, the
        // culling AABB must be sized to the manager's runtime max (the ceiling the
        // proxy is clamped to), not the material's static value. A static param
        // passes -1 so ComputeBeamBounds uses the material, as before.
        float hazeCeiling    = manager.AnimateHaze    ? manager.MaxHazeDensity     : -1f;
        float scatterCeiling = manager.AnimateScatter ? manager.MaxScatterStrength : -1f;

        int missing = 0;
        for (int i = 0; i < count; i++)
        {
            var def = defs[i];

            // Record the fixture's stable scene identity, index-aligned with the
            // reference arrays. This is now the key the map's groups resolve
            // against (stage 3). Same GlobalObjectId helper the fixture map writer
            // used, so identity is unchanged from the JSON era.
            sceneIds[i] = GlobalObjectId.GetGlobalObjectIdSlow(def.gameObject).ToString();
            fixtures[i] = def.gameObject;

            // The object graph and derived values are read straight off
            // DiamondFixtureDefinition (which sources them from the profile and
            // beam material). No DiamondFixtureDriver dependency: the driver is
            // retired, so the bake can't lean on it.
            lampProps[i]     = def.LampProps;
            beamProps[i]     = def.BeamProps;
            heads[i]         = def.Head;
            headRenderers[i] = def.HeadRenderer;
            beamRenderers[i] = def.BeamRenderer;
            symmetricBeam[i] = def.SymmetricBeam;

            if (def.HeadRenderer == null || def.LampProps == null)
            {
                Debug.LogWarning($"[Diamond] Fixture '{def.gameObject.name}' is missing its HeadRenderer or LampProps reference on DiamondFixtureDefinition; it won't light.", def);
                missing++;
            }

            // --- Map (presentation) data. Same derivation the old
            // DiamondFixtureMapWriter did, now folded into this one crawl. ---
            var profile = def.Profile;
            bool isRound = profile != null && profile.Shape == DiamondFixtureProfile.BeamShape.Round;

            mapNames[i] = string.IsNullOrEmpty(def.DisplayName) ? def.gameObject.name : def.DisplayName;

            // XZ world position, centred on the rig bounding box (world +Z -> map +Y).
            Vector3 fpos = def.transform.position;
            mapPositions[i] = new Vector2(fpos.x - centreX, fpos.z - centreZ);

            // Node footprint: width = long axis (X), depth = short axis (Z). Round
            // fixtures use FixtureWidth as the emitter diameter and leave
            // FixtureHeight unused, so emit a square rather than a zero-depth node.
            float nodeW = profile != null ? profile.FixtureWidth : 0f;
            float nodeD = isRound ? nodeW : (profile != null ? profile.FixtureHeight : 0f);
            mapSizes[i] = new Vector2(nodeW, nodeD);

            mapYaw[i]   = ComputeMapYaw(def.transform);
            mapRound[i] = isRound;

            // Record the static emitter size so the manager can re-push it at
            // runtime. It can't be baked onto the property block here: a
            // MaterialPropertyBlock is instance state, not serialized on the
            // renderer, so it doesn't survive entering play mode. The manager
            // re-applies it in Start() from this array instead.
            fixtureEmitterSizes[i] = def.FixtureEmitterSize;

            // Bake the beam's worst-case culling bounds once, here, so the runtime
            // loop never touches bounds. Unlike the property block, Renderer.bounds
            // IS serialized, so this one does persist into play. Definition computes
            // it from the profile/material directly; safe in edit mode. The manager
            // ceilings size the AABB for animated haze/scatter (else -1 = material).
            def.ComputeBeamBounds(hazeCeiling, scatterCeiling);
        }

        // Capture groups by identity. Members are stored as GlobalObjectId strings
        // (not indices) so a later re-bake that reorders fixtures can't misassign
        // them; the map window resolves identity -> current index at read time.
        var bakedGroups = BakeGroups();

        // Assign the runtime arrays through SerializedObject so they persist and
        // the manager scene object is marked dirty for saving.
        var so = new SerializedObject(manager);
        AssignStringArray(so, "SceneIds", sceneIds);
        AssignArray(so, "Fixtures",  fixtures);
        AssignVector2Array(so, "FixtureEmitterSizes", fixtureEmitterSizes);
        AssignArray(so, "LampProps",     lampProps);
        AssignArray(so, "BeamProps",     beamProps);
        AssignArray(so, "Heads",         heads);
        AssignArray(so, "HeadRenderers", headRenderers);
        AssignArray(so, "BeamRenderers", beamRenderers);
        AssignBoolArray(so, "SymmetricBeam", symmetricBeam);
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(manager);

        // Assign the map data onto this Definition. Direct field writes with an
        // explicit SetDirty + undo record, since these fields live here (not on
        // the Udon manager) and don't need the string-keyed SerializedObject path.
        Undo.RecordObject(this, "Bake fixtures");
        MapNames     = mapNames;
        MapPositions = mapPositions;
        MapSizes     = mapSizes;
        MapYaw       = mapYaw;
        MapRound     = mapRound;
        Groups       = bakedGroups;
        EditorUtility.SetDirty(this);

        Debug.Log($"[Diamond] Baked {count} fixture(s) and {bakedGroups.Count} group(s) into '{name}'" +
                  (missing > 0 ? $" ({missing} missing a HeadRenderer/LampProps reference; those won't light)" : "") + ".", this);
    }

    // Crawls DiamondFixtureGroupDefinition components under the root and records
    // each group's members by stable identity (GlobalObjectId). Mirrors the old
    // DiamondFixtureMapWriter group crawl, but stores GIDs rather than indices so
    // the result survives a re-bake that reorders fixtures.
    private List<BakedGroup> BakeGroups()
    {
        var result = new List<BakedGroup>();
        var groupDefs = GetComponentsInChildren<DiamondFixtureGroupDefinition>();
        foreach (var g in groupDefs)
        {
            var memberIds = new List<string>();
            foreach (var fd in g.GetComponentsInChildren<DiamondFixtureDefinition>())
                memberIds.Add(GlobalObjectId.GetGlobalObjectIdSlow(fd.gameObject).ToString());

            result.Add(new BakedGroup
            {
                name      = string.IsNullOrEmpty(g.DisplayName) ? g.gameObject.name : g.DisplayName,
                sceneId   = GlobalObjectId.GetGlobalObjectIdSlow(g.gameObject).ToString(),
                memberIds = memberIds,
            });
        }
        return result;
    }

    // Yaw of the fixture about world Y, in degrees, as the map renderer uses it.
    // Copied from the retiring DiamondFixtureMapWriter.GetMapYaw so the folded
    // bake produces identical yaw values. The map draws the footprint in the XZ
    // plane with the node's width along map +X (the fixture's local +X); this
    // measures how that axis is oriented in world XZ. Using the axis projection
    // (not eulerAngles.y) keeps the value stable when the fixture is also tilted
    // or rolled. Clockwise from map +X (world +Z -> map +Y). Unrotated returns 0.
    private static float ComputeMapYaw(Transform t)
    {
        Vector3 right = t.right;                        // fixture local +X in world space
        Vector2 inPlane = new Vector2(right.x, right.z);
        if (inPlane.sqrMagnitude < 1e-8f) return 0f;    // width axis near-vertical; no meaningful yaw
        return Mathf.Atan2(inPlane.y, inPlane.x) * Mathf.Rad2Deg;
    }

    // Writes an Object-reference array onto a serialized property.
    private static void AssignArray(SerializedObject so, string prop, Object[] values)
    {
        var p = so.FindProperty(prop);
        p.arraySize = values.Length;
        for (int i = 0; i < values.Length; i++)
            p.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
    }

    private static void AssignBoolArray(SerializedObject so, string prop, bool[] values)
    {
        var p = so.FindProperty(prop);
        p.arraySize = values.Length;
        for (int i = 0; i < values.Length; i++)
            p.GetArrayElementAtIndex(i).boolValue = values[i];
    }

    private static void AssignStringArray(SerializedObject so, string prop, string[] values)
    {
        var p = so.FindProperty(prop);
        p.arraySize = values.Length;
        for (int i = 0; i < values.Length; i++)
            p.GetArrayElementAtIndex(i).stringValue = values[i];
    }

    private static void AssignVector2Array(SerializedObject so, string prop, Vector2[] values)
    {
        var p = so.FindProperty(prop);
        p.arraySize = values.Length;
        for (int i = 0; i < values.Length; i++)
            p.GetArrayElementAtIndex(i).vector2Value = values[i];
    }
#endif
}
