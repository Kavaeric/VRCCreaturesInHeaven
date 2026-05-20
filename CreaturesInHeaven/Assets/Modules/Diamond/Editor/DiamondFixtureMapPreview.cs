using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Drives scene-view material preview for all DiamondFixtureDefinition components in edit mode.
// Subscribes to EditorApplication.update once and iterates all active definitions each tick,
// keeping DiamondFixtureDefinition itself free of any UnityEditor API.
[InitializeOnLoad]
public static class DiamondFixtureMapPreview
{
    // Per-definition MaterialPropertyBlock, keyed by instance ID to avoid GC on every frame.
    private static readonly Dictionary<int, MaterialPropertyBlock> _propBlocks = new();

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
            if (!_propBlocks.TryGetValue(id, out var propBlock))
            {
                propBlock = new MaterialPropertyBlock();
                _propBlocks[id] = propBlock;
            }

            if (!driver.PropsTransform.gameObject.activeSelf)
            {
                propBlock.SetColor("_EmissionColor", Color.black);
                driver.HeadRenderer.SetPropertyBlock(propBlock);
                continue;
            }

            Color emission = def.Colour == DiamondFixtureDefinition.ColourMode.Blackbody
                ? DiamondFixtureDefinition.BlackbodyToRGB(def.ColourTemperature)
                : def.EmissionColor;

            float linearBrightness = driver.PropsTransform.localScale.x;
            float spread           = driver.PropsTransform.localScale.y;

            propBlock.SetColor("_EmissionColor", emission * linearBrightness);
            propBlock.SetFloat("_Spread", spread);
            driver.HeadRenderer.SetPropertyBlock(propBlock);
        }
    }
}
