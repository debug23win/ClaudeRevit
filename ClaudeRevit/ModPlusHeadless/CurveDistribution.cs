#nullable disable
// Spacing/parameter traversal adapted from ModPlus mprCopyAlongCurve.Divide.
// Original algorithm copyright: ModPlus. Input checks and JSON/API facade added.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace ModPlus.Headless;

internal static class CurveDistribution
{
    internal static IList<Transform> Divide(Document doc, JsonElement args, double tolerance)
    {
        using CurveLoop loop = Path(doc, args, tolerance);
        double spacing = args.GetProperty("spacing_mm").GetDouble() / 304.8;
        if (!double.IsFinite(spacing) || spacing <= tolerance) throw new ArgumentException("spacing_mm must exceed Revit's short curve tolerance.");
        if (loop.GetExactLength() / spacing > 10000) throw new ArgumentException("The path would produce more than 10000 positions. Increase spacing.");
        bool includeEnd = !args.TryGetProperty("include_end", out JsonElement ie) || ie.GetBoolean();
        double? projection = args.TryGetProperty("projection_parameter", out JsonElement projectionValue) ? projectionValue.GetDouble() : null;
        Curve first = loop.First();
        if (projection.HasValue && (!double.IsFinite(projection.Value) || projection.Value < first.GetEndParameter(0) || projection.Value > first.GetEndParameter(1))) throw new ArgumentException("projection_parameter must lie on the first bounded curve.");
        var result = new List<Transform>();
        foreach (Transform transform in OriginalDivide(loop, spacing, includeEnd, projection))
        {
            if (result.Count >= 10001) throw new ArgumentException("The path exceeds the 10000-position limit.");
            result.Add(transform);
        }
        // Remove only a closed path's final duplicate, preserving intersections
        // and deliberate repeated positions elsewhere along a path.
        bool closed = loop.Last().GetEndPoint(1).DistanceTo(loop.First().GetEndPoint(0)) < tolerance;
        if (closed && result.Count > 1 && result[result.Count - 1].Origin.DistanceTo(result[0].Origin) < tolerance) result.RemoveAt(result.Count - 1);
        if (result.Count > 10000) throw new ArgumentException("The path exceeds the 10000-position limit.");
        return result;
    }
    internal static object Copy(Document doc, JsonElement args, double tolerance)
    {
        long[] raw = args.GetProperty("element_ids").EnumerateArray().Select(id => id.GetInt64()).Distinct().ToArray();
        if (raw.Length < 1 || raw.Length > 1000 || raw.Any(id => doc.GetElement(new ElementId(id)) == null)) throw new ArgumentException("Supply 1..1000 valid element IDs.");
        IList<Transform> positions = Divide(doc, args, tolerance);
        if (positions.Count * raw.Length > 10000) throw new ArgumentException("The requested copies exceed 10000 source-position pairs.");
        bool rotate = args.TryGetProperty("rotate", out JsonElement rotation) && rotation.GetBoolean();
        using CurveLoop loop = Path(doc, args, tolerance);
        XYZ normal = loop.HasPlane() ? loop.GetPlane().Normal : doc.ActiveView.ViewDirection;
        var copies = new List<object>();
        List<ElementId> source = raw.Select(id => new ElementId(id)).ToList();
        foreach (Transform position in positions.Skip(1))
        {
            ICollection<ElementId> copied = ElementTransformUtils.CopyElements(doc, source, position.Origin - positions[0].Origin);
            if (rotate)
            {
                double angle = positions[0].BasisX.AngleOnPlaneTo(position.BasisX, normal);
                using Line axis = Line.CreateBound(position.Origin, position.Origin + normal);
                ElementTransformUtils.RotateElements(doc, copied, axis, angle);
            }
            copies.Add(new { elementIds = copied.Select(id => id.Value).ToArray(), originFt = new[] { position.Origin.X, position.Origin.Y, position.Origin.Z } });
        }
        return new { sourceIds = raw, copies, semantics = "Sources represent the first path position; their initial offset is preserved. Copied groups remain groups." };
    }
    private static CurveLoop Path(Document doc, JsonElement args, double tolerance)
    {
        bool hasIds = args.TryGetProperty("curve_element_ids", out JsonElement curveIds), hasPolyline = args.TryGetProperty("polyline_mm", out JsonElement polyline);
        if (hasIds == hasPolyline) throw new ArgumentException("Choose exactly one path: ordered curve_element_ids or polyline_mm.");
        var curves = new List<Curve>();
        if (hasIds)
        {
            foreach (JsonElement id in curveIds.EnumerateArray())
            {
                if (curves.Count == 1000) throw new ArgumentException("The path exceeds 1000 curves.");
                CurveElement element = doc.GetElement(new ElementId(id.GetInt64())) as CurveElement ?? throw new ArgumentException("Supply CurveElement IDs in contiguous order.");
                curves.Add(element.GeometryCurve);
            }
        }
        else
        {
            JsonElement[] input = polyline.EnumerateArray().ToArray();
            if (input.Length < 2 || input.Length > 1001) throw new ArgumentException("polyline_mm needs 2..1001 points.");
            XYZ[] points = input.Select(p =>
            {
                double[] numbers = p.EnumerateArray().Select(n => n.GetDouble()).ToArray();
                if (numbers.Length != 3 || numbers.Any(n => !double.IsFinite(n))) throw new ArgumentException("Each point needs three finite millimeter coordinates.");
                return new XYZ(numbers[0] / 304.8, numbers[1] / 304.8, numbers[2] / 304.8);
            }).ToArray();
            for (int i = 1; i < points.Length; i++)
            {
                if (points[i - 1].DistanceTo(points[i]) <= tolerance) throw new ArgumentException("A polyline segment is shorter than the model tolerance.");
                curves.Add(Line.CreateBound(points[i - 1], points[i]));
            }
        }
        if (curves.Count == 0 || curves.Any(c => !c.IsBound || c.Length <= tolerance)) throw new ArgumentException("The path must contain bounded curves longer than the model tolerance.");
        return CurveLoop.Create(curves);
    }
    private static IEnumerable<Transform> OriginalDivide(CurveLoop loop, double spacing, bool includeEnd, double? projection)
    {
        decimal length = (decimal)loop.GetExactLength(), interval = (decimal)spacing;
        Curve first = loop.First(), last = loop.Last();
        double startRaw = projection ?? first.GetEndParameter(0);
        decimal indent = (decimal)(first.Length * first.ComputeNormalizedParameter(startRaw));
        if (length - indent <= interval)
        {
            yield return first.ComputeDerivatives(startRaw, false);
            if (includeEnd) yield return last.ComputeDerivatives(last.GetEndParameter(1), false);
            yield break;
        }
        decimal remain = 0m; bool isFirst = true;
        foreach (Curve curve in loop)
        {
            decimal rawStart = (decimal)curve.GetEndParameter(0), start = isFirst ? (decimal)startRaw : rawStart;
            decimal curveLength = (decimal)curve.Length;
            if (curveLength < interval - remain)
            {
                if (isFirst) { yield return curve.ComputeDerivatives((double)start, false); isFirst = false; }
                remain += curveLength; continue;
            }
            decimal parameter = start, end = (decimal)curve.GetEndParameter(1), factor = (end - rawStart) / curveLength;
            decimal parameterSpacing = factor * interval;
            if (remain > 0m) parameter += parameterSpacing - factor * remain;
            do { yield return curve.ComputeDerivatives((double)parameter, false); parameter += parameterSpacing; } while (parameter <= end);
            remain = (end - (parameter - parameterSpacing)) / factor; isFirst = false;
        }
        if (includeEnd && Math.Round(remain, 4) > 0m) yield return last.ComputeDerivatives(last.GetEndParameter(1), false);
    }
}
