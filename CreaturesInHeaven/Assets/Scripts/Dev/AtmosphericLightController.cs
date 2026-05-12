using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

// AtmosphericLightController
//
// Calculates the appropriate colour of sunlight given atmospheric properties and the sun's
// location, and writes that colour to a Unity (directional) Light or Bakery Directional Light component.
//
// Rayleigh scattering algorithm stolen from Hiyu

[ExecuteInEditMode]
public class AtmosphericLightController : MonoBehaviour
{
    [SerializeField] private bool autoUpdate = true;
    [SerializeField] private float peakIntensity = 1.0f;
    [SerializeField] private Vector3 sunDirection = Vector3.forward;

    private Light unityLight;
    private BakeryDirectLight bakeryLight;

    private float groundRadiusMM = 6.360f;
    private const int SUNTRANSMITTANCE_STEPS = 40;

    [SerializeField] private float gizmoDistance = 50f;

    void OnEnable()
    {
        unityLight = GetComponent<Light>();
        bakeryLight = GetComponent<BakeryDirectLight>();
    }

    public void RecalculateAtmosphericLight()
    {
        if (unityLight == null) unityLight = GetComponent<Light>();
        if (bakeryLight == null) bakeryLight = GetComponent<BakeryDirectLight>();

        Vector3 normalizedSunDir = sunDirection.normalized;
        Color sunColor = GetSunTransmittance(Vector3.zero, normalizedSunDir, out float transmittanceIntensity);
        float finalIntensity = transmittanceIntensity * peakIntensity;

        if (unityLight != null)
        {
            unityLight.color = sunColor;
            unityLight.intensity = finalIntensity;
        }

        if (bakeryLight != null)
        {
            bakeryLight.color = sunColor;
            bakeryLight.intensity = finalIntensity;
        }
    }

    public void SyncSunDirectionFromLight()
    {
        if (unityLight == null) unityLight = GetComponent<Light>();
        if (unityLight != null)
        {
            sunDirection = -unityLight.transform.forward;
            RecalculateAtmosphericLight();
        }
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (!autoUpdate) return;
        if (!EditorApplication.isPlayingOrWillChangePlaymode)
        {
            RecalculateAtmosphericLight();
        }
    }

    void Update()
    {
        Vector3 rayStart = transform.position;
        Vector3 rayEnd = rayStart + sunDirection.normalized * gizmoDistance;

        Debug.DrawLine(rayStart, rayEnd, Color.red, 0f);
    }
#endif

    void GetScattering(Vector3 pos, out Vector3 rayleigh, out float mie, out Vector3 extinction)
    {
        float altitudeKM = (pos.magnitude - groundRadiusMM) * 1000;

        float MieBase = 3.996f;
        Vector3 RayBase = new Vector3(5.802f, 13.558f, 33.1f);
        float Density_rayleigh = Mathf.Exp(-altitudeKM / 8);
        float Density_mie = Mathf.Exp(-altitudeKM / 1.2f);
        float Density_ozone = Mathf.Max(0, 1 - Mathf.Abs(altitudeKM - 25) / 15);

        float Absorption_rayleigh = 0;
        float Absorption_mie = 4.4f;
        Vector3 Absorption_ozone = new Vector3(0.650f, 1.881f, 0.085f) * 2;

        rayleigh = RayBase * Density_rayleigh * 0.5f;
        mie = MieBase * Density_mie;

        extinction = rayleigh + Vector3.one * mie;
        extinction += Vector3.one * (Absorption_rayleigh * Density_rayleigh);
        extinction += Vector3.one * (Absorption_mie * Density_mie);
        extinction += Absorption_ozone * Density_ozone;
    }

    // Ray-sphere intersection
    // Returns distance from ro to sphere surface
    float iSphere(Vector3 startPos, Vector3 rayDir, float radius)
    {
        float b = Vector3.Dot(startPos, rayDir);
        float c = Vector3.Dot(startPos, startPos) - radius * radius;

        if (c > 0 && b > 0) return -1;

        float discr = b * b - c;
        if (discr < 0) return -1;

        if (discr > b * b) return -b + Mathf.Sqrt(discr);

        return -b - Mathf.Sqrt(discr);
    }

    Color GetSunTransmittance(Vector3 pos, Vector3 sunDir, out float intensity)
    {
        pos = new Vector3(0, groundRadiusMM + Mathf.Max(2, pos.y) * 0.000001f, 0);
        intensity = 0;
        float atmosphereRadiusMM = 6.460f;
        if (iSphere(pos, sunDir, groundRadiusMM) > 0) return Color.black;
        float atmoDist = iSphere(pos, sunDir, atmosphereRadiusMM);
        float t = 0;

        Vector3 transmittance = Vector3.one;
        for (int i = 0; i < SUNTRANSMITTANCE_STEPS; i++)
        {
            float newT = (i + 0.3f) / SUNTRANSMITTANCE_STEPS * atmoDist;
            float dt = newT - t;
            t = newT;

            Vector3 rayleigh, extinction;
            float mie;
            GetScattering(pos + t * sunDir, out rayleigh, out mie, out extinction);

            Vector3 dtExt = -dt * extinction;
            float eX = Mathf.Exp(dtExt.x);
            float eY = Mathf.Exp(dtExt.y);
            float eZ = Mathf.Exp(dtExt.z);
            transmittance = Vector3.Scale(transmittance, new Vector3(eX, eY, eZ));
        }
        intensity = transmittance.magnitude;
        Color input = new Color(transmittance.x, transmittance.y, transmittance.z, 1).gamma;
        Color.RGBToHSV(input, out float hue, out float saturation, out float value);
        Color output = Color.HSVToRGB(hue, saturation, 1);
        return output;
    }
}
