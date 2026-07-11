using UnityEditor;
using UnityEngine;

// Editor-only scene-view gizmo that draws a fixture's beam culling bounds as a
// wireframe box. This is the same AABB the bake writes to BeamRenderer.bounds in
// DiamondFixtureDefinition.ComputeBeamBounds. Used for checking the bound neither
// clips the visible beam nor wastefully oversizes the culling silhouette.
//
// Reads the worst-case bounds scalars off DiamondFixtureDefinition (the same
// source ComputeBeamBounds uses), so the gizmo and the baked bounds agree. Used
// to target DiamondFixtureDriver; repointed to Definition when the driver retired.
public static class DiamondFixtureBoundsGizmo
{
    private static bool s_ShowBounds = false;

    [MenuItem("Tools/Diamond/Toggle beam culling bounds gizmo")]
    static void ToggleBoundsGizmo()
    {
        s_ShowBounds = !s_ShowBounds;
    }

    [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
    static void DrawBeamBounds(DiamondFixtureDefinition def, GizmoType gizmoType)
    {
        if (!s_ShowBounds || def.BeamRenderer == null) return;

        // Local-space AABB, mirroring ComputeBeamBounds: the beam fires along +Y
        // from y=0 to y=MaxBeamLength, so the box is centred halfway up and sized
        // by the shared lateral-extent formula on each axis.
        //
        // Haze/scatter take the manager's ceiling when that parameter is animated,
        // exactly as the bake does -- otherwise the gizmo would draw the material-
        // sized box while the bake wrote the (larger) ceiling-sized one, and future
        // you would be confused about why the beam spills past the drawn bounds.
        var manager = def.GetComponentInParent<DiamondManager>();
        float maxHaze    = EffectiveMax(manager != null && manager.AnimateHaze,    manager != null ? manager.MaxHazeDensity     : 0f, def.MaxHazeDensity);
        float maxScatter = EffectiveMax(manager != null && manager.AnimateScatter, manager != null ? manager.MaxScatterStrength : 0f, def.MaxScatterStrength);

        Vector2 emitter = def.FixtureEmitterSize;
        float halfX = DiamondBeamMath.LateralHalfExtent(
            emitter.x * 0.5f, def.MaxZoomTan, def.MaxShearX,
            maxHaze, maxScatter, def.MaxBeamLength);
        float halfZ = DiamondBeamMath.LateralHalfExtent(
            emitter.y * 0.5f, def.MaxZoomTan, def.MaxShearZ,
            maxHaze, maxScatter, def.MaxBeamLength);

        // Beam-space box (world metres), then divided by the cube counter-scale
        // into object space so localToWorld re-applies the cube's localScale and
        // cancels it -- mirroring both DiamondBeamVert and ComputeBeamBounds.
        // Without this the box comes out scaled by the counter-scale (a 50 m beam
        // drawn at 5 m when the scale is 0.1).
        Vector3 cs = def.SafeCubeLocalScale();
        Vector3 center = new Vector3(0f, def.MaxBeamLength * 0.5f / cs.y, 0f);
        Vector3 size   = new Vector3(
            halfX * 2f / cs.x, def.MaxBeamLength / cs.y, halfZ * 2f / cs.z);

        Matrix4x4 prevMatrix = Gizmos.matrix;
        Color     prevColor  = Gizmos.color;

        bool selected = (gizmoType & GizmoType.Selected) != 0;
        var t = def.BeamRenderer.transform;

        // (1) The tight local box, drawn under the renderer's matrix so it follows
        // the fixture's position/rotation/scale. This is what should enclose the
        // rasterised beam geometry. Compare it against the visible shaft.
        Gizmos.matrix = t.localToWorldMatrix;
        Gizmos.color  = selected ? new Color(0.3f, 0.9f, 1f, 0.9f)
                                 : new Color(0.3f, 0.9f, 1f, 0.25f);
        Gizmos.DrawWireCube(center, size);

        // (2) The world-space AABB the driver actually writes to renderer.bounds
        // (the box Unity's culler tests), reproduced from the same TransformPoint/
        // TransformVector path as ApplyBeamRendererBounds.
        if (selected)
        {
            Bounds local = new Bounds(center, size);
            Vector3 worldCenter  = t.TransformPoint(local.center);
            Vector3 worldExtents = t.TransformVector(local.extents);
            worldExtents = new Vector3(
                Mathf.Abs(worldExtents.x), Mathf.Abs(worldExtents.y), Mathf.Abs(worldExtents.z));

            Gizmos.matrix = Matrix4x4.identity;
            Gizmos.color  = new Color(1f, 0.6f, 0.2f, 0.5f);   // amber: the culler's AABB
            Gizmos.DrawWireCube(worldCenter, worldExtents * 2f);
        }

        Gizmos.matrix = prevMatrix;
        Gizmos.color  = prevColor;
    }

    // The max haze/scatter the bounds are sized to: the manager's ceiling when the
    // parameter is animated, else the material value. Mirrors the bake's -1/ceiling
    // rule (DiamondManagerDefinition.BakeFixtures), so the gizmo matches what the
    // bake wrote.
    private static float EffectiveMax(bool animated, float ceiling, float material)
        => animated ? ceiling : material;
}
