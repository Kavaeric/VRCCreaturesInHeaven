using UnityEngine;

// Describes the capabilities and limits of a fixture type.
// Assign one profile asset per fixture type; all fixtures of that type share it.
// Editor-only — FixtureDefinition references this, and FixtureDefinition is stripped at build time.
[CreateAssetMenu(fileName = "FixtureProfile", menuName = "Lighting/Fixture Profile")]
public class FixtureProfile : ScriptableObject
{
    // --- Rotation axes -----------------------------------------------

    public string FixtureMake;
    public string FixtureModel;
    public string FixtureDescription;

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
}
