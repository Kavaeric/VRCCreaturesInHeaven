#if BAKERY_INCLUDED
using UnityEngine;

// Mirrors animated Diamond fixture state to a Bakery light each editor update.
// Sibling to DiamondFixtureDefinition and DiamondFixtureDriver on the fixture root.
// Runs in edit mode so the Bakery light tracks the fixture as the animator scrubs.
[ExecuteAlways]
public class DiamondBakeryDriver : MonoBehaviour
{
    public Component  Light;
    public Transform  PropsTransform;
    public float      BrightnessScale;

    void Update()
    {
        if (Light == null || PropsTransform == null) return;

        float intensity = PropsTransform.localScale.x * BrightnessScale;
        var   fixture   = GetComponent<DiamondFixtureDriver>();
        Color colour    = fixture != null ? fixture.EmissionColor : Color.white;

        UpdateLightState(intensity, colour);
    }

    public void UpdateLightState(float intensity, Color colour)
    {
        bool lightIsOff = !PropsTransform.gameObject.activeSelf
                       || intensity <= 0f
                       || (colour.r <= 0f && colour.g <= 0f && colour.b <= 0f);

        if (lightIsOff)
        {
            Light.gameObject.SetActive(false);
            return;
        }

        Light.gameObject.SetActive(true);

        if (Light is BakeryPointLight point)
        {
            point.intensity = intensity;
            point.color     = colour;
        }
        else if (Light is BakeryLightMesh mesh)
        {
            mesh.intensity = intensity;
            mesh.color     = colour;
        }
    }
}
#endif
