using UnityEngine;
using UnityEditor;

// Right-click a .alv.json file in the Project window and choose
// "Moment ALV/Migrate sidecar: initialise renderSeconds" to patch legacy sidecars
// that predate the renderSeconds field.
//
// Every snapshot entry with renderSeconds == 0 is set to -1 (the "not recorded" sentinel),
// so downstream diagnostics can tell the difference between a genuine zero-second bake
// and an entry that was written before timing was tracked.
public static class MomentMigrateSidecar
{
    const string MenuPath = "Assets/Moment ALV/Migrate sidecar: initialise renderSeconds";

    // Uncomment to add this back to the asset menu right-click menu.
    // [MenuItem(MenuPath, validate = true)]
    static bool Validate()
    {
        string path = AssetDatabase.GetAssetPath(Selection.activeObject);
        return path != null && path.EndsWith(".alv.json");
    }

    [MenuItem(MenuPath)]
    static void Run()
    {
        string assetPath = AssetDatabase.GetAssetPath(Selection.activeObject);
        // MomentTextureInfo.Load expects a .asset path and derives the .alv.json path from it.
        // Since we have the .alv.json directly, strip the suffix and restore the .asset extension.
        string texAssetPath = assetPath.Replace(".alv.json", ".asset");

        MomentTextureInfo info = MomentTextureInfo.Load(texAssetPath);
        if (info == null)
        {
            Debug.LogError($"[Moment] Could not load sidecar at {assetPath}.");
            return;
        }

        if (info.snapshots == null || info.snapshots.Length == 0)
        {
            Debug.Log($"[Moment] {assetPath}: no snapshots to migrate.");
            return;
        }

        int patched = 0;
        for (int i = 0; i < info.snapshots.Length; i++)
        {
            if (info.snapshots[i].renderSeconds == 0f)
            {
                info.snapshots[i] = new MomentTextureInfo.SnapshotEntry
                {
                    baked         = info.snapshots[i].baked,
                    animFrame     = info.snapshots[i].animFrame,
                    renderSeconds = -1f,
                };
                patched++;
            }
        }

        if (patched == 0)
        {
            Debug.Log($"[Moment] {assetPath}: all entries already have renderSeconds set; nothing to do.");
            return;
        }

        info.Save(texAssetPath);
        Debug.Log($"[Moment] Migrated {patched} snapshot{(patched == 1 ? "" : "s")} in {assetPath}.");
    }
}
