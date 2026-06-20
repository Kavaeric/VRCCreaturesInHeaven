#if BAKERY_INCLUDED
using UnityEngine;

// Mirrors animated Diamond fixture state to a Bakery light each editor update.
// Sibling to DiamondFixtureDefinition and DiamondFixtureDriver on the fixture root.
// Runs in edit mode so the Bakery light tracks the fixture as the animator scrubs.
[ExecuteAlways]
public class DiamondBakeryDriver : MonoBehaviour
{
    public Component  Light;
    public Transform  LampProps;
    public float      BrightnessScale;

    // Spot (cone) fixtures only. When set, the cone's full angle tracks the
    // animated spread so a scrubbed/baked frame reflects the fixture's spread.
    // BeamProps.localEulerAngles.x stores tan(half-angle); we convert to full
    // degrees (matching DiamondFixtureDefinition's convention). Leave null for
    // point/mesh fixtures or to keep the cone angle fixed at its authored value.
    public Transform  BeamProps;

    void Update()
    {
        if (Light == null || LampProps == null) return;

        float brightness = LampProps.localPosition.y * BrightnessScale;
        var   fixture   = GetComponent<DiamondFixtureDriver>();
        Color colour    = fixture != null ? fixture.EmissionColor : Color.white;

        UpdateLightState(brightness, colour);
        UpdateConeAngle();
    }

    // Mirrors animated spread onto a Bakery cone light's angle. No-op unless the
    // light is a BakeryPointLight in Cone mode and BeamProps is assigned.
    void UpdateConeAngle()
    {
        if (BeamProps == null) return;
        if (!(Light is BakeryPointLight point)) return;
        if (point.projMode != BakeryPointLight.ftLightProjectionMode.Cone) return;

        float tanHalfAngle = BeamProps.localEulerAngles.x;
        point.angle = DiamondFixtureDefinition.SpreadTanToDegrees(tanHalfAngle);
    }

    public void UpdateLightState(float intensity, Color colour)
    {
        bool lightIsOff = !LampProps.gameObject.activeSelf
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
