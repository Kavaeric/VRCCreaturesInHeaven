#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class LUTBeamMaterialManager
{
    [MenuItem("Assets/LUTBeam - Generate LUTs")]
    static void GenerateLUTs()
    {
        var textures = Selection.GetFiltered<Texture2D>(SelectionMode.Assets);

        Texture2D[] goboTextures = new Texture2D[textures.Length];
        Texture2D[] lutTextures = new Texture2D[textures.Length];

        string dir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(textures[0]));

        for (int q = 0; q < textures.Length; q++)
        {
            if (!(textures[q] is Texture2D))
                continue;

            Texture2D Gobo = textures[q] as Texture2D;

            if (!Gobo)
                return;

            Texture2D tex = Compute(Gobo, 1024, 1024);

            goboTextures[q] = Gobo;
            lutTextures[q] = tex;
        }

        Texture2DArray texArray = new Texture2DArray(1024, 1024, textures.Length, TextureFormat.BC4, false, false);
        Texture2DArray lutArray = new Texture2DArray(1024, 1024, textures.Length, TextureFormat.BC4, false, false);

        for (int i = 0; i < textures.Length; i++)
        {
            RenderTexture goboTempRT = new RenderTexture(1024, 1024, 0, RenderTextureFormat.ARGBFloat);
            Texture2D goboTex = new Texture2D(1024, 1024, TextureFormat.RGBA32, false, false);
            Graphics.Blit(goboTextures[i], goboTempRT);
            goboTex.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0);
            goboTex.Apply();
            EditorUtility.CompressTexture(goboTex, TextureFormat.BC4, TextureCompressionQuality.Best);
            Graphics.CopyTexture(goboTex, 0, texArray, i);

            RenderTexture lutTempRT = new RenderTexture(1024, 1024, 0, RenderTextureFormat.ARGBFloat);
            Texture2D lutTex = new Texture2D(1024, 1024, TextureFormat.RGBA32, false, false);
            Graphics.Blit(lutTextures[i], lutTempRT);
            lutTex.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0);
            lutTex.Apply();
            EditorUtility.CompressTexture(lutTex, TextureFormat.BC4, TextureCompressionQuality.Best);
            Graphics.CopyTexture(lutTex, 0, lutArray, i);
        }

        Scene scene = SceneManager.GetActiveScene();

        if (!Directory.Exists($"{dir}/Generated/"))
            Directory.CreateDirectory($"{dir}/Generated/");

        string texArrayFilename = $"{dir}/Generated/{scene.name} - Gobo Textures.asset";
        string lutArrayFilename = $"{dir}/Generated/{scene.name} - LUT Textures.asset";

        AssetDatabase.CreateAsset(texArray, texArrayFilename);
        AssetDatabase.CreateAsset(lutArray, lutArrayFilename);
        AssetDatabase.SaveAssets();

        texArray = (Texture2DArray)AssetDatabase.LoadAssetAtPath(texArrayFilename, typeof(Texture2DArray));
        lutArray = (Texture2DArray)AssetDatabase.LoadAssetAtPath(lutArrayFilename, typeof(Texture2DArray));

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    [MenuItem("Assets/LUTBeam - Generate LUTs", validate = true)]
    static bool ValidateGenerateLUTs()
    {
        return Selection.GetFiltered<Texture2D>(SelectionMode.Assets).Length > 0;
    }

    static Texture2D Compute(Texture Gobo, int sizeX, int sizeY)
    {
        // Path to the LUT Generator CRT.
        var path = AssetDatabase.GUIDToAssetPath("b959e6e2642a26549a398a4a7995e953");

        // make a copy so the thing in the project doesn't get edited!
        Material material = new Material(AssetDatabase.LoadAssetAtPath<Shader>(path));

        RenderTexture tempRT = new RenderTexture(sizeX, sizeY, 32, RenderTextureFormat.ARGBFloat);

        material.SetTexture("_MainTex", Gobo);
        
        Graphics.Blit(null, tempRT, material);

        RenderTexture.active = tempRT;
        Texture2D tex = new Texture2D(sizeX, sizeY, TextureFormat.RGBAFloat, false);
        tex.ReadPixels(new Rect(0, 0, sizeX, sizeY), 0, 0);
        tex.Apply();
        RenderTexture.active = null;

        //convert linear to srgb in the texture
        for (int x = 0; x < sizeX; x++)
        {
            for (int y = 0; y < sizeY; y++)
            {
                Color c = tex.GetPixel(x, y);
                c.r = Mathf.LinearToGammaSpace(c.r);
                c.g = Mathf.LinearToGammaSpace(c.g);
                c.b = Mathf.LinearToGammaSpace(c.b);
                tex.SetPixel(x, y, c);
            }
        }
        tex.Apply();

        return tex;
    }
}
#endif
