using UnityEngine;

// Exact signed distance from a point to a Bezier edge.
//
// Mathematical core of the vector-sourced encoder, ported from msdfgen's edge-segments.cpp.
// Replaces the raster pipeline's distance transform: instead of measuring to the nearest lit texel,
// it measures to the actual curve, so the result is exact everywhere rather than quantised to the
// source resolution.
public static class SDFAtlasEdgeDistance
{
    // A distance paired with the alignment of the edge at the closest point.
    //
    // The `dot` term exists to break ties. When a point is nearest to a shared endpoint of
    // two edges, both report the same distance, and the sign each would assign can differ.
    // Preferring the edge whose direction is more perpendicular to the point (smaller dot)
    // picks the one whose sign is meaningful, which is what keeps corners from flickering
    // between inside and outside.
    public struct SignedDistance
    {
        public double distance;
        public double dot;

        public static SignedDistance Infinite => new SignedDistance
        {
            distance = double.MaxValue,
            dot = 0.0,
        };

        // Ordering used to select the closest edge: nearer wins, and on a tie the more
        // perpendicular one wins.
        public bool IsCloserThan(SignedDistance other)
        {
            double a = System.Math.Abs(distance);
            double b = System.Math.Abs(other.distance);
            if (a < b) return true;
            if (a > b) return false;
            return dot < other.dot;
        }
    }

    // --- Dispatch ---------------------------------------------------------

    // Signed distance from a point to an edge, plus the curve parameter of the closest
    // point.
    //
    // The parameter is returned because it is needed afterwards: values outside 0..1 mean
    // the closest point is beyond an endpoint, which is what triggers the pseudo-distance
    // extension used to keep corners sharp.
    public static SignedDistance Distance(in SDFAtlasShape.Edge edge, Vector2 origin, out double param)
    {
        switch (edge.degree)
        {
            case 1: return LinearDistance(edge, origin, out param);
            case 2: return QuadraticDistance(edge, origin, out param);
            default: return CubicDistance(edge, origin, out param);
        }
    }

    // --- Linear -----------------------------------------------------------

    static SignedDistance LinearDistance(in SDFAtlasShape.Edge edge, Vector2 origin, out double param)
    {
        Vector2d p0 = new Vector2d(edge.p0);
        Vector2d p1 = new Vector2d(edge.p1);

        Vector2d aq = new Vector2d(origin) - p0;
        Vector2d ab = p1 - p0;

        param = Vector2d.Dot(aq, ab) / Vector2d.Dot(ab, ab);

        // Nearest endpoint: whichever half of the segment the projection falls in.
        Vector2d endpoint = param > 0.5 ? p1 : p0;
        Vector2d eq = endpoint - new Vector2d(origin);
        double endpointDistance = eq.Length;

        // If the projection lands within the segment, the perpendicular distance is the
        // true minimum, and its sign comes directly from which side of the line the point
        // is on.
        if (param > 0.0 && param < 1.0)
        {
            double orthoDistance = Vector2d.Dot(ab.Orthonormal(false), aq);
            if (System.Math.Abs(orthoDistance) < endpointDistance)
                return new SignedDistance { distance = orthoDistance, dot = 0.0 };
        }

        // Otherwise the nearest point is an endpoint. Sign from the cross product, and
        // record the alignment so a neighbouring edge sharing this endpoint can win the tie
        // if it is more perpendicular.
        double sign = NonZeroSign(Vector2d.Cross(aq, ab));
        double dot = System.Math.Abs(Vector2d.Dot(ab.Normalised, eq.Normalised));

        return new SignedDistance { distance = sign * endpointDistance, dot = dot };
    }

    // --- Quadratic --------------------------------------------------------

    // Closest point on a quadratic Bezier.
    //
    // Minimising |B(t) - origin|^2 means setting its derivative to zero, which for a
    // quadratic gives a cubic in t.
    static SignedDistance QuadraticDistance(in SDFAtlasShape.Edge edge, Vector2 origin, out double param)
    {
        Vector2d o = new Vector2d(origin);
        Vector2d p0 = new Vector2d(edge.p0);
        Vector2d p1 = new Vector2d(edge.p1);
        Vector2d p2 = new Vector2d(edge.p2);

        Vector2d qa = p0 - o;
        Vector2d ab = p1 - p0;
        Vector2d br = p2 - p1 - ab;   // second difference: the curve's constant acceleration

        double a = Vector2d.Dot(br, br);
        double b = 3.0 * Vector2d.Dot(ab, br);
        double c = 2.0 * Vector2d.Dot(ab, ab) + Vector2d.Dot(qa, br);
        double d = Vector2d.Dot(qa, ab);

        var roots = new double[3];
        int solutions = SolveCubic(roots, a, b, c, d);

        // Start with the distance to the start point, then improve.
        Vector2d epDir = Direction(edge, 0.0);
        double minDistance = NonZeroSign(Vector2d.Cross(epDir, qa)) * qa.Length;
        param = -Vector2d.Dot(qa, epDir) / Vector2d.Dot(epDir, epDir);

        // End point.
        {
            Vector2d bq = p2 - o;
            double distance = bq.Length;
            if (distance < System.Math.Abs(minDistance))
            {
                epDir = Direction(edge, 1.0);
                minDistance = NonZeroSign(Vector2d.Cross(epDir, bq)) * distance;
                param = Vector2d.Dot(o - p1, epDir) / Vector2d.Dot(epDir, epDir);
            }
        }

        // Interior critical points.
        for (int i = 0; i < solutions; i++)
        {
            double t = roots[i];
            if (t <= 0.0 || t >= 1.0) continue;

            Vector2d qe = qa + 2.0 * t * ab + t * t * br;
            double distance = qe.Length;

            if (distance <= System.Math.Abs(minDistance))
            {
                minDistance = NonZeroSign(Vector2d.Cross(ab + t * br, qe)) * distance;
                param = t;
            }
        }

        return Finish(edge, o, qa, p2 - o, minDistance, param);
    }

    // --- Cubic ------------------------------------------------------------

    // Number of evenly spaced starting points for the Newton search, and how many
    // refinement steps each start gets.
    //
    // A cubic's distance function can have up to five critical points, and no closed form
    // exists, so this brackets the curve with several starts and refines each. These are
    // msdfgen's values; raising them costs generation time linearly and changes results
    // only on pathological curves.
    const int CubicSearchStarts = 4;
    const int CubicSearchSteps = 4;

    static SignedDistance CubicDistance(in SDFAtlasShape.Edge edge, Vector2 origin, out double param)
    {
        Vector2d o = new Vector2d(origin);
        Vector2d p0 = new Vector2d(edge.p0);
        Vector2d p1 = new Vector2d(edge.p1);
        Vector2d p2 = new Vector2d(edge.p2);
        Vector2d p3 = new Vector2d(edge.p3);

        Vector2d qa = p0 - o;
        Vector2d ab = p1 - p0;
        Vector2d br = p2 - p1 - ab;
        Vector2d ass = (p3 - p2) - (p2 - p1) - br;   // third difference

        Vector2d epDir = Direction(edge, 0.0);
        double minDistance = NonZeroSign(Vector2d.Cross(epDir, qa)) * qa.Length;
        param = -Vector2d.Dot(qa, epDir) / Vector2d.Dot(epDir, epDir);

        {
            Vector2d bq = p3 - o;
            double distance = bq.Length;
            if (distance < System.Math.Abs(minDistance))
            {
                epDir = Direction(edge, 1.0);
                minDistance = NonZeroSign(Vector2d.Cross(epDir, bq)) * distance;
                param = Vector2d.Dot(epDir - bq, epDir) / Vector2d.Dot(epDir, epDir);
            }
        }

        // Newton-Raphson on the squared-distance derivative, from several starting points.
        //
        // The update is the standard second-order step: subtract f'/f'' where f is the
        // squared distance. Including the qe.d2 term in the denominator is what makes it
        // converge in a handful of iterations rather than crawling.
        for (int i = 0; i <= CubicSearchStarts; i++)
        {
            double t = (double)i / CubicSearchStarts;

            Vector2d qe = qa + 3.0 * t * ab + 3.0 * t * t * br + t * t * t * ass;
            Vector2d d1 = 3.0 * ab + 6.0 * t * br + 3.0 * t * t * ass;
            Vector2d d2 = 6.0 * br + 6.0 * t * ass;

            double improvedT = t - Vector2d.Dot(qe, d1) /
                                   (Vector2d.Dot(d1, d1) + Vector2d.Dot(qe, d2));

            if (improvedT <= 0.0 || improvedT >= 1.0) continue;

            int remainingSteps = CubicSearchSteps;
            do
            {
                t = improvedT;
                qe = qa + 3.0 * t * ab + 3.0 * t * t * br + t * t * t * ass;
                d1 = 3.0 * ab + 6.0 * t * br + 3.0 * t * t * ass;

                if (--remainingSteps == 0) break;

                d2 = 6.0 * br + 6.0 * t * ass;
                improvedT = t - Vector2d.Dot(qe, d1) /
                                (Vector2d.Dot(d1, d1) + Vector2d.Dot(qe, d2));
            }
            while (improvedT > 0.0 && improvedT < 1.0);

            double distance = qe.Length;
            if (distance < System.Math.Abs(minDistance))
            {
                minDistance = NonZeroSign(Vector2d.Cross(d1, qe)) * distance;
                param = t;
            }
        }

        return Finish(edge, o, qa, p3 - o, minDistance, param);
    }

    // Shared tail for the curve cases: package the result, recording endpoint alignment
    // when the closest point fell outside the curve.
    static SignedDistance Finish(in SDFAtlasShape.Edge edge, Vector2d origin,
                                 Vector2d qa, Vector2d bq, double minDistance, double param)
    {
        if (param >= 0.0 && param <= 1.0)
            return new SignedDistance { distance = minDistance, dot = 0.0 };

        if (param < 0.5)
        {
            double dot = System.Math.Abs(Vector2d.Dot(Direction(edge, 0.0).Normalised, qa.Normalised));
            return new SignedDistance { distance = minDistance, dot = dot };
        }
        else
        {
            double dot = System.Math.Abs(Vector2d.Dot(Direction(edge, 1.0).Normalised, bq.Normalised));
            return new SignedDistance { distance = minDistance, dot = dot };
        }
    }

    // --- Pseudo-distance --------------------------------------------------

    // Extends a distance past an edge's endpoint, as if the edge continued straight.
    //
    // Inside a corner, each of the two meeting edges reports the distance to its own
    // endpoint, and both curve away, so the true distance field has a crease there,
    // and any single channel storing it reconstructs the corner rounded. Extending
    // each edge's field along its own tangent instead gives each channel a straight,
    // creaseless field.
    //
    // Only applies when the closest point was beyond an endpoint (param outside 0..1) and
    // the point lies in the region the extension actually covers. Used both as the
    // single-channel field's own extension, and, in multi-channel, as an unbounded tie-break
    // applied to the near edge on top of PerpendicularAccumulator's per-edge extensions.
    public static void ToPseudoDistance(ref SignedDistance distance, in SDFAtlasShape.Edge edge,
                                        Vector2 origin, double param)
    {
        Vector2d o = new Vector2d(origin);

        if (param < 0.0)
        {
            Vector2d dir = Direction(edge, 0.0).Normalised;
            Vector2d aq = o - new Vector2d(edge.Start);
            double ts = Vector2d.Dot(aq, dir);

            // Only extend behind the start point, not sideways past it.
            if (ts < 0.0)
            {
                double perpendicular = Vector2d.Cross(aq, dir);
                if (System.Math.Abs(perpendicular) <= System.Math.Abs(distance.distance))
                {
                    distance.distance = perpendicular;
                    distance.dot = 0.0;
                }
            }
        }
        else if (param > 1.0)
        {
            Vector2d dir = Direction(edge, 1.0).Normalised;
            Vector2d bq = o - new Vector2d(edge.End);
            double ts = Vector2d.Dot(bq, dir);

            if (ts > 0.0)
            {
                double perpendicular = Vector2d.Cross(bq, dir);
                if (System.Math.Abs(perpendicular) <= System.Math.Abs(distance.distance))
                {
                    distance.distance = perpendicular;
                    distance.dot = 0.0;
                }
            }
        }
    }

    // --- Multi-channel perpendicular accumulator ---------------------------

    // Builds one channel's creaseless field. Ported from msdfgen's
    // PerpendicularDistanceSelectorBase.
    //
    // Folds in every edge in the channel, not just the nearest: each edge extends a
    // perpendicular distance into the domain owned by its start and end (bounded by the
    // bisector with its neighbour), and the channel keeps the closest such extension on each
    // side of zero. The final value is that extremum on the side matching the true-nearest
    // edge's sign, refined by letting the near edge's own unbounded extension win if closer.
    public struct PerpendicularAccumulator
    {
        SignedDistance minTrueDistance;
        double minNegativePerpendicular;
        double minPositivePerpendicular;
        SDFAtlasShape.Edge nearEdge;
        double nearEdgeParam;
        bool hasNearEdge;

        public static PerpendicularAccumulator Empty => new PerpendicularAccumulator
        {
            minTrueDistance = SignedDistance.Infinite,
            minNegativePerpendicular = -System.Math.Abs(SignedDistance.Infinite.distance),
            minPositivePerpendicular = System.Math.Abs(SignedDistance.Infinite.distance),
            hasNearEdge = false,
        };

        // Folds in one edge's contribution: true distance for sign/near-edge tracking, plus
        // its perpendicular extension into whichever neighbour's domain it reaches.
        public void Add(in SDFAtlasShape.Edge edge, in SDFAtlasShape.Edge previous, in SDFAtlasShape.Edge next,
                        SignedDistance distance, double param, Vector2 origin)
        {
            if (distance.IsCloserThan(minTrueDistance))
            {
                minTrueDistance = distance;
                nearEdge = edge;
                nearEdgeParam = param;
                hasNearEdge = true;
            }

            Vector2d o = new Vector2d(origin);
            Vector2d ap = o - new Vector2d(edge.Start);
            Vector2d bp = o - new Vector2d(edge.End);
            Vector2d aDir = Direction(edge, 0.0).Normalised;
            Vector2d bDir = Direction(edge, 1.0).Normalised;
            Vector2d prevDir = Direction(previous, 1.0).Normalised;
            Vector2d nextDir = Direction(next, 0.0).Normalised;

            double add = Vector2d.Dot(ap, (prevDir + aDir).Normalised);
            double bdd = -Vector2d.Dot(bp, (bDir + nextDir).Normalised);

            if (add > 0.0)
            {
                double pd = distance.distance;
                if (GetPerpendicularDistance(ref pd, ap, -aDir.x, -aDir.y))
                    AddPerpendicular(-pd);
            }

            if (bdd > 0.0)
            {
                double pd = distance.distance;
                if (GetPerpendicularDistance(ref pd, bp, bDir.x, bDir.y))
                    AddPerpendicular(pd);
            }
        }

        void AddPerpendicular(double distance)
        {
            if (distance <= 0.0 && distance > minNegativePerpendicular)
                minNegativePerpendicular = distance;
            if (distance >= 0.0 && distance < minPositivePerpendicular)
                minPositivePerpendicular = distance;
        }

        // Resolves the accumulated contributions into the channel's final signed distance.
        public double Resolve(Vector2 origin)
        {
            if (!hasNearEdge) return 0.0;

            double resolved = minTrueDistance.distance < 0.0
                ? minNegativePerpendicular
                : minPositivePerpendicular;

            SignedDistance nearDistance = minTrueDistance;
            ToPseudoDistance(ref nearDistance, nearEdge, origin, nearEdgeParam);
            if (System.Math.Abs(nearDistance.distance) < System.Math.Abs(resolved))
                resolved = nearDistance.distance;

            return resolved;
        }
    }

    // Perpendicular distance of `ep` against direction `edgeDir`, kept only if it both lies
    // ahead of the edge (ts > 0) and improves on the distance already in `distance`.
    //
    // Ported from PerpendicularDistanceSelectorBase::getPerpendicularDistance.
    static bool GetPerpendicularDistance(ref double distance, Vector2d ep, double dirX, double dirY)
    {
        double ts = ep.x * dirX + ep.y * dirY;
        if (ts > 0.0)
        {
            double perpendicular = ep.x * dirY - ep.y * dirX;
            if (System.Math.Abs(perpendicular) < System.Math.Abs(distance))
            {
                distance = perpendicular;
                return true;
            }
        }
        return false;
    }

    // --- Helpers ----------------------------------------------------------

    // Edge tangent at parameter t, in double precision.
    static Vector2d Direction(in SDFAtlasShape.Edge edge, double t)
    {
        Vector2d p0 = new Vector2d(edge.p0);
        Vector2d p1 = new Vector2d(edge.p1);
        Vector2d p2 = new Vector2d(edge.p2);
        Vector2d p3 = new Vector2d(edge.p3);

        switch (edge.degree)
        {
            case 1:
                return p1 - p0;

            case 2:
            {
                Vector2d d = 2.0 * Lerp(p1 - p0, p2 - p1, t);
                if (d.SqrLength < 1e-24) return p2 - p0;
                return d;
            }

            default:
            {
                Vector2d a = Lerp(p1 - p0, p2 - p1, t);
                Vector2d b = Lerp(p2 - p1, p3 - p2, t);
                Vector2d d = 3.0 * Lerp(a, b, t);

                if (d.SqrLength < 1e-24)
                {
                    if (t <= 0.0) return p2 - p0;
                    if (t >= 1.0) return p3 - p1;
                    return p3 - p0;
                }
                return d;
            }
        }
    }

    static Vector2d Lerp(Vector2d a, Vector2d b, double t) => a + (b - a) * t;

    // Sign that never returns zero, so a point exactly on an edge's line still gets a
    // definite side rather than collapsing the distance to unsigned.
    static double NonZeroSign(double value) => value > 0.0 ? 1.0 : -1.0;

    // --- Polynomial solvers -----------------------------------------------

    // Real roots of ax^2 + bx + c. Returns how many were written.
    // A return of -1 means the equation is degenerate (0 == 0), i.e. infinitely many roots.
    static int SolveQuadratic(double[] x, double a, double b, double c)
    {
        // Treat a vanishing leading coefficient as a linear equation. The magnitude test
        // catches the case where a is not exactly zero but is so small that dividing by it
        // would produce a root dominated by rounding error.
        if (a == 0.0 || System.Math.Abs(b) > 1e12 * System.Math.Abs(a))
        {
            if (b == 0.0) return c == 0.0 ? -1 : 0;
            x[0] = -c / b;
            return 1;
        }

        double discriminant = b * b - 4.0 * a * c;

        if (discriminant > 0.0)
        {
            double root = System.Math.Sqrt(discriminant);
            x[0] = (-b + root) / (2.0 * a);
            x[1] = (-b - root) / (2.0 * a);
            return 2;
        }
        if (discriminant == 0.0)
        {
            x[0] = -b / (2.0 * a);
            return 1;
        }
        return 0;
    }

    // Real roots of a monic cubic x^3 + ax^2 + bx + c, by the trigonometric method for
    // three real roots and Cardano's formula otherwise.
    static int SolveCubicNormed(double[] x, double a, double b, double c)
    {
        double a2 = a * a;
        double q = (a2 - 3.0 * b) / 9.0;
        double r = (a * (2.0 * a2 - 9.0 * b) + 27.0 * c) / 54.0;
        double r2 = r * r;
        double q3 = q * q * q;

        a /= 3.0;

        // Three distinct real roots: express them via cosines of a third of an angle.
        if (r2 < q3)
        {
            double t = r / System.Math.Sqrt(q3);
            t = System.Math.Max(-1.0, System.Math.Min(1.0, t));
            t = System.Math.Acos(t);

            q = -2.0 * System.Math.Sqrt(q);
            x[0] = q * System.Math.Cos(t / 3.0) - a;
            x[1] = q * System.Math.Cos((t + 2.0 * System.Math.PI) / 3.0) - a;
            x[2] = q * System.Math.Cos((t - 2.0 * System.Math.PI) / 3.0) - a;
            return 3;
        }

        // One real root, or a repeated pair.
        double u = (r < 0.0 ? 1.0 : -1.0) *
                   System.Math.Pow(System.Math.Abs(r) + System.Math.Sqrt(r2 - q3), 1.0 / 3.0);
        double v = u == 0.0 ? 0.0 : q / u;

        x[0] = (u + v) - a;

        if (u == v || System.Math.Abs(u - v) < 1e-12 * System.Math.Abs(u + v))
        {
            x[1] = -0.5 * (u + v) - a;
            return 2;
        }
        return 1;
    }

    // Real roots of ax^3 + bx^2 + cx + d.
    static int SolveCubic(double[] x, double a, double b, double c, double d)
    {
        if (a != 0.0)
        {
            double bn = b / a;

            // Beyond this ratio, normalising amplifies rounding error more than dropping
            // the cubic term does, so solve the quadratic instead.
            if (System.Math.Abs(bn) < 1e6)
                return SolveCubicNormed(x, bn, c / a, d / a);
        }
        return SolveQuadratic(x, b, c, d);
    }

    // --- Double-precision 2D vector ----------------------------------------

    // Unity's Vector2 is float-only, and this maths needs doubles (see the class comment).
    // Kept minimal and internal rather than made a general-purpose type.
    public struct Vector2d
    {
        public double x;
        public double y;

        public Vector2d(double x, double y) { this.x = x; this.y = y; }
        public Vector2d(Vector2 v) { x = v.x; y = v.y; }

        public double SqrLength => x * x + y * y;
        public double Length => System.Math.Sqrt(x * x + y * y);

        // Returns the zero vector unchanged rather than producing NaN, so a degenerate
        // edge cannot poison an entire channel of the field.
        public Vector2d Normalised
        {
            get
            {
                double length = Length;
                if (length == 0.0) return new Vector2d(0.0, 0.0);
                return new Vector2d(x / length, y / length);
            }
        }

        // Unit normal. `polarity` selects which of the two perpendicular directions.
        public Vector2d Orthonormal(bool polarity)
        {
            double length = Length;
            if (length == 0.0) return new Vector2d(0.0, polarity ? 1.0 : -1.0);
            return polarity ? new Vector2d(-y / length, x / length)
                            : new Vector2d(y / length, -x / length);
        }

        public static double Dot(Vector2d a, Vector2d b) => a.x * b.x + a.y * b.y;
        public static double Cross(Vector2d a, Vector2d b) => a.x * b.y - a.y * b.x;

        public static Vector2d operator +(Vector2d a, Vector2d b) => new Vector2d(a.x + b.x, a.y + b.y);
        public static Vector2d operator -(Vector2d a, Vector2d b) => new Vector2d(a.x - b.x, a.y - b.y);
        public static Vector2d operator *(double s, Vector2d v) => new Vector2d(s * v.x, s * v.y);
        public static Vector2d operator *(Vector2d v, double s) => new Vector2d(s * v.x, s * v.y);

        public Vector2 ToVector2() => new Vector2((float)x, (float)y);
    }
}
