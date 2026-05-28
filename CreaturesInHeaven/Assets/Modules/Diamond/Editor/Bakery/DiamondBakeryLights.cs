#if BAKERY_INCLUDED
using UnityEditor;
using UnityEngine;

// Menu actions to add or remove Bakery lights on selected Diamond fixtures.
// Operates only on GameObjects that have a DiamondFixtureDefinition.
// All Bakery-specific code is confined to this file and DiamondBakeryDriver.
public static class DiamondBakeryLights
{
    const string AddMenu    = "Tools/Diamond/Bakery: Add lights to selected";
    const string RemoveMenu = "Tools/Diamond/Bakery: Remove lights from selected";

    // --- Add -------------------------------------------------------------

    [MenuItem(AddMenu)]
    static void AddBakeryLights()
    {
        int added   = 0;
        int skipped = 0;

        Undo.SetCurrentGroupName("Add Bakery lights");
        int group = Undo.GetCurrentGroup();

        foreach (var go in Selection.gameObjects)
        {
            var def = go.GetComponent<DiamondFixtureDefinition>();
            if (def == null) continue;

            // Skip if already set up.
            if (go.GetComponent<DiamondBakeryDriver>() != null)
            {
                Debug.LogWarning($"[Diamond] {go.name}: already has DiamondBakeryDriver. Skipped.", go);
                skipped++;
                continue;
            }

            var profile = def.Profile;
            if (profile == null)
            {
                Debug.LogWarning($"[Diamond] {go.name}: no profile assigned. Skipped.", go);
                skipped++;
                continue;
            }

            var driver = go.GetComponent<DiamondFixtureDriver>();
            if (driver == null || driver.Head == null)
            {
                Debug.LogWarning($"[Diamond] {go.name}: FixtureDriver or Head not found. Skipped.", go);
                skipped++;
                continue;
            }

            // Create the light child under Head.
            var lightGO = new GameObject("Bakery light");
            Undo.RegisterCreatedObjectUndo(lightGO, "Add Bakery lights");
            lightGO.transform.SetParent(driver.Head, false);
            lightGO.transform.localPosition = profile.BakeryLightOffset;

            // Add the appropriate Bakery light component.
            Component bakeryLight = AddBakeryLight(lightGO, profile);

            // Add the driver sibling on the fixture root.
            var bakeryDriver = Undo.AddComponent<DiamondBakeryDriver>(go);
            bakeryDriver.Light           = bakeryLight;
            bakeryDriver.LampProps       = driver.LampProps;
            bakeryDriver.BrightnessScale = profile.BakeryBrightnessScale;

            Undo.CollapseUndoOperations(group);
            added++;
        }

        if (added > 0 || skipped > 0)
            Debug.Log($"[Diamond] Add Bakery lights: {added} added, {skipped} skipped.");
    }

    [MenuItem(AddMenu, true)]
    static bool AddBakeryLightsValidate() => AnyFixtureSelected();

    // Adds the Bakery light component(s) appropriate for the profile type.
    // Returns the primary Bakery light component (the one DiamondBakeryDriver drives).
    static Component AddBakeryLight(GameObject go, DiamondFixtureProfile profile)
    {
        switch (profile.BakeryLightType)
        {
            case DiamondBakeryLightType.Point:
            case DiamondBakeryLightType.Spot:
            {
                return go.AddComponent<BakeryPointLight>();
            }

            case DiamondBakeryLightType.Mesh:
            {
                // Bakery area lights need a real mesh to sample from.
                var filter   = go.AddComponent<MeshFilter>();
                var renderer = go.AddComponent<MeshRenderer>();
                filter.sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                go.transform.localScale = profile.BakeryMeshLightSize;

                // Required settings for Bakery to treat this mesh as an area light source.
                renderer.shadowCastingMode    = UnityEngine.Rendering.ShadowCastingMode.On;
                renderer.receiveGI            = ReceiveGI.LightProbes;  // prevents Bakery baking a lightmap onto the light mesh
                renderer.lightProbeUsage      = UnityEngine.Rendering.LightProbeUsage.Off;
                renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ContributeGI);

                return go.AddComponent<BakeryLightMesh>();
            }

            default:
                Debug.LogWarning($"[Diamond] Unknown BakeryLightType {profile.BakeryLightType}. Adding point light as fallback.");
                return go.AddComponent<BakeryPointLight>();
        }
    }

    // --- Remove ----------------------------------------------------------

    [MenuItem(RemoveMenu)]
    static void RemoveBakeryLights()
    {
        int removed = 0;
        int skipped = 0;

        Undo.SetCurrentGroupName("Remove Bakery lights");
        int group = Undo.GetCurrentGroup();

        foreach (var go in Selection.gameObjects)
        {
            var def = go.GetComponent<DiamondFixtureDefinition>();
            if (def == null) continue;

            var bakeryDriver = go.GetComponent<DiamondBakeryDriver>();
            if (bakeryDriver == null)
            {
                skipped++;
                continue;
            }

            // Destroy the light GameObject via the reference stored on the driver.
            if (bakeryDriver.Light != null)
                Undo.DestroyObjectImmediate(bakeryDriver.Light.gameObject);

            Undo.DestroyObjectImmediate(bakeryDriver);
            Undo.CollapseUndoOperations(group);
            removed++;
        }

        if (removed > 0 || skipped > 0)
            Debug.Log($"[Diamond] Remove Bakery lights: {removed} removed, {skipped} skipped.");
    }

    [MenuItem(RemoveMenu, true)]
    static bool RemoveBakeryLightsValidate() => AnyFixtureSelected();

    // --- Helpers ---------------------------------------------------------

    static bool AnyFixtureSelected()
    {
        foreach (var go in Selection.gameObjects)
            if (go.GetComponent<DiamondFixtureDefinition>() != null) return true;
        return false;
    }
}
#endif
