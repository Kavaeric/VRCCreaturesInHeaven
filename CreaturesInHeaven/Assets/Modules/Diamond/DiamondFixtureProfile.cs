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

    // Spread range in degrees (full cone angle). Clamps the editor UI to the
    // fixture's physical capabilities. SpreadDefault is what the inspector
    // resets to and what new fixtures sit at when the profile is assigned.
    // Only meaningful when HasSpread is true.
    public float SpreadMinDegrees     = 0f;
    public float SpreadMaxDegrees     = 90f;
    public float SpreadDefaultDegrees = 30f;

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
