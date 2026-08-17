using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

// Loads an SVG file into an SDFAtlasShape.
//
// Scope is deliberately narrow: this reads filled path outlines and nothing else. No
// strokes, no styling, no gradients, no text elements, no groups with transforms. Signage
// artwork is expected to be flattened to outlines before export, which is what Figma's
// "flatten" does and what every other vector tool offers under some name.
//
// Every <path> in the file is merged into one shape. A logo is routinely exported as many
// sibling paths (one per letter, per counter, per detail), and they collectively form the
// single graphic that belongs in one atlas cell.
//
// The SVG Y axis points down, while distance generation and UV space both run bottom-up.
// The loader flips Y at parse time so that everything is aligned to UV convention.
public static class SDFAtlasSvgLoader
{
    // --- Public API ------------------------------------------------------

    // Parses an SVG file into a shape, in the SVG's own coordinate space with Y flipped up.
    //
    // Returns null and logs if the file has no usable path data. Throws no exceptions for
    // malformed path syntax: unparseable commands are skipped with a warning, because a
    // partially-correct outline is easier to diagnose visually than a stack trace.
    public static SDFAtlasShape Load(string assetPath, out Vector2 documentSize)
    {
        documentSize = Vector2.zero;

        string text;
        try { text = File.ReadAllText(assetPath); }
        catch (IOException e)
        {
            Debug.LogError($"[SDFAtlas] Could not read '{assetPath}': {e.Message}");
            return null;
        }

        return ParseDocument(text, assetPath, out documentSize);
    }

    // --- Document parsing -------------------------------------------------

    static readonly Regex PathElementPattern =
        new Regex(@"<path\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    static readonly Regex AttributePattern =
        new Regex(@"(?<name>[\w-]+)\s*=\s*""(?<value>[^""]*)""", RegexOptions.Singleline);

    static readonly Regex SvgElementPattern =
        new Regex(@"<svg\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    static SDFAtlasShape ParseDocument(string text, string sourceName, out Vector2 documentSize)
    {
        documentSize = Vector2.zero;

        // Document dimensions come from viewBox where present, since width/height may carry
        // units (px, mm) while viewBox is always in user units.
        Match svgMatch = SvgElementPattern.Match(text);
        if (svgMatch.Success)
        {
            var attributes = ReadAttributes(svgMatch.Value);

            if (attributes.TryGetValue("viewbox", out string viewBox))
            {
                float[] box = ParseNumbers(viewBox);
                if (box.Length >= 4) documentSize = new Vector2(box[2], box[3]);
            }

            if (documentSize == Vector2.zero)
            {
                float width = attributes.TryGetValue("width", out string w) ? ParseLength(w) : 0f;
                float height = attributes.TryGetValue("height", out string h) ? ParseLength(h) : 0f;
                documentSize = new Vector2(width, height);
            }
        }

        var shape = new SDFAtlasShape();
        bool sawEvenOdd = false;
        bool sawNonZero = false;

        foreach (Match pathMatch in PathElementPattern.Matches(text))
        {
            var attributes = ReadAttributes(pathMatch.Value);

            if (!attributes.TryGetValue("d", out string data) || string.IsNullOrWhiteSpace(data))
                continue;

            // A path explicitly filled "none" contributes no area.
            if (attributes.TryGetValue("fill", out string fill) &&
                fill.Trim().Equals("none", System.StringComparison.OrdinalIgnoreCase))
                continue;

            if (attributes.TryGetValue("fill-rule", out string rule))
            {
                if (rule.Trim().Equals("evenodd", System.StringComparison.OrdinalIgnoreCase))
                    sawEvenOdd = true;
                else
                    sawNonZero = true;
            }
            else
            {
                sawNonZero = true;   // SVG's default
            }

            ParsePathData(data, shape, sourceName);
        }

        if (shape.EdgeCount == 0)
        {
            Debug.LogError($"[SDFAtlas] '{sourceName}' contains no usable path data.");
            return null;
        }

        // Fill rule is a per-path property in SVG but a per-shape one here, since all paths
        // merge into a single outline. They agree in practice: an exporter picks one rule
        // for a document. Warn if they disagree.
        if (sawEvenOdd && sawNonZero)
        {
            Debug.LogWarning(
                $"[SDFAtlas] '{sourceName}' mixes evenodd and nonzero fill rules across its " +
                "paths. Using evenodd for the whole shape; if holes render solid (or vice " +
                "versa), re-export with a single fill rule.");
        }

        shape.fillRule = sawEvenOdd ? SDFAtlasShape.FillRule.EvenOdd : SDFAtlasShape.FillRule.NonZero;

        // Flip Y so the shape is bottom-up, matching UV space and the atlas convention.
        //
        // Flipping mirrors the shape, which reverses every contour's winding direction. That
        // is corrected in Normalise-time winding handling rather than here, since the
        // generator determines inside/outside from winding sign directly.
        if (documentSize.y > 0f)
            shape.Transform(new Vector2(1f, -1f), new Vector2(0f, documentSize.y));
        else
            shape.Transform(new Vector2(1f, -1f), Vector2.zero);

        shape.Normalise();
        return shape;
    }

    static Dictionary<string, string> ReadAttributes(string elementText)
    {
        var attributes = new Dictionary<string, string>();
        foreach (Match m in AttributePattern.Matches(elementText))
            attributes[m.Groups["name"].Value.ToLowerInvariant()] = m.Groups["value"].Value;
        return attributes;
    }

    // --- Path data parsing -------------------------------------------------

    // Splits path data into command letters and their numeric arguments.
    //
    // SVG path syntax is permissive: separators may be commas or whitespace or nothing at
    // all, numbers may be written ".5" or "1e-3", and a negative sign doubles as a
    // separator ("10-5" is two numbers). The number pattern handles all of those, which is
    // why it is more involved than it first appears.
    static readonly Regex CommandPattern = new Regex(@"([MmZzLlHhVvCcSsQqTtAa])([^MmZzLlHhVvCcSsQqTtAa]*)");
    static readonly Regex NumberPattern = new Regex(@"[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?");

    static void ParsePathData(string data, SDFAtlasShape shape, string sourceName)
    {
        SDFAtlasShape.Contour contour = null;

        Vector2 current = Vector2.zero;      // current point
        Vector2 subpathStart = Vector2.zero; // where the active subpath began, for Z
        Vector2 lastControl = Vector2.zero;  // for S/T smooth-curve reflection
        char lastCommand = ' ';

        foreach (Match commandMatch in CommandPattern.Matches(data))
        {
            char command = commandMatch.Groups[1].Value[0];
            float[] args = ParseNumbers(commandMatch.Groups[2].Value);

            bool relative = char.IsLower(command);
            char absolute = char.ToUpperInvariant(command);

            // Z takes no arguments; everything else consumes its arguments in groups, and
            // repeats the command if more remain (SVG's implicit-repetition rule).
            if (absolute == 'Z')
            {
                if (contour != null && contour.edges.Count > 0)
                {
                    // Close the loop if the pen is not already back at the start.
                    if ((current - subpathStart).sqrMagnitude > 1e-12f)
                        contour.edges.Add(SDFAtlasShape.Edge.Line(current, subpathStart));

                    shape.contours.Add(contour);
                }
                contour = null;
                current = subpathStart;
                lastCommand = absolute;
                continue;
            }

            int stride = ArgumentStride(absolute);
            if (stride == 0)
            {
                Debug.LogWarning($"[SDFAtlas] '{sourceName}': unsupported path command '{command}'; skipped.");
                continue;
            }

            if (args.Length < stride)
            {
                if (args.Length > 0)
                {
                    Debug.LogWarning(
                        $"[SDFAtlas] '{sourceName}': command '{command}' has {args.Length} " +
                        $"argument(s), needs {stride}; skipped.");
                }
                continue;
            }

            for (int offset = 0; offset + stride <= args.Length; offset += stride)
            {
                switch (absolute)
                {
                    case 'M':
                    {
                        Vector2 target = Argument(args, offset, relative, current);

                        // A new M ends the previous subpath. An unclosed subpath still
                        // bounds area for filling purposes, so close it implicitly rather
                        // than discarding it.
                        if (contour != null && contour.edges.Count > 0)
                        {
                            if ((current - subpathStart).sqrMagnitude > 1e-12f)
                                contour.edges.Add(SDFAtlasShape.Edge.Line(current, subpathStart));
                            shape.contours.Add(contour);
                        }

                        contour = new SDFAtlasShape.Contour();
                        current = target;
                        subpathStart = target;

                        // Subsequent coordinate pairs after an M are implicit L commands,
                        // per the SVG spec.
                        absolute = 'L';
                        relative = char.IsLower(command);
                        break;
                    }

                    case 'L':
                    {
                        Vector2 target = Argument(args, offset, relative, current);
                        AddLine(ref contour, current, target);
                        current = target;
                        break;
                    }

                    case 'H':
                    {
                        float x = relative ? current.x + args[offset] : args[offset];
                        Vector2 target = new Vector2(x, current.y);
                        AddLine(ref contour, current, target);
                        current = target;
                        break;
                    }

                    case 'V':
                    {
                        float y = relative ? current.y + args[offset] : args[offset];
                        Vector2 target = new Vector2(current.x, y);
                        AddLine(ref contour, current, target);
                        current = target;
                        break;
                    }

                    case 'C':
                    {
                        Vector2 c0 = Argument(args, offset, relative, current);
                        Vector2 c1 = Argument(args, offset + 2, relative, current);
                        Vector2 target = Argument(args, offset + 4, relative, current);
                        AddCubic(ref contour, current, c0, c1, target);
                        lastControl = c1;
                        current = target;
                        break;
                    }

                    case 'S':
                    {
                        // Smooth cubic: first control point is the reflection of the
                        // previous curve's last control point about the current point.
                        Vector2 c0 = (lastCommand == 'C' || lastCommand == 'S')
                            ? current + (current - lastControl)
                            : current;
                        Vector2 c1 = Argument(args, offset, relative, current);
                        Vector2 target = Argument(args, offset + 2, relative, current);
                        AddCubic(ref contour, current, c0, c1, target);
                        lastControl = c1;
                        current = target;
                        break;
                    }

                    case 'Q':
                    {
                        Vector2 c = Argument(args, offset, relative, current);
                        Vector2 target = Argument(args, offset + 2, relative, current);
                        AddQuadratic(ref contour, current, c, target);
                        lastControl = c;
                        current = target;
                        break;
                    }

                    case 'T':
                    {
                        // Smooth quadratic: control point reflected from the previous one.
                        Vector2 c = (lastCommand == 'Q' || lastCommand == 'T')
                            ? current + (current - lastControl)
                            : current;
                        Vector2 target = Argument(args, offset, relative, current);
                        AddQuadratic(ref contour, current, c, target);
                        lastControl = c;
                        current = target;
                        break;
                    }

                    case 'A':
                    {
                        float rx = args[offset];
                        float ry = args[offset + 1];
                        float rotation = args[offset + 2];
                        bool largeArc = args[offset + 3] != 0f;
                        bool sweep = args[offset + 4] != 0f;
                        Vector2 target = Argument(args, offset + 5, relative, current);

                        AppendArc(ref contour, current, target, rx, ry, rotation, largeArc, sweep);
                        current = target;
                        break;
                    }
                }

                // Track the command actually executed, so S/T reflection sees 'L' rather
                // than 'M' after an implicit-lineto run.
                lastCommand = absolute;
            }
        }

        // Trailing subpath with no closing Z.
        if (contour != null && contour.edges.Count > 0)
        {
            if ((current - subpathStart).sqrMagnitude > 1e-12f)
                contour.edges.Add(SDFAtlasShape.Edge.Line(current, subpathStart));
            shape.contours.Add(contour);
        }
    }

    // Number of arguments each command consumes per repetition.
    static int ArgumentStride(char absoluteCommand)
    {
        switch (absoluteCommand)
        {
            case 'M':
            case 'L':
            case 'T': return 2;
            case 'H':
            case 'V': return 1;
            case 'C': return 6;
            case 'S':
            case 'Q': return 4;
            case 'A': return 7;
            default: return 0;
        }
    }

    static Vector2 Argument(float[] args, int index, bool relative, Vector2 current)
    {
        var value = new Vector2(args[index], args[index + 1]);
        return relative ? current + value : value;
    }

    // Edge appenders. Each guards against a null contour, which happens when path data
    // begins with a drawing command before any M. Malformed, but survivable by treating
    // the current point as the subpath start.
    static void AddLine(ref SDFAtlasShape.Contour contour, Vector2 from, Vector2 to)
    {
        if (contour == null) contour = new SDFAtlasShape.Contour();
        if ((to - from).sqrMagnitude <= 1e-12f) return;   // zero-length edge helps nobody
        contour.edges.Add(SDFAtlasShape.Edge.Line(from, to));
    }

    static void AddQuadratic(ref SDFAtlasShape.Contour contour, Vector2 from, Vector2 c, Vector2 to)
    {
        if (contour == null) contour = new SDFAtlasShape.Contour();
        contour.edges.Add(SDFAtlasShape.Edge.Quadratic(from, c, to));
    }

    static void AddCubic(ref SDFAtlasShape.Contour contour, Vector2 from, Vector2 c0, Vector2 c1, Vector2 to)
    {
        if (contour == null) contour = new SDFAtlasShape.Contour();
        contour.edges.Add(SDFAtlasShape.Edge.Cubic(from, c0, c1, to));
    }

    // --- Elliptical arcs ---------------------------------------------------

    // Converts an SVG elliptical arc into cubic Beziers and appends them.
    //
    // SVG specifies arcs endpoint-style (where it ends, plus radii and flags), while Beziers
    // need centre-style parameters (centre, start angle, sweep). The conversion is the
    // procedure from the SVG specification's implementation notes, followed by splitting the
    // sweep into segments of at most 90 degrees. A cubic approximates a quarter ellipse to
    // well under a thousandth of its radius, far below one output texel, but degrades
    // quickly beyond that.
    static void AppendArc(ref SDFAtlasShape.Contour contour, Vector2 from, Vector2 to,
                          float rx, float ry, float rotationDegrees, bool largeArc, bool sweep)
    {
        if (contour == null) contour = new SDFAtlasShape.Contour();

        // Degenerate radii mean a straight line, per the spec.
        rx = Mathf.Abs(rx);
        ry = Mathf.Abs(ry);
        if (rx < 1e-9f || ry < 1e-9f || (to - from).sqrMagnitude <= 1e-12f)
        {
            AddLine(ref contour, from, to);
            return;
        }

        float phi = rotationDegrees * Mathf.Deg2Rad;
        float cosPhi = Mathf.Cos(phi);
        float sinPhi = Mathf.Sin(phi);

        // Step 1: transform into the ellipse's own frame, where it is a unit-axis ellipse.
        Vector2 half = (from - to) * 0.5f;
        float x1 = cosPhi * half.x + sinPhi * half.y;
        float y1 = -sinPhi * half.x + cosPhi * half.y;

        // Step 2: scale up radii that are too small to span the endpoints. The spec requires
        // this rather than treating it as an error.
        float lambda = (x1 * x1) / (rx * rx) + (y1 * y1) / (ry * ry);
        if (lambda > 1f)
        {
            float scale = Mathf.Sqrt(lambda);
            rx *= scale;
            ry *= scale;
        }

        // Step 3: find the centre in the ellipse frame.
        float rxSq = rx * rx;
        float rySq = ry * ry;
        float x1Sq = x1 * x1;
        float y1Sq = y1 * y1;

        float numerator = rxSq * rySq - rxSq * y1Sq - rySq * x1Sq;
        float denominator = rxSq * y1Sq + rySq * x1Sq;
        float factor = denominator > 0f ? Mathf.Sqrt(Mathf.Max(numerator / denominator, 0f)) : 0f;

        if (largeArc == sweep) factor = -factor;

        float cx1 = factor * rx * y1 / ry;
        float cy1 = -factor * ry * x1 / rx;

        // Step 4: back to the original frame.
        Vector2 mid = (from + to) * 0.5f;
        var centre = new Vector2(
            cosPhi * cx1 - sinPhi * cy1 + mid.x,
            sinPhi * cx1 + cosPhi * cy1 + mid.y);

        // Step 5: start angle and sweep.
        float startAngle = Angle(1f, 0f, (x1 - cx1) / rx, (y1 - cy1) / ry);
        float deltaAngle = Angle((x1 - cx1) / rx, (y1 - cy1) / ry,
                                 (-x1 - cx1) / rx, (-y1 - cy1) / ry);

        if (!sweep && deltaAngle > 0f) deltaAngle -= 2f * Mathf.PI;
        else if (sweep && deltaAngle < 0f) deltaAngle += 2f * Mathf.PI;

        // Step 6: emit cubic segments, each covering at most a quarter turn.
        int segments = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(deltaAngle) / (Mathf.PI * 0.5f)));
        float segmentAngle = deltaAngle / segments;

        // Control point distance for a cubic approximating a circular arc of this angle.
        float alpha = 4f / 3f * Mathf.Tan(segmentAngle * 0.25f);

        Vector2 segmentStart = from;

        for (int i = 0; i < segments; i++)
        {
            float theta0 = startAngle + i * segmentAngle;
            float theta1 = theta0 + segmentAngle;

            Vector2 end = EllipsePoint(centre, rx, ry, cosPhi, sinPhi, theta1);

            Vector2 derivative0 = EllipseDerivative(rx, ry, cosPhi, sinPhi, theta0);
            Vector2 derivative1 = EllipseDerivative(rx, ry, cosPhi, sinPhi, theta1);

            Vector2 c0 = segmentStart + alpha * derivative0;
            Vector2 c1 = end - alpha * derivative1;

            contour.edges.Add(SDFAtlasShape.Edge.Cubic(segmentStart, c0, c1, end));
            segmentStart = end;
        }
    }

    static Vector2 EllipsePoint(Vector2 centre, float rx, float ry, float cosPhi, float sinPhi, float theta)
    {
        float cosT = Mathf.Cos(theta);
        float sinT = Mathf.Sin(theta);
        return new Vector2(
            centre.x + rx * cosT * cosPhi - ry * sinT * sinPhi,
            centre.y + rx * cosT * sinPhi + ry * sinT * cosPhi);
    }

    static Vector2 EllipseDerivative(float rx, float ry, float cosPhi, float sinPhi, float theta)
    {
        float cosT = Mathf.Cos(theta);
        float sinT = Mathf.Sin(theta);
        return new Vector2(
            -rx * sinT * cosPhi - ry * cosT * sinPhi,
            -rx * sinT * sinPhi + ry * cosT * cosPhi);
    }

    // Signed angle between two vectors, in the range -PI..PI.
    static float Angle(float ux, float uy, float vx, float vy)
    {
        float dot = ux * vx + uy * vy;
        float lengths = Mathf.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        if (lengths < 1e-12f) return 0f;

        float cosine = Mathf.Clamp(dot / lengths, -1f, 1f);
        float angle = Mathf.Acos(cosine);

        // Sign from the 2D cross product.
        if (ux * vy - uy * vx < 0f) angle = -angle;
        return angle;
    }

    // --- Number parsing ----------------------------------------------------

    static float[] ParseNumbers(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new float[0];

        MatchCollection matches = NumberPattern.Matches(text);
        var values = new float[matches.Count];

        for (int i = 0; i < matches.Count; i++)
        {
            // Invariant culture matters: a machine with comma decimal separators would
            // otherwise misparse "0.5" as 5.
            float.TryParse(matches[i].Value, NumberStyles.Float,
                           CultureInfo.InvariantCulture, out values[i]);
        }
        return values;
    }

    // Parses an SVG length, ignoring any unit suffix.
    static float ParseLength(string text)
    {
        Match m = NumberPattern.Match(text);
        if (!m.Success) return 0f;

        float.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value);
        return value;
    }
}
