
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using UnityEngine.Rendering;

public class SetLighting : UdonSharpBehaviour
{
    public Color ambientSkyColor = Color.white;
    public Color ambientEquatorColor = Color.white;
    public Color ambientGroundColor = Color.white;

    public ReflectionProbe reflectionProbe;

    public float AmbientIntensity = 1;
    //public float SHIntensity = 1;

    private void OnValidate()
    {
        Apply();
    }

    void OnEnable()
    {
        Apply();
    }

    void Apply()
    {
        // Environment light
        RenderSettings.ambientSkyColor = ambientSkyColor;
        RenderSettings.ambientEquatorColor = ambientEquatorColor;
        RenderSettings.ambientGroundColor = ambientGroundColor;
        RenderSettings.ambientIntensity = AmbientIntensity;

        // SH, only applies to players and dynamic objects
        // Might not actually be needed
        //SphericalHarmonicsL2 SH = new SphericalHarmonicsL2();
        //SH.AddDirectionalLight(Vector3.up, ambientSkyColor * SHIntensity, 1);
        //SH.AddDirectionalLight(Vector3.down, ambientGroundColor * SHIntensity, 1);
        //
        //SH.AddDirectionalLight(Vector3.left, ambientEquatorColor * SHIntensity * 0.25f, 1);
        //SH.AddDirectionalLight(Vector3.right, ambientEquatorColor * SHIntensity * 0.25f, 1);
        //SH.AddDirectionalLight(Vector3.forward, ambientEquatorColor * SHIntensity * 0.25f, 1);
        //SH.AddDirectionalLight(Vector3.back, ambientEquatorColor * SHIntensity * 0.25f, 1);
        //
        //RenderSettings.ambientProbe = SH;
        
        if (reflectionProbe)
            reflectionProbe.RenderProbe();
    }
}
