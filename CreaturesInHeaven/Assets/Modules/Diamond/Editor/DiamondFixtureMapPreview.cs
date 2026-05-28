using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Drives scene-view material preview for all DiamondFixtureDefinition components in edit mode.
// Subscribes to EditorApplication.update once and iterates all active definitions each tick,
// keeping DiamondFixtureDefinition itself free of any UnityEditor API.
[InitializeOnLoad]
public static class DiamondFixtureMapPreview
{
    // Per-definition MaterialPropertyBlocks, keyed by instance ID to avoid GC on every frame.
    // The head and beam each get their own block since their property names differ
    // (HeadRenderer uses _EmissionColor / _Spread; BeamRenderer uses _Color / _EmitterWidth / etc).
    private static readonly Dictionary<int, MaterialPropertyBlock> _headBlocks = new();
    private static readonly Dictionary<int, MaterialPropertyBlock> _beamBlocks = new();

    static DiamondFixtureMapPreview()
    {
        EditorApplication.update += OnEditorUpdate;
    }

    private static void OnEditorUpdate()
    {
        if (Application.isPlaying) return;

        var definitions = Object.FindObjectsByType<DiamondFixtureDefinition>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (var def in definitions)
        {
            var driver = def.GetComponent<DiamondFixtureDriver>();
            if (driver == null || driver.PropsTransform == null || driver.HeadRenderer == null) continue;

            int id = def.GetInstanceID();
            if (!_headBlocks.TryGetValue(id, out var headBlock))
            {
                headBlock = new MaterialPropertyBlock();
                _headBlocks[id] = headBlock;
            }
            if (!_beamBlocks.TryGetValue(id, out var beamBlock))
            {
                beamBlock = new MaterialPropertyBlock();
                _beamBlocks[id] = beamBlock;
            }

            if (!driver.PropsTransform.gameObject.activeSelf)
            {
                headBlock.SetColor("_EmissionColor", Color.black);
                driver.HeadRenderer.SetPropertyBlock(headBlock);

                if (driver.BeamRenderer != null)
                {
                    beamBlock.SetColor("_Color", Color.clear);
                    driver.BeamRenderer.SetPropertyBlock(beamBlock);
                }
                continue;
            }

            Color emission = def.Colour == DiamondFixtureDefinition.ColourMode.Blackbody
                ? DiamondFixtureDefinition.BlackbodyToRGB(def.ColourTemperature)
                : def.EmissionColor;

            float linearBrightness = driver.PropsTransform.localScale.x;
            float spread           = driver.PropsTransform.localScale.y;
            float beamIntensity    = driver.PropsTransform.localScale.z;
            Color drivenColour     = emission * linearBrightness;

            headBlock.SetColor("_EmissionColor", drivenColour);
            driver.HeadRenderer.SetPropertyBlock(headBlock);

            // Mirror onto the beam shaft: brightness-modulated colour, animated
            // intensity, animated spread (stored as tan(half-angle)), and the
            // emitter dimensions from the driver (kept in sync with the profile
            // by DiamondFixtureDefinition.SyncEmitterSize).
            if (driver.BeamRenderer != null)
            {
                beamBlock.SetColor("_Color", drivenColour);
                beamBlock.SetFloat("_EmitterWidth",  driver.EmitterSize.x);
                beamBlock.SetFloat("_EmitterHeight", driver.EmitterSize.y);
                beamBlock.SetFloat("_BeamIntensity", beamIntensity);
                beamBlock.SetFloat("_SpreadX",       spread);
                beamBlock.SetFloat("_SpreadZ",       spread);
                driver.BeamRenderer.SetPropertyBlock(beamBlock);
            }
        }
    }
}
