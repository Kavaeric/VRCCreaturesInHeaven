using UnityEditor;
using UnityEngine;

// Editor-only scene-view gizmo that draws a DiamondFixtureDriver's beam culling
// bounds as a wireframe box. This is the same AABB the driver writes to
// BeamRenderer.bounds in ApplyBeamRendererBounds. Used for checking the bound
// neither clips the visible beam nor wastefully oversizes the culling silhouette.
public static class DiamondFixtureBoundsGizmo
{
    private static bool s_ShowBounds = false;

    [MenuItem("Tools/Diamond/Toggle beam culling bounds gizmo")]
    static void ToggleBoundsGizmo()
    {
        s_ShowBounds = !s_ShowBounds;
    }

    [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
    static void DrawBeamBounds(DiamondFixtureDriver driver, GizmoType gizmoType)
    {
        if (!s_ShowBounds || driver.BeamRenderer == null) return;

        // Local-space AABB, mirroring ApplyBeamRendererBounds: the beam fires
        // along +Y from y=0 to y=MaxBeamLength, so the box is centred halfway up
        // and sized by the shared lateral-extent formula on each axis.
        float halfX = DiamondBeamMath.LateralHalfExtent(
            driver.EmitterSize.x * 0.5f, driver.MaxSpreadTan, driver.MaxShear,
            driver.MaxHazeDensity, driver.MaxScatterStrength, driver.MaxBeamLength);
        float halfZ = DiamondBeamMath.LateralHalfExtent(
            driver.EmitterSize.y * 0.5f, driver.MaxSpreadTan, driver.MaxShear,
            driver.MaxHazeDensity, driver.MaxScatterStrength, driver.MaxBeamLength);

        // Beam-space box (world metres), then divided by the cube counter-scale
        // into object space so localToWorld re-applies the cube's localScale and
        // cancels it -- mirroring both DiamondBeamVert and ApplyBeamRendererBounds.
        // Without this the box comes out scaled by the counter-scale (a 50 m beam
        // drawn at 5 m when the scale is 0.1).
        Vector3 cs = driver.SafeCubeLocalScale();
        Vector3 center = new Vector3(0f, driver.MaxBeamLength * 0.5f / cs.y, 0f);
        Vector3 size   = new Vector3(
            halfX * 2f / cs.x, driver.MaxBeamLength / cs.y, halfZ * 2f / cs.z);

        Matrix4x4 prevMatrix = Gizmos.matrix;
        Color     prevColor  = Gizmos.color;

        bool selected = (gizmoType & GizmoType.Selected) != 0;
        var t = driver.BeamRenderer.transform;

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
}
