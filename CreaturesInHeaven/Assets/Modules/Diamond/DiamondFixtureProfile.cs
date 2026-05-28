using UnityEngine;

// Describes the capabilities and limits of a fixture type.
// Assign one profile asset per fixture type; all fixtures of that type share it.
// Editor-only: FixtureDefinition references this, and FixtureDefinition is stripped at build time.
[CreateAssetMenu(fileName = "DiamondFixtureProfile", menuName = "Diamond fixture profile")]
public class DiamondFixtureProfile : ScriptableObject
{
    // --- Rotation axes -----------------------------------------------

    public string FixtureMake;
    public string FixtureModel;
    public string FixtureDescription;
    public string FixtureType;
    public float FixtureWidth;
    public float FixtureHeight;

    [System.Serializable]

    public struct RotationAxis
    {
        public bool Enabled;
        public float Min;
        public float Max;
    }

    // Pan: head local X axis (tilt up/down)
    public RotationAxis AxisX;

    // Tilt: head local Y axis (pan left/right)
    public RotationAxis AxisY;

    // Roll: head local Z axis
    public RotationAxis AxisZ;

    // --- Material channels -------------------------------------------

    public float BrightnessMin;
    public float BrightnessMax;

    public bool HasSpread;

    // Whether this fixture has a visible volumetric beam shaft.
    // Gates the "Beam Intensity" control in the FixtureDefinition inspector.
    public bool HasBeam;

    // --- Bakery light ---------------------------------------------------

#if BAKERY_INCLUDED
    public DiamondBakeryLightType BakeryLightType;
    public float                  BakeryBrightnessScale;
    public Vector3                BakeryLightOffset;

    // Mesh lights only.
    public Vector3 BakeryMeshLightSize;
#endif
}
