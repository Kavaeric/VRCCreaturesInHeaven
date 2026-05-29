using System;
using UdonSharp;
using UnityEngine;

// Runtime driver for a lighting fixture. Attach to the fixture root prefab.
// The parent animator keys properties on two proxy transforms and the head's
// localRotation directly. This script reads those each frame and applies
// brightness/collimation via MaterialPropertyBlock to preserve batching.
//
// Animatable channels are split across TWO transforms, each using a DIFFERENT
// Unity property, so the animator records them as fully independent curves
// rather than bundling them as a single Vector3 keyframe.
//
// _LampProps:
//   .localPosition.y    - Brightness (emissive multiplier, HDR range 0..2).
//                         Stored on position, not scale, so that localScale
//                         remains free to carry an RGB colour vector (each
//                         component independently keyable, and bundling is
//                         desirable for colour fades).
//   .gameObject.activeSelf - On/off.
//
// _BeamProps:
//   .localEulerAngles.x - Beam spread, stored as tan(half-angle). UIs convert
//                         to/from degrees at the boundary. (Rotation, not
//                         scale, so it doesn't bundle with intensity.)
//   .localScale.y       - Beam intensity (volumetric shaft brightness; haze).
//
// Free slots on _LampProps: localScale.xyz (reserved for future RGB colour),
// localPosition.x/z, localEulerAngles.xyz.
// Free slots on _BeamProps: localEulerAngles.y/z, localScale.x/z,
// localPosition.xyz -- eight more independent floats.
public class DiamondFixtureDriver : UdonSharpBehaviour
{
    // --- Inspector references ----------------------------------------

    // The moving head child GameObject. The animator keys its localRotation directly.
    public Transform Head;

    // Proxy transform whose localPosition.y carries animated brightness, and
    // whose gameObject.activeSelf is the on/off state.
    public Transform LampProps;

    // Proxy transform whose localEulerAngles.x carries animated spread and
    // localScale.y carries animated beam intensity.
    public Transform BeamProps;

    // The renderer on the head whose emissive is driven by brightness.
    public Renderer HeadRenderer;

    // The renderer on the volumetric beam cube. Optional; leave null if the
    // fixture has no beam (e.g. a wash light with no visible shaft).
    // Material should use the Diamond/Beam shader.
    public Renderer BeamRenderer;

    public Vector2 EmitterSize = new Vector2(1, 1);

    // Worst-case spread (as tan(half-angle)) used for renderer-bounds sizing.
    // Mirrored from the FixtureProfile by DiamondFixtureDefinition.SyncBounds.
    // Defaults to tan(45 degrees) = 1.0 -- a sane 90-degree max cone.
    public float MaxSpreadTan = 1f;

    // Worst-case beam length (metres) used for renderer-bounds sizing.
    // Mirrored from the shader's _BeamLengthMax. Hardcoded fallback matches
    // the shader's default; override per fixture if a beam material uses a
    // different cap.
    public float MaxBeamLength = 50f;

    // Base emission colour. Set via FixtureDefinition in the editor; not animated.
    // Brightness (from LampProps) is applied as a multiplier on top of this.
    public Color EmissionColor = Color.white;

    private MaterialPropertyBlock _propBlock;
    private MaterialPropertyBlock _beamPropBlock;

    // --- Lifecycle ---------------------------------------------------

    public void Start()
    {
        EnsurePropertyBlocks();
        ApplyBeamEmitterSize();
        ApplyBeamRendererBounds();
    }

    // Computes worst-case bounds for the beam renderer and writes them so
    // Unity's frustum culler doesn't disable the renderer when the small
    // proxy cube goes off-screen but the actual beam volume is still visible.
    //
    // The bounds are sized in local space to the fixture root, then assigned
    // as world-space bounds (Unity transforms them by the renderer's local
    // matrix internally when used for culling). Safe to call from edit mode.
    public void ApplyBeamRendererBounds()
    {
        if (BeamRenderer == null) return;

        // Lateral half-extent at the far cap of the beam: emitter half-width
        // plus the worst-case spread plus a small margin for the soft-edge
        // halo. Same shape the vertex shader inflates the bounding cube to.
        float halfLateralX = EmitterSize.x * 0.5f + MaxSpreadTan * MaxBeamLength + 1f;
        float halfLateralZ = EmitterSize.y * 0.5f + MaxSpreadTan * MaxBeamLength + 1f;

        // Local-space AABB. The beam fires along +Y from y=0 to y=MaxBeamLength.
        Vector3 center = new Vector3(0f, MaxBeamLength * 0.5f, 0f);
        Vector3 size   = new Vector3(halfLateralX * 2f, MaxBeamLength, halfLateralZ * 2f);
        Bounds localBounds = new Bounds(center, size);

        // Transform to world space. Renderer.bounds is in world space, so we
        // need to convert. Use the beam renderer's transform (not the fixture
        // root) since the bounds are about that GameObject's mesh.
        var t = BeamRenderer.transform;
        Vector3 worldCenter = t.TransformPoint(localBounds.center);
        // For arbitrary rotations a full corner-transform is needed, but the
        // simpler axis-aligned extent works fine for the worst-case sizing
        // (slightly overestimates after rotation, which is what we want).
        Vector3 worldExtents = t.TransformVector(localBounds.extents);
        worldExtents = new Vector3(Mathf.Abs(worldExtents.x), Mathf.Abs(worldExtents.y), Mathf.Abs(worldExtents.z));

        BeamRenderer.bounds = new Bounds(worldCenter, worldExtents * 2f);
    }

    // Lazily creates the property blocks so callers from edit mode (e.g.
    // FixtureDefinition.OnValidate -> ApplyBeamEmitterSize) don't NRE before
    // Start has had a chance to run.
    private void EnsurePropertyBlocks()
    {
        if (_propBlock == null)     _propBlock = new MaterialPropertyBlock();
        if (_beamPropBlock == null) _beamPropBlock = new MaterialPropertyBlock();
    }

    // Pushes EmitterSize onto the beam renderer's property block.
    // Safe to call from edit mode (used by FixtureDefinition.SyncEmitterSize).
    public void ApplyBeamEmitterSize()
    {
        if (BeamRenderer == null) return;

        EnsurePropertyBlocks();

        // Merge with whatever's already on the renderer so edit-time sync
        // doesn't clobber other property-block entries (e.g. _Color from
        // a preview helper).
        BeamRenderer.GetPropertyBlock(_beamPropBlock);
        _beamPropBlock.SetFloat("_EmitterWidth",  EmitterSize.x);
        _beamPropBlock.SetFloat("_EmitterHeight", EmitterSize.y);
        BeamRenderer.SetPropertyBlock(_beamPropBlock);
    }

    public void Update()
    {
        ApplyMaterialProperties();
    }

    // --- Application -------------------------------------------------

    private bool IsLightOff()
    {
        // If LampProps is disabled, it's off.
        if (!LampProps.gameObject.activeSelf)
        {
            return true;
        }

        // If brightness is 0, it basically is.
        float brightness = LampProps.localPosition.y;
        if (brightness == 0)
        {
            return true;
        }

        // So is if the set colour is black.
        if (EmissionColor == Color.black)
        {
            return true;
        }

        return false;
    }

    private void ApplyMaterialProperties()
    {
        if (HeadRenderer == null || LampProps == null)
        {
            Debug.LogWarning("[Diamond] No HeadRenderer or LampProps.");
            return;
        }

        if (IsLightOff())
        {
            _propBlock.SetColor("_EmissionColor", new Color(0f, 0f, 0f, 0f));
            HeadRenderer.SetPropertyBlock(_propBlock);

            if (BeamRenderer != null)
            {
                _beamPropBlock.SetColor("_Color", new Color(0f, 0f, 0f, 0f));
                BeamRenderer.SetPropertyBlock(_beamPropBlock);
                BeamRenderer.gameObject.SetActive(false);
            }
            return;
        }

        float brightness = LampProps.localPosition.y;

        // BeamProps is optional. If a fixture has no beam shaft, leave the
        // animated channels at their defaults (spread 0, intensity 1).
        float spread        = 0f;
        float beamIntensity = 1f;
        if (BeamProps != null)
        {
            spread        = BeamProps.localEulerAngles.x;
            beamIntensity = BeamProps.localScale.y;
        }

        Color drivenColour = EmissionColor * brightness;

        _propBlock.SetColor("_EmissionColor", drivenColour);
        HeadRenderer.SetPropertyBlock(_propBlock);

        // Mirror brightness-modulated colour, animated intensity, and spread
        // onto the beam shaft. Spread is symmetric (X = Z) for a square cone.
        if (BeamRenderer != null)
        {
            BeamRenderer.gameObject.SetActive(true);
            _beamPropBlock.SetColor("_Color", drivenColour);
            _beamPropBlock.SetFloat("_BeamIntensity", beamIntensity);
            _beamPropBlock.SetFloat("_SpreadX", spread);
            _beamPropBlock.SetFloat("_SpreadZ", spread);
            BeamRenderer.SetPropertyBlock(_beamPropBlock);
        }
    }
}
