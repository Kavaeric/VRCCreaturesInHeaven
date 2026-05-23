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

        if (!PropsTransform.gameObject.activeSelf)
        {
            Light.gameObject.SetActive(false);
            return;
        }

        Light.gameObject.SetActive(true);

        float intensity = PropsTransform.localScale.x * BrightnessScale;
        var   fixture   = GetComponent<DiamondFixtureDriver>();
        Color colour    = fixture != null ? fixture.EmissionColor : Color.white;

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
