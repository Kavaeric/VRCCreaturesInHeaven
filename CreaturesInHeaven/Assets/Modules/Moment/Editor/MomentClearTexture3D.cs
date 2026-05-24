using UnityEngine;
using UnityEditor;

// Zeroes all pixels in a selected Texture3D asset, keeping its dimensions and GUID intact.
// Useful for clearing the VRCLightVolumes atlas between bake sessions after an ALV bake.
//
// For uncompressed formats (e.g. RGBAFloat, RGBAHalf), SetPixels is used directly.
// For compressed formats (e.g. ASTC_4x4), a blank texture is constructed and its
// serialized data is copied over the existing asset via EditorUtility.CopySerialized,
// which preserves the GUID and all scene references.
public static class MomentClearTexture3D
{
    const string MenuPath = "Tools/Moment ALV/Clear selected Texture3D";

    [MenuItem(MenuPath)]
    static void ClearSelected()
    {
        var targets = SelectedTargets();

        var lines = new System.Text.StringBuilder();
        foreach (var tex in targets)
            lines.AppendLine($"  • {tex.name}  ({tex.width}×{tex.height}×{tex.depth}, {tex.format})");

        bool confirmed = EditorUtility.DisplayDialog(
            "Clear Texture3D",
            $"Zero all pixels in {targets.Count} texture{(targets.Count == 1 ? "" : "s")}?\n\n{lines}",
            "Clear", "Cancel");

        if (!confirmed) return;

        foreach (var tex in targets)
        {
            ClearTexture(tex);
            Debug.Log($"[Moment] Cleared {AssetDatabase.GetAssetPath(tex)} ({tex.width}x{tex.height}x{tex.depth}, {tex.format})");
        }

        AssetDatabase.SaveAssets();
    }

    static void ClearTexture(Texture3D tex)
    {
        if (!IsCompressed(tex.format))
        {
            // Uncompressed: zero pixels in place.
            tex.SetPixels(new Color[tex.width * tex.height * tex.depth]);
            tex.Apply();
            EditorUtility.SetDirty(tex);
        }
        else
        {
            // Compressed: build a blank texture with the same spec and copy its serialised
            // data over the existing asset. The existing object keeps its instance ID and
            // GUID, so all scene references remain valid.
            var blank = new Texture3D(tex.width, tex.height, tex.depth, tex.format, tex.mipmapCount > 1)
            {
                wrapMode   = tex.wrapMode,
                filterMode = tex.filterMode,
            };
            blank.Apply();

            EditorUtility.CopySerialized(blank, tex);
            EditorUtility.SetDirty(tex);

            Object.DestroyImmediate(blank);
        }
    }

    [MenuItem(MenuPath, validate = true)]
    static bool ValidateClearSelected() => SelectedTargets().Count > 0;

    static System.Collections.Generic.List<Texture3D> SelectedTargets()
    {
        var list = new System.Collections.Generic.List<Texture3D>();
        foreach (var obj in Selection.objects)
            if (obj is Texture3D t)
                list.Add(t);
        return list;
    }

    static bool IsCompressed(TextureFormat fmt) => fmt switch
    {
        TextureFormat.DXT1        or TextureFormat.DXT5        or
        TextureFormat.BC4         or TextureFormat.BC5         or
        TextureFormat.BC6H        or TextureFormat.BC7         or
        TextureFormat.ASTC_4x4   or TextureFormat.ASTC_5x5   or
        TextureFormat.ASTC_6x6   or TextureFormat.ASTC_8x8   or
        TextureFormat.ASTC_10x10 or TextureFormat.ASTC_12x12 or
        TextureFormat.ETC_RGB4   or TextureFormat.ETC2_RGB    or
        TextureFormat.ETC2_RGBA1 or TextureFormat.ETC2_RGBA8  or
        TextureFormat.EAC_R      or TextureFormat.EAC_R_SIGNED or
        TextureFormat.EAC_RG     or TextureFormat.EAC_RG_SIGNED or
        TextureFormat.PVRTC_RGB2 or TextureFormat.PVRTC_RGBA2 or
        TextureFormat.PVRTC_RGB4 or TextureFormat.PVRTC_RGBA4 => true,
        _ => false,
    };
}
