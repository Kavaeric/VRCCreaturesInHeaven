using System;
using UdonSharp;
using UnityEngine;

// Runtime driver for a lighting fixture. Attach to the fixture root prefab.
// The parent animator keys properties on _PropsTransform's localScale and
// the head's localRotation directly. This script reads those each frame and
// applies brightness/collimation via MaterialPropertyBlock to preserve batching.
//
// _PropsTransform.localScale channels:
//   x - Brightness (emissive multiplier, HDR range 0..2)
//   y - Beam spread, stored as tan(half-angle) so the shader can use it
//       directly with no per-frame trig. UIs (inspector, properties window)
//       expose it as full cone angle in degrees and convert at the boundary.
//   z - Beam intensity (volumetric shaft brightness; "haze density")
public class DiamondFixtureDriver : UdonSharpBehaviour
{
    // --- Inspector references ----------------------------------------

    // The moving head child GameObject. The animator keys its localRotation directly.
    public Transform Head;

    // Proxy transform whose localScale carries the animated float channels.
    public Transform PropsTransform;

    // The renderer on the head whose emissive is driven by brightness.
    public Renderer HeadRenderer;

    // The renderer on the volumetric beam cube. Optional; leave null if the
    // fixture has no beam (e.g. a wash light with no visible shaft).
    // Material should use the Diamond/Beam shader.
    public Renderer BeamRenderer;

    public Vector2 EmitterSize = new Vector2(1, 1);

    // Base emission colour. Set via FixtureDefinition in the editor; not animated.
    // Brightness (from PropsTransform) is applied as a multiplier on top of this.
    public Color EmissionColor = Color.white;

    private MaterialPropertyBlock _propBlock;
    private MaterialPropertyBlock _beamPropBlock;

    // --- Lifecycle ---------------------------------------------------

    public void Start()
    {
        EnsurePropertyBlocks();
        ApplyBeamEmitterSize();
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
        // If PropsTransform is disabled, it's off.
        if (!PropsTransform.gameObject.activeSelf)
        {
            return true;
        }

        // If brightness is 0, it basically is.
        float brightness = PropsTransform.localScale.x;
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
        if (HeadRenderer == null || PropsTransform == null)
        {
            Debug.LogWarning("  [Diamond] No HeadRenderer or PropsTransform.");
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

        float brightness    = PropsTransform.localScale.x;
        float spread        = PropsTransform.localScale.y;
        float beamIntensity = PropsTransform.localScale.z;

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
