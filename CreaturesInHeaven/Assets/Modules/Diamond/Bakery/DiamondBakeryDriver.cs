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

        var   fixture = GetComponent<DiamondFixtureDriver>();
        Color colour  = fixture != null ? fixture.EmissionColor : Color.white;

        // Flux-conserving intensity: as the cone opens (spread grows), the same
        // emitter flux spreads over a larger cross-section, so the pool dims --
        // matching the shaft's emitterArea/crossArea falloff. Dividing by
        // spread^2 reproduces that; BrightnessScale absorbs the constant
        // emitterArea + unit-gap factors (so it now means "intensity at unit
        // spread"). Only meaningful for cone fixtures with an animated spread;
        // point/mesh fixtures leave BeamProps null and skip the correction.
        float brightness = LampProps.localPosition.y * BrightnessScale;
        if (BeamProps != null)
        {
            float spreadTan = BeamProps.localEulerAngles.x;
            // Floor spread^2 so a near-zero (pencil) beam doesn't divide to
            // infinity. 1e-4 ~= a ~1.1deg full cone -- tighter than any real
            // fixture, so it never clips a meaningful spread.
            brightness /= Mathf.Max(spreadTan * spreadTan, 1e-4f);
        }

        UpdateLightState(brightness, colour);
        UpdateConeAngle();
        UpdateRange(fixture);
    }

    // Tracks the Bakery cone's range (cutoff) to the beam's rendered length so
    // the baked pool fades out where the visible shaft ends. Per frame, because
    // each baked frame is a flipbook cell of animated lighting. No-op unless the
    // light is a cone with BeamProps assigned and the fixture has a beam
    // renderer to read the (material-level) haze/cutoff/max-length from.
    void UpdateRange(DiamondFixtureDriver fixture)
    {
        if (BeamProps == null) return;
        if (!(Light is BakeryPointLight point)) return;
        if (point.projMode != BakeryPointLight.ftLightProjectionMode.Cone) return;
        if (fixture == null || fixture.BeamRenderer == null) return;

        var mat = fixture.BeamRenderer.sharedMaterial;
        if (mat == null) return;

        // Round emitter: FixtureWidth is the diameter, so radius is half of it.
        // EmitterSize is mirrored from the profile by SyncEmitterSize.
        float emitterRadius = fixture.EmitterSize.x * 0.5f;
        float spreadTan     = BeamProps.localEulerAngles.x;

        // What drives the POOL's reach is the lamp brightness (the actual
        // emitted intensity), NOT the haze-shaft intensity. The shader derives
        // the *visible shaft* length from _BeamIntensity (BeamProps.localScale.y)
        // because that's what governs the fog density; but a brighter lamp
        // throws light farther onto surfaces regardless of haze, so for the bake
        // we feed lamp brightness in instead. Scaled by BrightnessScale to keep
        // it in the same unit family as the light intensity we push above.
        float poolReachIntensity = LampProps.localPosition.y * BrightnessScale;

        // Material-level (non-instanced) beam properties. These match the
        // floats DiamondBeamCommon.cginc reads; pull the live values so the
        // derived length tracks any material edits. _BeamCutoffThreshold here
        // acts as "how dim before the pool is considered gone" -- it may want a
        // different value than the shaft's visual cutoff if the reach feels off.
        float haze            = mat.GetFloat("_HazeDensity");
        float cutoffThreshold = mat.GetFloat("_BeamCutoffThreshold");
        float beamLengthMax   = mat.GetFloat("_BeamLengthMax");

        point.cutoff = DiamondBeamMath.DeriveRoundBeamLength(
            emitterRadius, spreadTan, poolReachIntensity, haze, cutoffThreshold, beamLengthMax);
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
