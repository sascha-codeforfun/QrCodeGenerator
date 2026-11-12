using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace QrCodeGenerator.Rendering;

/// <summary>Small SVG text helpers: invariant number formatting, dimensions, path data.</summary>
internal static class Svg
{
    public static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static double ParseNum(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>Reads width/height (or the viewBox size) from the root svg element.</summary>
    public static (double width, double height) ReadDimensions(string svg)
    {
        Match wM = Regex.Match(svg, "\\bwidth=\"([\\d.]+)(?:px)?\"", RegexOptions.IgnoreCase);
        Match hM = Regex.Match(svg, "\\bheight=\"([\\d.]+)(?:px)?\"", RegexOptions.IgnoreCase);
        if (wM.Success && hM.Success)
            return (ParseNum(wM.Groups[1].Value), ParseNum(hM.Groups[1].Value));

        Match vb = Regex.Match(
            svg, "viewBox=\"[\\d.]+\\s+[\\d.]+\\s+([\\d.]+)\\s+([\\d.]+)\"",
            RegexOptions.IgnoreCase);
        if (vb.Success)
            return (ParseNum(vb.Groups[1].Value), ParseNum(vb.Groups[2].Value));

        throw new InvalidOperationException("Could not determine SVG dimensions for logo placement.");
    }

    /// <summary>Inserts markup just before the closing &lt;/svg&gt; tag.</summary>
    public static string InsertBeforeClose(string svg, string markup, string what)
    {
        int closeIdx = svg.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
        if (closeIdx < 0)
            throw new InvalidOperationException($"Could not parse the generated SVG to insert the {what}.");

        return svg.Substring(0, closeIdx) + markup + svg.Substring(closeIdx);
    }

    /// <summary>Converts a PathGeometry to an SVG path 'd' string, preserving curves.</summary>
    public static string PathData(PathGeometry geometry)
    {
        var sb = new StringBuilder();

        foreach (PathFigure figure in geometry.Figures)
        {
            sb.Append('M').Append(Pt(figure.StartPoint));

            foreach (PathSegment segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment ls:
                        sb.Append('L').Append(Pt(ls.Point));
                        break;
                    case PolyLineSegment pls:
                        foreach (Point p in pls.Points) sb.Append('L').Append(Pt(p));
                        break;
                    case BezierSegment bs:
                        sb.Append('C').Append(Pt(bs.Point1)).Append(Pt(bs.Point2)).Append(Pt(bs.Point3));
                        break;
                    case PolyBezierSegment pbs:
                        for (int i = 0; i + 2 < pbs.Points.Count; i += 3)
                            sb.Append('C').Append(Pt(pbs.Points[i])).Append(Pt(pbs.Points[i + 1])).Append(Pt(pbs.Points[i + 2]));
                        break;
                    case QuadraticBezierSegment qs:
                        sb.Append('Q').Append(Pt(qs.Point1)).Append(Pt(qs.Point2));
                        break;
                    case PolyQuadraticBezierSegment pqs:
                        for (int i = 0; i + 1 < pqs.Points.Count; i += 2)
                            sb.Append('Q').Append(Pt(pqs.Points[i])).Append(Pt(pqs.Points[i + 1]));
                        break;
                    case ArcSegment arc:
                        sb.Append('A')
                          .Append(Num(arc.Size.Width)).Append(' ').Append(Num(arc.Size.Height)).Append(' ')
                          .Append(Num(arc.RotationAngle)).Append(' ')
                          .Append(arc.IsLargeArc ? '1' : '0').Append(' ')
                          .Append(arc.SweepDirection == SweepDirection.Clockwise ? '1' : '0').Append(' ')
                          .Append(Pt(arc.Point));
                        break;
                }
            }

            if (figure.IsClosed) sb.Append('Z');
        }

        return sb.ToString();
    }

    private static string Pt(Point p) => Num(p.X) + " " + Num(p.Y) + " ";
}
