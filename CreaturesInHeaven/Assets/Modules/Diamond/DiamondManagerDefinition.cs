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

        int missing = 0;
        for (int i = 0; i < count; i++)
        {
            var def = defs[i];

            // Record the fixture's stable scene identity, index-aligned with the
            // reference arrays. Record-only for now: nothing reads it until stage
            // 3 builds the identity -> index map. Same GlobalObjectId helper the
            // fixture map writer uses, so the two agree on identity.
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

            // Record the static emitter size so the manager can re-push it at
            // runtime. It can't be baked onto the property block here: a
            // MaterialPropertyBlock is instance state, not serialized on the
            // renderer, so it doesn't survive entering play mode. The manager
            // re-applies it in Start() from this array instead.
            fixtureEmitterSizes[i] = def.FixtureEmitterSize;

            // Bake the beam's worst-case culling bounds once, here, so the runtime
            // loop never touches bounds. Unlike the property block, Renderer.bounds
            // IS serialized, so this one does persist into play. Definition computes
            // it from the profile/material directly; safe in edit mode.
            def.ComputeBeamBounds();
        }

        // Assign through SerializedObject so the arrays persist and the scene is
        // marked dirty for saving.
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

        Debug.Log($"[Diamond] Baked {count} fixture(s) into '{name}'" +
                  (missing > 0 ? $" ({missing} missing a HeadRenderer/LampProps reference; those won't light)" : "") + ".", this);
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
