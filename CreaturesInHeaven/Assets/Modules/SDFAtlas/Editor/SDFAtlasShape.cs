using System.Collections.Generic;
using UnityEngine;

// Vector shape representation for MSDF generation.
//
// A shape is a set of closed contours; a contour is a sequence of edge segments that join
// end to end. This mirrors the structure msdfgen uses, because the multi-channel technique
// is defined on shape topology rather than on pixels: corners are found by comparing edge
// directions where two segments meet, and each edge is assigned colour channels based on
// where the corners fall.
public class SDFAtlasShape
{
    public readonly List<Contour> contours = new List<Contour>();

    // How overlapping contours combine into filled area. Set by the loader from the SVG's
    // fill-rule, and consumed when deciding the sign of a distance.
    public enum FillRule
    {
        NonZero,   // SVG default: filled where winding number != 0
        EvenOdd,   // filled where the crossing count is odd
    }

    public FillRule fillRule = FillRule.NonZero;

    // --- Edge segments ---------------------------------------------------

    // Which colour channels an edge contributes to.
    //
    // The values are a bitmask over RGB so that channel membership can be tested with a
    // bitwise AND, matching msdfgen's EdgeColor. MSDF only ever uses the three two-channel
    // combinations (plus White for degenerate cases): each edge must be present in at least
    // two channels, so that any single channel still sees a closed shape.
    public enum EdgeColour
    {
        Black = 0,
        Red = 1,
        Green = 2,
        Yellow = 3,   // red + green
        Blue = 4,
        Magenta = 5,  // red + blue
        Cyan = 6,     // green + blue
        White = 7,    // all three
    }

    // A single edge of a contour: a line, or a quadratic or cubic Bezier.
    //
    // One struct covers all three rather than a class hierarchy, because the degree is the
    // only thing that varies and a tagged struct keeps the generator's inner loop free of
    // virtual dispatch -- that loop runs per texel per edge, so it is the hot path.
    public struct Edge
    {
        public Vector2 p0;      // start point
        public Vector2 p1;      // control point 1, or end point for a line
        public Vector2 p2;      // control point 2, or end point for a quadratic
        public Vector2 p3;      // end point for a cubic
        public int degree;      // 1 = line, 2 = quadratic, 3 = cubic
        public EdgeColour colour;

        public static Edge Line(Vector2 a, Vector2 b) => new Edge
        {
            p0 = a, p1 = b, p2 = b, p3 = b, degree = 1, colour = EdgeColour.White,
        };

        public static Edge Quadratic(Vector2 a, Vector2 c, Vector2 b) => new Edge
        {
            p0 = a, p1 = c, p2 = b, p3 = b, degree = 2, colour = EdgeColour.White,
        };

        public static Edge Cubic(Vector2 a, Vector2 c0, Vector2 c1, Vector2 b) => new Edge
        {
            p0 = a, p1 = c0, p2 = c1, p3 = b, degree = 3, colour = EdgeColour.White,
        };

        public Vector2 Start => p0;

        public Vector2 End
        {
            get
            {
                switch (degree)
                {
                    case 1: return p1;
                    case 2: return p2;
                    default: return p3;
                }
            }
        }

        // Point on the edge at parameter t in 0..1, by de Casteljau.
        public Vector2 Point(float t)
        {
            switch (degree)
            {
                case 1:
                    return Vector2.LerpUnclamped(p0, p1, t);

                case 2:
                {
                    Vector2 a = Vector2.LerpUnclamped(p0, p1, t);
                    Vector2 b = Vector2.LerpUnclamped(p1, p2, t);
                    return Vector2.LerpUnclamped(a, b, t);
                }

                default:
                {
                    Vector2 a = Vector2.LerpUnclamped(p0, p1, t);
                    Vector2 b = Vector2.LerpUnclamped(p1, p2, t);
                    Vector2 c = Vector2.LerpUnclamped(p2, p3, t);
                    Vector2 d = Vector2.LerpUnclamped(a, b, t);
                    Vector2 e = Vector2.LerpUnclamped(b, c, t);
                    return Vector2.LerpUnclamped(d, e, t);
                }
            }
        }

        // Tangent direction at parameter t. Not normalised: callers that need a unit vector
        // normalise themselves, and the raw derivative is what the distance solver wants.
        //
        // A degenerate control point (coincident with an endpoint) makes the derivative
        // vanish at t=0 or t=1, which would break corner detection. In that case fall back
        // to the chord, which is the limiting direction.
        public Vector2 Direction(float t)
        {
            switch (degree)
            {
                case 1:
                    return p1 - p0;

                case 2:
                {
                    Vector2 d = Vector2.LerpUnclamped(p1 - p0, p2 - p1, t) * 2f;
                    if (d.sqrMagnitude < 1e-12f) return p2 - p0;
                    return d;
                }

                default:
                {
                    Vector2 a = Vector2.LerpUnclamped(p1 - p0, p2 - p1, t);
                    Vector2 b = Vector2.LerpUnclamped(p2 - p1, p3 - p2, t);
                    Vector2 d = Vector2.LerpUnclamped(a, b, t) * 3f;
                    if (d.sqrMagnitude < 1e-12f)
                    {
                        // Both ends degenerate: the chord is all that is left.
                        if (t <= 0f) return p2 - p0;
                        if (t >= 1f) return p3 - p1;
                        return p3 - p0;
                    }
                    return d;
                }
            }
        }

        // Splits the edge into three consecutive parts covering the same curve.
        //
        // Needed by the edge colourer: a contour with only one or two segments cannot carry
        // three distinct colours, so its segments get subdivided until it can.
        public void SplitInThirds(out Edge a, out Edge b, out Edge c)
        {
            a = SubCurve(0f, 1f / 3f);
            b = SubCurve(1f / 3f, 2f / 3f);
            c = SubCurve(2f / 3f, 1f);

            a.colour = colour;
            b.colour = colour;
            c.colour = colour;
        }

        // Extracts the portion of this edge between two parameter values as a new edge of
        // the same degree, using the standard Bezier subdivision blossom.
        public Edge SubCurve(float t0, float t1)
        {
            switch (degree)
            {
                case 1:
                    return Line(Point(t0), Point(t1));

                case 2:
                {
                    // Blossom values for a quadratic: f(t0,t0), f(t0,t1), f(t1,t1).
                    Vector2 a = Point(t0);
                    Vector2 b = Blossom2(t0, t1);
                    Vector2 c = Point(t1);
                    var e = Quadratic(a, b, c);
                    e.colour = colour;
                    return e;
                }

                default:
                {
                    // Blossom values for a cubic: f(t0,t0,t0), f(t0,t0,t1), f(t0,t1,t1),
                    // f(t1,t1,t1).
                    Vector2 a = Point(t0);
                    Vector2 b = Blossom3(t0, t0, t1);
                    Vector2 c = Blossom3(t0, t1, t1);
                    Vector2 d = Point(t1);
                    var e = Cubic(a, b, c, d);
                    e.colour = colour;
                    return e;
                }
            }
        }

        // Symmetric blossom of the quadratic at two parameters.
        Vector2 Blossom2(float u, float v)
        {
            Vector2 a = Vector2.LerpUnclamped(p0, p1, u);
            Vector2 b = Vector2.LerpUnclamped(p1, p2, u);
            return Vector2.LerpUnclamped(a, b, v);
        }

        // Symmetric blossom of the cubic at three parameters.
        Vector2 Blossom3(float u, float v, float w)
        {
            Vector2 a = Vector2.LerpUnclamped(p0, p1, u);
            Vector2 b = Vector2.LerpUnclamped(p1, p2, u);
            Vector2 c = Vector2.LerpUnclamped(p2, p3, u);
            Vector2 d = Vector2.LerpUnclamped(a, b, v);
            Vector2 e = Vector2.LerpUnclamped(b, c, v);
            return Vector2.LerpUnclamped(d, e, w);
        }

        // Expands a bounding box to contain this edge.
        //
        // Control points are included rather than solving for the true extrema: a Bezier is
        // contained within the convex hull of its control points, so this is conservative,
        // and the bound is only used for framing rather than for anything precision-critical.
        public void Bound(ref Vector2 min, ref Vector2 max)
        {
            Include(p0, ref min, ref max);
            Include(End, ref min, ref max);

            if (degree >= 2) Include(p1, ref min, ref max);
            if (degree >= 3) Include(p2, ref min, ref max);
        }

        static void Include(Vector2 p, ref Vector2 min, ref Vector2 max)
        {
            min.x = Mathf.Min(min.x, p.x);
            min.y = Mathf.Min(min.y, p.y);
            max.x = Mathf.Max(max.x, p.x);
            max.y = Mathf.Max(max.y, p.y);
        }
    }

    // --- Contours ---------------------------------------------------------

    // A closed loop of edges. Consecutive edges share endpoints, and the last edge's end
    // meets the first edge's start.
    public class Contour
    {
        public readonly List<Edge> edges = new List<Edge>();

        // Signed area, positive for counter-clockwise. Used to determine winding direction,
        // which is what distinguishes a hole from a solid under the nonzero fill rule.
        //
        // Computed on a polyline approximation rather than exactly: the sign is all that
        // matters here, and sampling curves finely enough to get the sign right is far
        // simpler than integrating the Bezier area formula per degree.
        public float SignedArea(int samplesPerEdge = 8)
        {
            float area = 0f;
            Vector2 previous = Vector2.zero;
            bool started = false;

            foreach (Edge edge in edges)
            {
                for (int i = 0; i <= samplesPerEdge; i++)
                {
                    Vector2 point = edge.Point((float)i / samplesPerEdge);
                    if (started) area += previous.x * point.y - point.x * previous.y;
                    previous = point;
                    started = true;
                }
            }
            return area * 0.5f;
        }

        public void Bound(ref Vector2 min, ref Vector2 max)
        {
            foreach (Edge edge in edges) edge.Bound(ref min, ref max);
        }
    }

    // --- Shape-level operations -------------------------------------------

    public int EdgeCount
    {
        get
        {
            int count = 0;
            foreach (Contour contour in contours) count += contour.edges.Count;
            return count;
        }
    }

    // Axis-aligned bounds of every contour. Returns false for an empty shape.
    public bool Bounds(out Vector2 min, out Vector2 max)
    {
        min = new Vector2(float.MaxValue, float.MaxValue);
        max = new Vector2(float.MinValue, float.MinValue);

        if (EdgeCount == 0) return false;

        foreach (Contour contour in contours) contour.Bound(ref min, ref max);
        return true;
    }

    // Removes contours that carry no area, and repairs contours whose edges do not quite
    // meet.
    //
    // Exporters routinely emit coordinates rounded to a few decimals, so a subpath that
    // closes with Z can still leave a sub-unit gap between the last edge's end and the
    // first edge's start. Left alone, that gap is a hole in the outline that the distance
    // solver will happily measure into, producing a spike of wrong distance. Snapping the
    // endpoints together is safe because the gap is far below one output texel.
    public void Normalise()
    {
        for (int i = contours.Count - 1; i >= 0; i--)
        {
            Contour contour = contours[i];

            if (contour.edges.Count == 0)
            {
                contours.RemoveAt(i);
                continue;
            }

            // Close any gap by moving the last edge's endpoint onto the first edge's start.
            Edge first = contour.edges[0];
            Edge last = contour.edges[contour.edges.Count - 1];

            if ((last.End - first.Start).sqrMagnitude > 0f)
            {
                last = MoveEnd(last, first.Start);
                contour.edges[contour.edges.Count - 1] = last;
            }

            // A contour of one segment cannot enclose area unless it is a curve; a single
            // line collapsed to a point is degenerate and would divide by zero downstream.
            if (contour.edges.Count == 1 && contour.edges[0].degree == 1)
                contours.RemoveAt(i);
        }
    }

    // Returns a copy of an edge with its endpoint moved, preserving degree and colour.
    static Edge MoveEnd(Edge edge, Vector2 to)
    {
        switch (edge.degree)
        {
            case 1: edge.p1 = to; edge.p2 = to; edge.p3 = to; break;
            case 2: edge.p2 = to; edge.p3 = to; break;
            default: edge.p3 = to; break;
        }
        return edge;
    }

    // Applies a uniform scale and translation to every control point.
    //
    // Used to frame a shape into the encoder's pixel space. Kept as an explicit pass rather
    // than a transform carried alongside the shape, so that everything downstream can assume
    // shape coordinates are already the coordinates it works in.
    public void Transform(Vector2 scale, Vector2 offset)
    {
        foreach (Contour contour in contours)
        {
            for (int i = 0; i < contour.edges.Count; i++)
            {
                Edge e = contour.edges[i];
                e.p0 = Vector2.Scale(e.p0, scale) + offset;
                e.p1 = Vector2.Scale(e.p1, scale) + offset;
                e.p2 = Vector2.Scale(e.p2, scale) + offset;
                e.p3 = Vector2.Scale(e.p3, scale) + offset;
                contour.edges[i] = e;
            }
        }
    }
}
