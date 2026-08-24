
using UnityEngine;
using UnityEditor;

#if UNITY_EDITOR

// ResolutionGizmos
// Gizmo drawing helpers for the Asset Resolution tools.
//
// Positions are in whatever local space the caller has set up.
public static class ResolutionGizmos
{
    // Draws a wire circle on the plane spanned by the two given axes.
    // Defaults to the XZ (ground) plane when no axes are supplied.
    public static void DrawCircle(Vector3 center, float radius, int segments, Vector3 axisA, Vector3 axisB)
    {
        if (segments < 3 || radius <= 0f) return;

        float step = 2f * Mathf.PI / segments;
        Vector3 prev = center + axisA * radius;
        for (int i = 1; i <= segments; i++)
        {
            float angle = i * step;
            Vector3 next = center + (axisA * Mathf.Cos(angle) + axisB * Mathf.Sin(angle)) * radius;
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }

    // Convenience overload: circle on the XZ plane, the usual case for ground-plane rings.
    public static void DrawCircle(Vector3 center, float radius, int segments)
    {
        DrawCircle(center, radius, segments, Vector3.right, Vector3.forward);
    }

    // Draws a wire rectangle with rounded corners on the plane spanned by the two given axes.
    // halfExtents is the half-size of the rectangle's straight section along axisA and axisB;
    // cornerRadius is added outside that on all sides, so the overall half-size is
    // halfExtents + cornerRadius.
    //
    // This is the offset curve of a rectangle: every point on it is exactly cornerRadius away
    // from the rectangle. Squaring the corners off instead would put them cornerRadius * 1.414
    // away, overstating the distance diagonally.
    //
    // cornerSegments is the number of line segments per quarter-arc.
    public static void DrawRoundedRect(Vector3 center, Vector2 halfExtents, float cornerRadius, int cornerSegments, Vector3 axisA, Vector3 axisB)
    {
        float a = Mathf.Max(halfExtents.x, 0f);
        float b = Mathf.Max(halfExtents.y, 0f);
        float r = Mathf.Max(cornerRadius, 0f);

        // Nothing to draw: no straight sections and no corner radius.
        if (a <= 0f && b <= 0f && r <= 0f) return;

        // With no radius this degenerates to a plain rectangle, so a single segment per
        // corner is enough to close it up.
        int segs = r > 0f ? Mathf.Max(cornerSegments, 1) : 1;

        // The four corner centres, in counter-clockwise order starting from (+a, +b).
        // Each arc sweeps 90 degrees from the previous side's outward normal to the next.
        Vector2[] corners = { new Vector2(a, b), new Vector2(-a, b), new Vector2(-a, -b), new Vector2(a, -b) };

        Vector3 first = Vector3.zero;
        Vector3 prev = Vector3.zero;
        bool started = false;

        for (int c = 0; c < 4; c++)
        {
            // Arc c runs from angle c*90 to (c+1)*90 degrees.
            float startAngle = c * Mathf.PI * 0.5f;
            Vector2 corner = corners[c];

            for (int s = 0; s <= segs; s++)
            {
                float angle = startAngle + (s / (float)segs) * Mathf.PI * 0.5f;
                Vector2 p = corner + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * r;
                Vector3 point = center + axisA * p.x + axisB * p.y;

                if (!started)
                {
                    first = point;
                    started = true;
                }
                else
                {
                    Gizmos.DrawLine(prev, point);
                }
                prev = point;
            }
        }

        // Close the loop back to the first point of the first arc.
        if (started) Gizmos.DrawLine(prev, first);
    }

    // Convenience overload: rounded rectangle on the XZ plane, the usual case for ground-plane zones.
    public static void DrawRoundedRect(Vector3 center, Vector2 halfExtents, float cornerRadius, int cornerSegments)
    {
        DrawRoundedRect(center, halfExtents, cornerRadius, cornerSegments, Vector3.right, Vector3.forward);
    }

    // Returns a point on the rounded rectangle that DrawRoundedRect would draw, at fraction t
    // of the way around its perimeter. t is arc length, so it advances at a constant speed in
    // metres and wraps outside 0..1.
    //
    // t = 0 is the start of the corner arc nearest +axisA/+axisB, which is where
    // DrawRoundedRect begins its own walk, and t increases in the same direction.
    //
    // Useful for placing a label somewhere along a ring without hand-solving where the sides
    // and corners fall.
    public static Vector3 PointOnRoundedRect(Vector3 center, Vector2 halfExtents, float cornerRadius, float t, Vector3 axisA, Vector3 axisB)
    {
        float a = Mathf.Max(halfExtents.x, 0f);
        float b = Mathf.Max(halfExtents.y, 0f);
        float r = Mathf.Max(cornerRadius, 0f);

        // A rounded rectangle's perimeter is the four straight sides plus four quarter-arcs,
        // which together make one full circle of radius r.
        float sideA = 2f * a;
        float sideB = 2f * b;
        float arc = 0.5f * Mathf.PI * r;
        float perimeter = 2f * (sideA + sideB) + 4f * arc;

        // Degenerate zone with no size at all: everything is the centre.
        if (perimeter <= 0f) return center;

        // Walk that distance, in the same order DrawRoundedRect draws: corner arc at (+a, +b),
        // then the -axisA side, then the remaining corners and sides counter-clockwise.
        float d = Mathf.Repeat(t, 1f) * perimeter;

        // Corner centres and the angle each arc starts at, matching DrawRoundedRect.
        // The side that follows each arc runs along the direction the arc ended pointing.
        Vector2[] corners = { new Vector2(a, b), new Vector2(-a, b), new Vector2(-a, -b), new Vector2(a, -b) };
        Vector2[] sideDirs = { new Vector2(-1f, 0f), new Vector2(0f, -1f), new Vector2(1f, 0f), new Vector2(0f, 1f) };

        for (int c = 0; c < 4; c++)
        {
            // The corner arc.
            if (d <= arc)
            {
                float angle = c * Mathf.PI * 0.5f + (arc > 0f ? (d / arc) * Mathf.PI * 0.5f : 0f);
                Vector2 arcPoint = corners[c] + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * r;
                return center + axisA * arcPoint.x + axisB * arcPoint.y;
            }
            d -= arc;

            // The straight side that follows it. Sides alternate between the two axes, so
            // even-numbered corners are followed by a side of length sideA.
            float sideLen = (c % 2 == 0) ? sideA : sideB;
            if (d <= sideLen)
            {
                // Start of the side is where the arc ended: the next corner, pushed out by r
                // along the outward normal of the side being walked.
                float endAngle = (c + 1) * Mathf.PI * 0.5f;
                Vector2 sideStart = corners[c] + new Vector2(Mathf.Cos(endAngle), Mathf.Sin(endAngle)) * r;
                Vector2 sidePoint = sideStart + sideDirs[c] * d;
                return center + axisA * sidePoint.x + axisB * sidePoint.y;
            }
            d -= sideLen;
        }

        // Floating point drift can leave a sliver at the very end of the loop; treat it as t=0.
        return center + axisA * (a + r) + axisB * b;
    }

    // Convenience overload: point on a rounded rectangle on the XZ plane.
    public static Vector3 PointOnRoundedRect(Vector3 center, Vector2 halfExtents, float cornerRadius, float t)
    {
        return PointOnRoundedRect(center, halfExtents, cornerRadius, t, Vector3.right, Vector3.forward);
    }

    // Draws a wire box sitting on the given floor point, extending upward by size.y and
    // centred laterally on size.x by size.z. Gizmos.DrawWireCube is centred on all three axes,
    // which is the wrong anchor for a zone that rests on a floor.
    public static void DrawFloorBox(Vector3 floorCenter, Vector3 size)
    {
        Vector3 center = floorCenter + Vector3.up * (size.y * 0.5f);
        Gizmos.DrawWireCube(center, size);
    }

    // Draws a label at a position expressed in the caller's gizmo space.
    // Handles.Label ignores Gizmos.matrix, so the position is transformed to world space first.
    public static void DrawLabel(Vector3 localPos, string text)
    {
        Vector3 worldPos = Gizmos.matrix.MultiplyPoint3x4(localPos);
        Handles.Label(worldPos, text);
    }

    // Fades a base colour's alpha across a sequence of items, for stacked rings that
    // should get fainter with distance from the reference. index/count-1 drives the lerp;
    // a single item sits at the near end.
    public static Color FadeAlpha(Color baseColor, int index, int count, float nearAlpha, float farAlpha)
    {
        float t = count > 1 ? (float)index / (count - 1) : 0f;
        return new Color(baseColor.r, baseColor.g, baseColor.b, baseColor.a * Mathf.Lerp(nearAlpha, farAlpha, t));
    }
}

#endif
