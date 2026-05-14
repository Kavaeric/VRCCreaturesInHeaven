using UdonSharp;
using UnityEngine;

// Runtime driver for a lighting fixture. Attach to the fixture root prefab.
// The parent animator keys properties on _PropsTransform's localScale and
// the head's localRotation directly. This script reads those each frame and
// applies brightness/collimation via MaterialPropertyBlock to preserve batching.
//
// _PropsTransform.localScale channels:
//   x — Brightness (emissive multiplier, HDR range 0..2)
//   y — Collimation (beam tightness, 0..1)
public class DiamondFixtureDriver : UdonSharpBehaviour
{
    // --- Inspector references ----------------------------------------

    // The moving head child GameObject. The animator keys its localRotation directly.
    public Transform Head;

    // Proxy transform whose localScale carries the animated float channels.
    public Transform PropsTransform;

    // The renderer on the head whose emissive is driven by brightness.
    public Renderer HeadRenderer;

    // Base emission colour. Set via FixtureDefinition in the editor; not animated.
    // Brightness (from PropsTransform) is applied as a multiplier on top of this.
    public Color EmissionColor = Color.white;

    private MaterialPropertyBlock _propBlock;

    // --- Lifecycle ---------------------------------------------------

    public void Start()
    {
        _propBlock = new MaterialPropertyBlock();
    }

    public void Update()
    {
        ApplyMaterialProperties();
    }

    // --- Application -------------------------------------------------

    private void ApplyMaterialProperties()
    {
        if (HeadRenderer == null || PropsTransform == null)
        {
            Debug.Log("  [FixtureDriver] No HeadRenderer or PropsTransform.");
            return;
        }

        if (!PropsTransform.gameObject.activeSelf)
        {
            _propBlock.SetColor("_EmissionColor", new Color(0f, 0f, 0f, 0f));
            HeadRenderer.SetPropertyBlock(_propBlock);
            return;
        }

        float brightness   = PropsTransform.localScale.x;
        float spread  = PropsTransform.localScale.y;

        _propBlock.SetColor("_EmissionColor", EmissionColor * brightness);
        _propBlock.SetFloat("_Spread", spread);
        HeadRenderer.SetPropertyBlock(_propBlock);
    }
}
