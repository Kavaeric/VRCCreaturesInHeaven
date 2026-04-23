using System.IO;
using UnityEditor;
using UnityEngine;

public static class RenderTextureExport
{
    [MenuItem("Assets/Export RenderTexture as PNG")]
    static void Export()
    {
        RenderTexture rt = Selection.activeObject as RenderTexture;

        string path = AssetDatabase.GetAssetPath(rt);
        path = Path.ChangeExtension(path, ".png");

        // Warn before overwriting an existing file.
        if (File.Exists(path))
        {
            bool overwrite = EditorUtility.DisplayDialog(
                "Overwrite existing file?",
                $"{path} already exists. Overwrite it?",
                "Overwrite", "Cancel");

            if (!overwrite)
            {
                return;
            }
        }

        // Blit into an sRGB RenderTexture so the linear-to-sRGB conversion happens before the read.
        RenderTexture srgbRT = RenderTexture.GetTemporary(rt.width, rt.height, 0, rt.format, RenderTextureReadWrite.sRGB);
        Graphics.Blit(rt, srgbRT);

        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = srgbRT;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(srgbRT);

        File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
        Object.DestroyImmediate(tex);

        AssetDatabase.Refresh();

        // Auto-set some default texture importer settings.
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            // Enable alpha transparency by default for imposter images.
            importer.alphaIsTransparency = true;

            // VRChat insists on Kaiser mipmap filtering, and I concur.
            importer.mipmapFilter = TextureImporterMipFilter.KaiserFilter;

            // Save and reimport the texture.
            importer.SaveAndReimport();
        }

        Debug.Log($"    [RenderTextureExport] Saved to {path}");
    }

    [MenuItem("Assets/Export RenderTexture as PNG", true)]
    static bool ExportValidate() => Selection.activeObject is RenderTexture;
}
