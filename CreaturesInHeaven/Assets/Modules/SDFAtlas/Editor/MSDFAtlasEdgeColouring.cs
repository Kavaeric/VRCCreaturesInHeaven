using System.Collections.Generic;
using UnityEngine;

// Assigns colour channels to a shape's edges for MSDF atlas encoding.
//
// From Chlumsky's thesis: a single distance field cannot represent a corner,
// because the true distance function has a crease there and bilinear interpolation of one
// scalar always smooths a crease into a curve.

// However, if the two edges meeting at a corner are stored in different channels,
// each channel individually sees a smooth field with no crease, and the corner
// reappears exactly where the two channels cross. Taking the median of three channels
// in the shader recovers it.
//
// Based on two rules:
//   - Every edge belongs to at least two of the three channels, so each channel on its own
//     still describes a closed shape rather than a collection of disconnected arcs.
//   - Edges meeting at a corner must differ in at least one channel, or the corner is not
//     represented and rounds off exactly as in the single-channel case.
//
// Ported from msdfgen's edgeColoringSimple.
public static class MSDFAtlasEdgeColouring
{
    // Maximum angle, in radians, still considered a smooth join rather than a corner.
    // 3 radians is about 172 degrees, close to straight.
    public const double DefaultAngleThreshold = 3.0;

    // --- Public API -------------------------------------------------------

    // Colours every edge of the shape in place.
    //
    // `seed` varies the starting channel. Different seeds give equally valid colourings;
    // Occasionally one colouring produces artifacts on a particular shape that another does not.
    public static void Apply(SDFAtlasShape shape, double angleThreshold = DefaultAngleThreshold,
                             ulong seed = 0)
    {
        double crossThreshold = System.Math.Sin(angleThreshold);
        SDFAtlasShape.EdgeColour colour = InitialColour(ref seed);

        var corners = new List<int>();

        foreach (SDFAtlasShape.Contour contour in shape.contours)
        {
            if (contour.edges.Count == 0) continue;

            FindCorners(contour, crossThreshold, corners);

            if (corners.Count == 0)
            {
                // A fully smooth contour (like a circle) has no corners to preserve.
                SwitchColour(ref colour, ref seed);
                for (int i = 0; i < contour.edges.Count; i++)
                {
                    SDFAtlasShape.Edge edge = contour.edges[i];
                    edge.colour = colour;
                    contour.edges[i] = edge;
                }
            }
            else if (corners.Count == 1)
            {
                ColourTeardrop(contour, corners[0], ref colour, ref seed);
            }
            else
            {
                ColourMultiCorner(contour, corners, ref colour, ref seed);
            }
        }
    }

    // --- Corner detection --------------------------------------------------

    // Fills `corners` with the indices of edges that begin at a corner.
    static void FindCorners(SDFAtlasShape.Contour contour, double crossThreshold, List<int> corners)
    {
        corners.Clear();

        // Start from the last edge's outgoing direction, so the join between the last and
        // first edge is tested like any other.
        Vector2 previousDirection = DirectionAt(contour.edges[contour.edges.Count - 1], 1f);

        for (int i = 0; i < contour.edges.Count; i++)
        {
            Vector2 currentDirection = DirectionAt(contour.edges[i], 0f);

            if (IsCorner(previousDirection.normalized, currentDirection.normalized, crossThreshold))
                corners.Add(i);

            previousDirection = DirectionAt(contour.edges[i], 1f);
        }
    }

    // Whether two consecutive directions form a corner.
    //
    // Two tests, because either alone has a blind spot. The dot product catches sharp turns
    // (anything at or beyond a right angle), while the cross product catches shallow ones
    // that the dot product would call nearly-parallel.
    static bool IsCorner(Vector2 a, Vector2 b, double crossThreshold)
    {
        double dot = (double)a.x * b.x + (double)a.y * b.y;
        double cross = (double)a.x * b.y - (double)a.y * b.x;
        return dot <= 0.0 || System.Math.Abs(cross) > crossThreshold;
    }

    // --- Colouring strategies ----------------------------------------------

    // A contour with exactly one corner: the "teardrop" case.
    //
    // With one corner there is only one join to preserve, but colouring the loop with two
    // channels would make the corner's two sides identical somewhere around the back. The
    // fix is three colours distributed around the loop, with white in the middle.
    static void ColourTeardrop(SDFAtlasShape.Contour contour, int corner,
                               ref SDFAtlasShape.EdgeColour colour, ref ulong seed)
    {
        var colours = new SDFAtlasShape.EdgeColour[3];

        SwitchColour(ref colour, ref seed);
        colours[0] = colour;
        colours[1] = SDFAtlasShape.EdgeColour.White;
        SwitchColour(ref colour, ref seed);
        colours[2] = colour;

        int count = contour.edges.Count;

        if (count >= 3)
        {
            // Spread the three colours evenly around the loop starting from the corner.
            for (int i = 0; i < count; i++)
            {
                int index = (corner + i) % count;
                SDFAtlasShape.Edge edge = contour.edges[index];
                edge.colour = colours[1 + SymmetricalTrichotomy(i, count)];
                contour.edges[index] = edge;
            }
        }
        else
        {
            // Fewer than three edges cannot carry three colours, so subdivide until they
            // can. Splitting is safe as the parts trace exactly the same curve.
            SplitForTeardrop(contour, corner, colours);
        }
    }

    // Splits a one- or two-edge contour into thirds so three colours fit.
    static void SplitForTeardrop(SDFAtlasShape.Contour contour, int corner,
                                 SDFAtlasShape.EdgeColour[] colours)
    {
        var parts = new SDFAtlasShape.Edge[7];
        int count = contour.edges.Count;

        if (count == 0) return;

        contour.edges[0].SplitInThirds(
            out parts[0 + 3 * corner], out parts[1 + 3 * corner], out parts[2 + 3 * corner]);

        if (count >= 2)
        {
            contour.edges[1].SplitInThirds(
                out parts[3 - 3 * corner], out parts[4 - 3 * corner], out parts[5 - 3 * corner]);

            parts[0].colour = parts[1].colour = colours[0];
            parts[2].colour = parts[3].colour = colours[1];
            parts[4].colour = parts[5].colour = colours[2];
        }
        else
        {
            parts[0].colour = colours[0];
            parts[1].colour = colours[1];
            parts[2].colour = colours[2];
        }

        contour.edges.Clear();
        for (int i = 0; i < parts.Length; i++)
        {
            // Unset entries are default-initialised structs with degree 0; the split only
            // fills the first three or six.
            if (parts[i].degree == 0) continue;
            contour.edges.Add(parts[i]);
        }
    }

    // The general case: two or more corners.
    //
    // Each run of edges between consecutive corners (a "spline") gets one colour, switching
    // at every corner so the two sides of each corner always differ.
    static void ColourMultiCorner(SDFAtlasShape.Contour contour, List<int> corners,
                                  ref SDFAtlasShape.EdgeColour colour, ref ulong seed)
    {
        int cornerCount = corners.Count;
        int spline = 0;
        int start = corners[0];
        int count = contour.edges.Count;

        SwitchColour(ref colour, ref seed);
        SDFAtlasShape.EdgeColour initialColour = colour;

        for (int i = 0; i < count; i++)
        {
            int index = (start + i) % count;

            if (spline + 1 < cornerCount && corners[spline + 1] == index)
            {
                spline++;

                // On the final spline, ban the initial colour: the loop closes back onto
                // the first spline, so reusing that colour would erase the corner between
                // them.
                var banned = (spline == cornerCount - 1)
                    ? initialColour
                    : SDFAtlasShape.EdgeColour.Black;

                SwitchColour(ref colour, ref seed, banned);
            }

            SDFAtlasShape.Edge edge = contour.edges[index];
            edge.colour = colour;
            contour.edges[index] = edge;
        }
    }

    // --- Colour sequence ---------------------------------------------------

    // Returns -1, 0, or 1 depending on whether a position falls in the first, middle, or
    // last third of a run, balanced so the three thirds come out equally sized.
    //
    // The constants are msdfgen's; they encode the rounding needed to split n items into
    // three groups without biasing any one of them.
    static int SymmetricalTrichotomy(int position, int n)
    {
        double value = 3.0 + 2.875 * position / (n - 1) - 1.4375 + 0.5;
        return (int)value - 3;
    }

    // Pulls two bits off the seed, consuming them.
    static int SeedExtract2(ref ulong seed)
    {
        int value = (int)(seed & 1UL);
        seed >>= 1;
        return value;
    }

    static int SeedExtract3(ref ulong seed)
    {
        int value = (int)(seed % 3UL);
        seed /= 3UL;
        return value;
    }

    // The three valid starting colours: each has exactly two channels set, satisfying the
    // at-least-two-channels rule.
    static SDFAtlasShape.EdgeColour InitialColour(ref ulong seed)
    {
        var colours = new[]
        {
            SDFAtlasShape.EdgeColour.Cyan,
            SDFAtlasShape.EdgeColour.Magenta,
            SDFAtlasShape.EdgeColour.Yellow,
        };
        return colours[SeedExtract3(ref seed)];
    }

    // Rotates to a different two-channel colour.
    //
    // The shift-and-wrap keeps the result within the three two-channel combinations: a
    // 3-bit rotate of a two-bit pattern always lands on another two-bit pattern.
    static void SwitchColour(ref SDFAtlasShape.EdgeColour colour, ref ulong seed)
    {
        int shifted = (int)colour << (1 + SeedExtract2(ref seed));
        colour = (SDFAtlasShape.EdgeColour)((shifted | (shifted >> 3)) & (int)SDFAtlasShape.EdgeColour.White);
    }

    // Rotates to a different colour, avoiding one that must not be reused.
    //
    // If the current and banned colours share exactly one channel, the complement of that
    // shared channel is the unique colour differing from both, so pick it directly.
    // Otherwise any rotation will do.
    static void SwitchColour(ref SDFAtlasShape.EdgeColour colour, ref ulong seed,
                             SDFAtlasShape.EdgeColour banned)
    {
        var combined = (SDFAtlasShape.EdgeColour)((int)colour & (int)banned);

        if (combined == SDFAtlasShape.EdgeColour.Red ||
            combined == SDFAtlasShape.EdgeColour.Green ||
            combined == SDFAtlasShape.EdgeColour.Blue)
        {
            colour = (SDFAtlasShape.EdgeColour)((int)combined ^ (int)SDFAtlasShape.EdgeColour.White);
        }
        else
        {
            SwitchColour(ref colour, ref seed);
        }
    }

    // --- Helpers -----------------------------------------------------------

    // Edge tangent, falling back to the chord when a degenerate control point makes the
    // derivative vanish. Corner detection compares these, so a zero vector here would
    // produce a spurious corner.
    static Vector2 DirectionAt(SDFAtlasShape.Edge edge, float t)
    {
        Vector2 direction = edge.Direction(t);
        if (direction.sqrMagnitude < 1e-12f) return edge.End - edge.Start;
        return direction;
    }
}
