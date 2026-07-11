using UnityEngine;
using UnityEngine.Serialization;

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

    // --- Beam shape --------------------------------------------------

    // Cross-section profile of this fixture's beam/emitter.
    //   Rect  - rectangular emitter + (optionally elliptical) cone. Uses the
    //           Diamond/Beam shader and a Bakery mesh or point light.
    //   Round - circular emitter + symmetric cone (a true spotlight). Uses the
    //           Diamond/BeamRound shader and a Bakery cone (spot) light.
    //           FixtureWidth is the emitter DIAMETER; FixtureHeight is unused.
    public enum BeamShape { Rect, Round }
    public BeamShape Shape = BeamShape.Rect;

    // --- Material channels -------------------------------------------

    public float BrightnessMin;
    public float BrightnessMax;

    [FormerlySerializedAs("HasSpread")]
    public bool HasZoom;

    // Zoom range in degrees (full cone angle). Clamps the editor UI to the
    // fixture's physical capabilities. ZoomDefault is what the inspector
    // resets to and what new fixtures sit at when the profile is assigned.
    // Only meaningful when HasZoom is true.
    [FormerlySerializedAs("SpreadMinDegrees")]
    public float ZoomMinDegrees     = 0f;
    [FormerlySerializedAs("SpreadMaxDegrees")]
    public float ZoomMaxDegrees     = 90f;
    [FormerlySerializedAs("SpreadDefaultDegrees")]
    public float ZoomDefaultDegrees = 30f;

    // Whether this fixture has a visible volumetric beam shaft.
    // Gates the "Beam Intensity" control in the FixtureDefinition inspector.
    public bool HasBeam;

    // Whether this fixture's beam has a programmable focus control. Gates the
    // "Focus" control in the FixtureDefinition inspector. FocusDefault is what
    // the inspector resets to and what new fixtures sit at when the profile is
    // assigned; 1 (fully collimated) matches the shader's Range(0,1) default.
    // Focus is 0-1 direct pass-through (no unit conversion like Zoom's
    // tan/degrees), so there's no Min/Max pair to author.
    public bool HasFocus;
    public float FocusDefault = 1f;

    // --- Bakery light ---------------------------------------------------

#if BAKERY_INCLUDED
    public DiamondBakeryLightType BakeryLightType;
    public float                  BakeryBrightnessScale;
    public Vector3                BakeryLightOffset;

    // Mesh lights only.
    public Vector3 BakeryMeshLightSize;
#endif
}
