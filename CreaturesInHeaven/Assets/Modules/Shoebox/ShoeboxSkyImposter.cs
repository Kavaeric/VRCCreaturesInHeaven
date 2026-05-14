
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// Attach to a child of the ShoeboxSky object to register it as an imposter plane.
// Slot order is determined by sibling index in the hierarchy (top = slot 0).
public class ShoeboxSkyImposter : UdonSharpBehaviour
{
    public Texture texture;
    [Range(0, 1)] public float scroll = 0;
    public Vector2 tiling = Vector2.one;
    public Vector2 offset = Vector2.zero;
    public bool animate = false;
}
