using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEditor;
using VRCLightVolumes;

// CRT and material asset provisioning for a MomentAnimatedLightVolume component.
public static class MomentCRTSetup
{
    // Creates or reconfigures the CRT and material assets for a MomentAnimatedLightVolume,
    // then registers the CRT with the scene's LightVolumeSetup post-processor chain.
    public static void SetupCRT(MomentAnimatedLightVolume alv)
    {
        // Mirror the Light Volumes package convention: store generated assets
        // alongside the scene they belong to, not next to the script.
        string assetDir = MomentAssetPaths.SceneAssetDir();
        MomentAssetPaths.CreateDirectory(assetDir);

        // Create material using the Moment shader.
        Shader shader = Shader.Find("Hidden/Moment/AnimatedLightVolume");
        if (shader == null)
        {
            Debug.LogError("[Moment] Could not find shader Hidden/Moment/AnimatedLightVolume.");
            return;
        }

        // Reuse existing material if already set up, otherwise create one.
        Material mat;
        if (alv.Crt != null && alv.Crt.material != null)
        {
            mat = alv.Crt.material;
        }
        else
        {
            mat = new Material(shader);
            string matPath = $"{assetDir}/{alv.gameObject.name}_ALVRenderTexture.mat";
            AssetDatabase.CreateAsset(mat, matPath);
        }

        // Reuse existing CRT if already set up, otherwise create one.
        CustomRenderTexture crt;
        if (alv.Crt != null)
        {
            crt = alv.Crt;
        }
        else
        {
            crt = new CustomRenderTexture(1, 1, RenderTextureFormat.ARGBHalf);
            string crtPath = $"{assetDir}/{alv.gameObject.name}_ALVRenderTexture.asset";
            AssetDatabase.CreateAsset(crt, crtPath);
        }

        // Configure the CRT. The LightVolumeSetup will enforce these too, but set
        // them here so the asset is in the right state before the scene is run.
        crt.dimension      = UnityEngine.Rendering.TextureDimension.Tex3D;
        crt.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
        crt.updateMode     = CustomRenderTextureUpdateMode.Realtime;
        crt.material       = mat;

        alv.Crt = crt;

        // Register with the LightVolumeSetup post-processor chain.
        LightVolumeSetup setup = Object.FindObjectOfType<LightVolumeSetup>();
        if (setup != null)
        {
            setup.RegisterPostProcessorCRT(crt);
            EditorUtility.SetDirty(setup);
            Debug.Log($"[Moment] Registered CRT with LightVolumeSetup on '{setup.gameObject.name}'.");
        }
        else
        {
            Debug.LogWarning("[Moment] No LightVolumeSetup found in scene. Register the CRT manually by adding it to the list of AtlasPostProcessors in the scene's Light Volume Manager.");
        }

        EditorUtility.SetDirty(alv);
        EditorUtility.SetDirty(crt);
        AssetDatabase.SaveAssets();

        Debug.Log($"[Moment] Setup complete for '{alv.gameObject.name}'.");
    }
}
