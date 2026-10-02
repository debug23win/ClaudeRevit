using System.Text.Json;
using Autodesk.Revit.DB;

namespace ClaudeRevit.Tools;

internal static class NativeCurveInput
{
    // Either vertices (polyline) or explicit {start_mm,end_mm,mid_mm?} segments.
    public static List<Curve> Read(JsonElement input, bool closed)
    {
        var entries = input.EnumerateArray().ToArray();
        if (entries.Length is < 1 or > 500) throw new ToolInputException("Curve input requires 1..500 entries.");
        var curves = new List<Curve>();
        if (entries[0].ValueKind == JsonValueKind.Array)
        {
            var pts = entries.Select(p => NativeToolUtil.Point(p)).ToArray();
            if (pts.Length < (closed ? 3 : 2)) throw new ToolInputException("Insufficient polyline vertices.");
            for (var i = 0; i < pts.Length - 1; i++) curves.Add(Line.CreateBound(pts[i], pts[i + 1]));
            if (closed && !pts[^1].IsAlmostEqualTo(pts[0])) curves.Add(Line.CreateBound(pts[^1], pts[0]));
        }
        else
            foreach (var entry in entries)
            {
                var start = NativeToolUtil.Point(entry.GetProperty("start_mm"));
                var end = NativeToolUtil.Point(entry.GetProperty("end_mm"));
                curves.Add(entry.TryGetProperty("mid_mm", out var mid) ? Arc.Create(start, end, NativeToolUtil.Point(mid)) : Line.CreateBound(start, end));
            }
        for (var i = 1; i < curves.Count; i++)
            if (curves[i - 1].GetEndPoint(1).DistanceTo(curves[i].GetEndPoint(0)) > 1e-7) throw new ToolInputException("Curve segments must be contiguous and ordered.");
        if (closed && curves[^1].GetEndPoint(1).DistanceTo(curves[0].GetEndPoint(0)) > 1e-7) throw new ToolInputException("Profile must be closed.");
        return curves;
    }
    public static CurveArray Array(IEnumerable<Curve> curves) { var result = new CurveArray(); foreach (var c in curves) result.Append(c); return result; }
    public static CurveArrArray Loops(IEnumerable<Curve> curves) { var result = new CurveArrArray(); result.Append(Array(curves)); return result; }
    public static Plane Plane(IReadOnlyDictionary<string, JsonElement> input)
    {
        var origin = input.TryGetValue("plane_origin_mm", out var o) ? NativeToolUtil.Point(o) : XYZ.Zero;
        var normal = input.TryGetValue("plane_normal", out var n) ? NativeToolUtil.Point(n, false).Normalize() : XYZ.BasisZ;
        var x = input.TryGetValue("plane_x_axis", out var axis) ? NativeToolUtil.Point(axis, false).Normalize() :
            Math.Abs(normal.DotProduct(XYZ.BasisX)) < 1e-7 ? XYZ.BasisX : ReinforcementHelpers.PerpendicularTo(normal);
        if (Math.Abs(x.DotProduct(normal)) > 1e-7) throw new ToolInputException("plane_x_axis must be perpendicular to plane_normal.");
        return Autodesk.Revit.DB.Plane.CreateByOriginAndBasis(origin, x, normal.CrossProduct(x).Normalize());
    }
    public static void InPlane(IEnumerable<Curve> curves, Plane plane)
    {
        foreach (var curve in curves)
            foreach (var point in curve.Tessellate())
                if (Math.Abs((point - plane.Origin).DotProduct(plane.Normal)) > 1e-6) throw new ToolInputException("Profile/path is not in the supplied sketch plane.");
    }
}
