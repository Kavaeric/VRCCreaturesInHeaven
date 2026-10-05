using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEditor;

namespace Mochie {
    public static class TexturePacker {

        public static Texture2D PackTextures(Material mat, MaterialProperty redProp, MaterialProperty greenProp, MaterialProperty blueProp, MaterialProperty alphaProp, MaterialProperty packedMapProp){
            return PackTextures(mat, redProp, 1f, greenProp, 1f, blueProp, 1f, alphaProp, 1f, packedMapProp);
        }

        public static Texture2D PackTextures(
            Material mat, 
            MaterialProperty redProp, MaterialProperty redStrengthProp,
            MaterialProperty greenProp, MaterialProperty greenStrengthProp,
            MaterialProperty blueProp, MaterialProperty blueStrengthProp,
            MaterialProperty alphaProp, MaterialProperty alphaStrengthProp,
            MaterialProperty packedMapProp
        ){
            float redStrength = redStrengthProp != null ? redStrengthProp.floatValue : 1f;
            float greenStrength = greenStrengthProp != null ? greenStrengthProp.floatValue : 1f;
            float blueStrength = blueStrengthProp != null ? blueStrengthProp.floatValue : 1f;
            float alphaStrength = alphaStrengthProp != null ? alphaStrengthProp.floatValue : 1f;

            return PackTextures(
                mat, 
                redProp, redStrength, 
                greenProp, greenStrength, 
                blueProp, blueStrength, 
                alphaProp, alphaStrength, 
                packedMapProp
            );
        }

        public static Texture2D PackTextures(
            Material mat, 
            MaterialProperty redProp, float redStrength,
            MaterialProperty greenProp, float greenStrength,
            MaterialProperty blueProp, float blueStrength,
            MaterialProperty alphaProp, float alphaStrength,
            MaterialProperty packedMapProp
        ){
            Texture2D redTex = redProp != null ? redProp.textureValue as Texture2D : null;
            Texture2D greenTex = greenProp != null ? greenProp.textureValue as Texture2D : null;
            Texture2D blueTex = blueProp != null ? blueProp.textureValue as Texture2D : null;
            Texture2D alphaTex = alphaProp != null ? alphaProp.textureValue as Texture2D : null;

            string redName = redProp != null ? redProp.name : "";
            string greenName = greenProp != null ? greenProp.name : "";
            string blueName = blueProp != null ? blueProp.name : "";
            string alphaName = alphaProp != null ? alphaProp.name : "";

            Vector4 redST = GetTextureScaleAndOffset(mat, redProp, redName);
            Vector4 greenST = GetTextureScaleAndOffset(mat, greenProp, greenName);
            Vector4 blueST = GetTextureScaleAndOffset(mat, blueProp, blueName);
            Vector4 alphaST = GetTextureScaleAndOffset(mat, alphaProp, alphaName);

            Texture2D result = PackTextures(
                mat, 
                redTex, redStrength, redST,
                greenTex, greenStrength, greenST,
                blueTex, blueStrength, blueST,
                alphaTex, alphaStrength, alphaST,
                redName, greenName, blueName, alphaName
            );

            if (result != null && mat != null && packedMapProp != null){
                packedMapProp.textureValue = result;
                mat.SetTexture(packedMapProp.name, result);
                mat.SetTextureScale(packedMapProp.name, Vector2.one);
                mat.SetTextureOffset(packedMapProp.name, Vector2.zero);
            }

            return result;
        }

        public static Texture2D PackTextures(
            Material mat, 
            Texture2D redTex, float redStrength,
            Texture2D greenTex, float greenStrength,
            Texture2D blueTex, float blueStrength,
            Texture2D alphaTex, float alphaStrength,
            string redPropName, string greenPropName, string bluePropName, string alphaPropName
        ){
            Vector4 redST = GetTextureScaleAndOffset(mat, null, redPropName);
            Vector4 greenST = GetTextureScaleAndOffset(mat, null, greenPropName);
            Vector4 blueST = GetTextureScaleAndOffset(mat, null, bluePropName);
            Vector4 alphaST = GetTextureScaleAndOffset(mat, null, alphaPropName);
            return PackTextures(
                mat, 
                redTex, redStrength, redST,
                greenTex, greenStrength, greenST,
                blueTex, blueStrength, blueST,
                alphaTex, alphaStrength, alphaST,
                redPropName, greenPropName, bluePropName, alphaPropName
            );
        }

        public static Texture2D PackTextures(
            Material mat, 
            Texture2D redTex, float redStrength, Vector4 redST,
            Texture2D greenTex, float greenStrength, Vector4 greenST,
            Texture2D blueTex, float blueStrength, Vector4 blueST,
            Texture2D alphaTex, float alphaStrength, Vector4 alphaST,
            string redPropName, string greenPropName, string bluePropName, string alphaPropName
        ){
            if (mat == null)
                return null;

            // Ensure at least one texture slot is populated
            bool hasAnyTexture = redTex != null || greenTex != null || blueTex != null || alphaTex != null;
            if (!hasAnyTexture)
                return null;

            bool hasAlpha = alphaTex != null;

            bool createdRed = false;
            bool createdGreen = false;
            bool createdBlue = false;
            bool createdAlpha = false;

            bool isRedOcclusion = string.IsNullOrEmpty(redPropName) || redPropName.ToLowerInvariant().Contains("occlusion") || redPropName.ToLowerInvariant().Contains("ao");
            bool isAlphaHeight = !string.IsNullOrEmpty(alphaPropName) && (alphaPropName.ToLowerInvariant().Contains("height") || alphaPropName.ToLowerInvariant().Contains("parallax"));

            Texture2D redSource = GetSourceTexture(redTex, mat, redPropName, 1f, out createdRed);
            Texture2D greenSource = GetSourceTexture(greenTex, mat, greenPropName, 1f, out createdGreen);
            Texture2D blueSource = GetSourceTexture(blueTex, mat, bluePropName, 1f, out createdBlue);
            Texture2D alphaSource = hasAlpha ? GetSourceTexture(alphaTex, mat, alphaPropName, isAlphaHeight ? 0f : 1f, out createdAlpha) : null;

            Vector2Int res = GetMaxSizeFromTextures(redSource, greenSource, blueSource, alphaSource);
            if (res.x <= 1 || res.y <= 1)
                res = new Vector2Int(1024, 1024);

            Texture2D packedResult = PackTexturesInternal(
                res, 
                redSource, redST, redStrength, isRedOcclusion,
                greenSource, greenST, greenStrength,
                blueSource, blueST, blueStrength,
                alphaSource, alphaST, alphaStrength,
                hasAlpha, false, false, false, false
            );

            if (createdRed && redSource) UnityEngine.Object.DestroyImmediate(redSource);
            if (createdGreen && greenSource) UnityEngine.Object.DestroyImmediate(greenSource);
            if (createdBlue && blueSource) UnityEngine.Object.DestroyImmediate(blueSource);
            if (createdAlpha && alphaSource) UnityEngine.Object.DestroyImmediate(alphaSource);

            if (packedResult != null){
                string savePath = GetSavePath(mat);
                SaveTextureAsset(packedResult, savePath, true);

                TextureImporter importer = AssetImporter.GetAtPath(savePath) as TextureImporter;
                if (importer != null){
                    importer.sRGBTexture = false;
                    importer.SaveAndReimport();
                }

                Texture2D savedTex = AssetDatabase.LoadAssetAtPath<Texture2D>(savePath);
                return savedTex;
            }

            return null;
        }

        public static Texture2D PackTexturesInternal(Vector2Int resolution, Texture2D red, Texture2D green, Texture2D blue, Texture2D alpha, bool hasAlpha, bool invertRed, bool invertGreen, bool invertBlue, bool invertAlpha){
            return PackTexturesInternal(resolution, red, new Vector4(1,1,0,0), 1f, true, green, new Vector4(1,1,0,0), 1f, blue, new Vector4(1,1,0,0), 1f, alpha, new Vector4(1,1,0,0), 1f, hasAlpha, invertRed, invertGreen, invertBlue, invertAlpha);
        }

        public static Texture2D PackTexturesInternal(
            Vector2Int resolution, 
            Texture2D red, Vector4 redST, float redStrength, bool isRedOcclusion,
            Texture2D green, Vector4 greenST, float greenStrength,
            Texture2D blue, Vector4 blueST, float blueStrength,
            Texture2D alpha, Vector4 alphaST, float alphaStrength,
            bool hasAlpha, bool invertRed, bool invertGreen, bool invertBlue, bool invertAlpha
        ){
            Shader packerShader = Shader.Find("Hidden/Mochie/TexturePacker");
            if (packerShader == null)
                return null;

            Material mat = new Material(packerShader);
            mat.SetTexture("_Red", red);
            mat.SetVector("_Red_ST", redST);
            mat.SetTextureScale("_Red", new Vector2(redST.x, redST.y));
            mat.SetTextureOffset("_Red", new Vector2(redST.z, redST.w));
            mat.SetFloat("_Strength_Red", redStrength);
            mat.SetFloat("_IsOcclusion_Red", isRedOcclusion ? 1f : 0f);

            mat.SetTexture("_Green", green);
            mat.SetVector("_Green_ST", greenST);
            mat.SetTextureScale("_Green", new Vector2(greenST.x, greenST.y));
            mat.SetTextureOffset("_Green", new Vector2(greenST.z, greenST.w));
            mat.SetFloat("_Strength_Green", greenStrength);

            mat.SetTexture("_Blue", blue);
            mat.SetVector("_Blue_ST", blueST);
            mat.SetTextureScale("_Blue", new Vector2(blueST.x, blueST.y));
            mat.SetTextureOffset("_Blue", new Vector2(blueST.z, blueST.w));
            mat.SetFloat("_Strength_Blue", blueStrength);

            mat.SetTexture("_Alpha", alpha);
            mat.SetVector("_Alpha_ST", alphaST);
            mat.SetTextureScale("_Alpha", new Vector2(alphaST.x, alphaST.y));
            mat.SetTextureOffset("_Alpha", new Vector2(alphaST.z, alphaST.w));
            mat.SetFloat("_Strength_Alpha", alphaStrength);

            mat.SetInt("_Invert_Red", Convert.ToByte(invertRed));
            mat.SetInt("_Invert_Green", Convert.ToByte(invertGreen));
            mat.SetInt("_Invert_Blue", Convert.ToByte(invertBlue));
            mat.SetInt("_Invert_Alpha", Convert.ToByte(invertAlpha));

            TextureFormat format = hasAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24;
            Texture2D tex = new Texture2D(resolution.x, resolution.y, format, false, true);
            BakeMaterialToTexture(tex, mat);

            if (Application.isPlaying) UnityEngine.Object.Destroy(mat);
            else UnityEngine.Object.DestroyImmediate(mat);

            return tex;
        }

        public static Vector4 GetTextureScaleAndOffset(Material mat, MaterialProperty prop, string propName){
            if (prop != null){
                Vector4 st = prop.textureScaleAndOffset;
                if (st.x != 0f || st.y != 0f)
                    return st;
            }
            if (mat != null && !string.IsNullOrEmpty(propName) && mat.HasProperty(propName)){
                Vector2 scale = mat.GetTextureScale(propName);
                Vector2 offset = mat.GetTextureOffset(propName);
                if (scale.x != 0f || scale.y != 0f)
                    return new Vector4(scale.x, scale.y, offset.x, offset.y);
            }
            return new Vector4(1f, 1f, 0f, 0f);
        }

        public static void BakeMaterialToTexture(Texture2D tex, Material materialToBake){
            Vector2Int res = new Vector2Int(tex.width, tex.height);
            RenderTexture renderTexture = RenderTexture.GetTemporary(res.x, res.y, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(null, renderTexture, materialToBake);

            RenderTexture activeRT = RenderTexture.active;
            RenderTexture.active = renderTexture;
            tex.ReadPixels(new Rect(Vector2.zero, res), 0, 0);
            tex.Apply(false, false);

            RenderTexture.active = activeRT;
            RenderTexture.ReleaseTemporary(renderTexture);
        }

        public static Vector2Int GetMaxSizeFromTextures(params Texture2D[] textures){
            int maxW = 0;
            int maxH = 0;
            if (textures != null){
                foreach (var tex in textures){
                    if (tex != null){
                        if (tex.width > maxW) maxW = tex.width;
                        if (tex.height > maxH) maxH = tex.height;
                    }
                }
            }
            return new Vector2Int(maxW, maxH);
        }

        public static Texture2D GetSourceTexture(Texture2D tex, Material mat, string propName, float strength, out bool created){
            created = false;
            if (tex != null){
                string assetPath = AssetDatabase.GetAssetPath(tex);
                if (!string.IsNullOrEmpty(assetPath)){
                    string absolutePath = LocalAssetsPathToAbsolutePath(assetPath);
                    if (File.Exists(absolutePath)){
                        byte[] bytes = File.ReadAllBytes(absolutePath);
                        Texture2D sourceTex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                        if (ImageConversion.LoadImage(sourceTex, bytes)){
                            sourceTex.wrapMode = TextureWrapMode.Repeat;
                            created = true;
                            return sourceTex;
                        }
                        UnityEngine.Object.DestroyImmediate(sourceTex);
                    }
                }
                return tex;
            }

            // Unpopulated texture slot: generate a solid color texture from 0.0 (black) to 1.0 (white) based on strength
            created = true;
            float val = Mathf.Clamp01(strength);
            Texture2D solidTex = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            solidTex.SetPixel(0, 0, new Color(val, val, val, val));
            solidTex.wrapMode = TextureWrapMode.Repeat;
            solidTex.Apply();
            return solidTex;
        }

        public static Texture2D GetDefaultTextureForProperty(Material mat, string propName, out bool created){
            string defaultTexName = "white";
            if (mat != null && mat.shader != null && !string.IsNullOrEmpty(propName)){
                string shaderPath = AssetDatabase.GetAssetPath(mat.shader);
                if (!string.IsNullOrEmpty(shaderPath) && File.Exists(shaderPath)){
                    string shaderText = File.ReadAllText(shaderPath);
                    string pattern = propName + @"\s*\([^)]*\)\s*=\s*""([^""]*)""";
                    Match match = Regex.Match(shaderText, pattern);
                    if (match.Success){
                        defaultTexName = match.Groups[1].Value;
                    }
                }
            }
            return GetSolidColorTexture(defaultTexName, out created);
        }

        public static Texture2D GetSolidColorTexture(string defaultTexName, out bool created){
            created = false;
            if (string.IsNullOrEmpty(defaultTexName))
                return Texture2D.whiteTexture;

            string lower = defaultTexName.ToLowerInvariant();
            if (lower == "black")
                return Texture2D.blackTexture;
            if (lower == "white")
                return Texture2D.whiteTexture;
            if (lower == "gray" || lower == "grey")
                return Texture2D.grayTexture;
            if (lower == "red")
                return Texture2D.redTexture;

            created = true;
            Texture2D customTex = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            if (lower == "bump" || lower == "normal")
                customTex.SetPixel(0, 0, new Color(0.5f, 0.5f, 1f, 1f));
            else
                customTex.SetPixel(0, 0, Color.white);
            customTex.Apply();
            return customTex;
        }

        public static string GetSavePath(Material mat){
            string dir = "Assets";
            string baseName = mat.name + "_Packed";
            string matPath = AssetDatabase.GetAssetPath(mat);
            if (!string.IsNullOrEmpty(matPath)){
                dir = Path.GetDirectoryName(matPath);
                baseName = Path.GetFileNameWithoutExtension(matPath) + "_Packed";
            }
            dir = dir.Replace(@"\", "/");

            string candidate = $"{dir}/{baseName}.png";
            string absPath = LocalAssetsPathToAbsolutePath(candidate);
            if (!File.Exists(absPath) && !AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(candidate)){
                return candidate;
            }

            int counter = 1;
            while (true){
                candidate = $"{dir}/{baseName} {counter}.png";
                absPath = LocalAssetsPathToAbsolutePath(candidate);
                if (!File.Exists(absPath) && !AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(candidate)){
                    return candidate;
                }
                counter++;
            }
        }

        public static string LocalAssetsPathToAbsolutePath(string localPath){
            if (string.IsNullOrEmpty(localPath)) return localPath;
            localPath = localPath.Replace(@"\", "/");
            const string assets = "Assets/";
            if (localPath.StartsWith(assets, StringComparison.OrdinalIgnoreCase)){
                localPath = localPath.Substring(assets.Length);
                localPath = $"{Application.dataPath}/{localPath}";
            }
            return localPath;
        }

        public static string AbsolutePathToLocalAssetsPath(string path){
            if (string.IsNullOrEmpty(path)) return path;
            path = path.Replace(@"\", "/");
            if (path.StartsWith(Application.dataPath, StringComparison.OrdinalIgnoreCase))
                path = "Assets" + path.Substring(Application.dataPath.Length);
            return path;
        }

        public static void SaveTextureAsset(Texture2D tex, string assetPath, bool overwrite){
            byte[] bytes = tex.EncodeToPNG();

            if (!assetPath.StartsWith("Assets", StringComparison.OrdinalIgnoreCase)){
                Directory.CreateDirectory(Path.GetDirectoryName(assetPath));
                assetPath = AbsolutePathToLocalAssetsPath(assetPath);
            }
            else {
                string absolutePath = LocalAssetsPathToAbsolutePath(assetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
            }

            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) && !overwrite)
                assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);

            File.WriteAllBytes(assetPath, bytes);
            AssetDatabase.Refresh();
        }
    }
}