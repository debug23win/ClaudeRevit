using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// "Almost orthogonal" geometry: a grid at 0.003°, a wall at 89.98°. It looks right on screen, but
// dimensions read 2999.97, walls refuse to join cleanly, and everything placed relative to it
// inherits the error. The idea comes from the off-axis toolkit in chris4d's fork of
// mcp-servers-for-revit (MIT); this is a native implementation.
internal static class OffAxisGeometry
{
    public sealed record Item(ElementId Id, string Kind, XYZ Start, XYZ End, double AngleDeg, double DeviationDeg, double CorrectionDeg);

    // Deviation from the nearest multiple of `step` degrees (90 by default, 45 for diagonals).
    public static (double Deviation, double Correction) Measure(XYZ a, XYZ b, double step)
    {
        var angle = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
        var r = ((angle % step) + step) % step;
        return r <= step / 2 ? (r, -r) : (step - r, step - r);
    }

    public static IEnumerable<Item> Collect(Document doc, IEnumerable<string> kinds, View? view, double step)
    {
        FilteredElementCollector Col() => view != null ? new FilteredElementCollector(doc, view.Id) : new FilteredElementCollector(doc);
        var want = kinds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<(Element E, string Kind, XYZ? A, XYZ? B)> Source()
        {
            if (want.Contains("grids"))
                foreach (var g in Col().OfClass(typeof(Grid)).Cast<Grid>())
                    if (g.Curve is Line l) yield return (g, "grid", l.GetEndPoint(0), l.GetEndPoint(1));
            if (want.Contains("walls"))
                foreach (var w in Col().OfClass(typeof(Wall)).Cast<Wall>())
                    if (w.Location is LocationCurve { Curve: Line l }) yield return (w, "wall", l.GetEndPoint(0), l.GetEndPoint(1));
            if (want.Contains("beams"))
                foreach (var f in Col().OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType())
                    if (f.Location is LocationCurve { Curve: Line l }) yield return (f, "beam", l.GetEndPoint(0), l.GetEndPoint(1));
            if (want.Contains("reference_planes"))
                foreach (var p in Col().OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>())
                    yield return (p, "reference_plane", p.BubbleEnd, p.FreeEnd);
            if (want.Contains("lines"))
                foreach (var c in Col().OfClass(typeof(CurveElement)).Cast<CurveElement>())
                    if (c.GeometryCurve is Line l && c is ModelCurve or DetailCurve) yield return (c, c is DetailCurve ? "detail_line" : "model_line", l.GetEndPoint(0), l.GetEndPoint(1));
        }
        foreach (var (e, kind, a, b) in Source())
        {
            ToolContext.ThrowIfCancelled();
            if (a == null || b == null || a.DistanceTo(b) < 1e-6) continue;
            // Vertical lines (sloped beams seen in plan as a point) have no plan direction.
            if (Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y) < 1e-9) continue;
            var (dev, corr) = Measure(a, b, step);
            var angle = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
            yield return new Item(e.Id, kind, a, b, angle, dev, corr);
        }
    }

    public static readonly string[] AllKinds = ["grids", "walls", "beams", "reference_planes", "lines"];

    public static string[] Kinds(IReadOnlyDictionary<string, JsonElement> input) =>
        input.TryGetValue("kinds", out var k) && k.ValueKind == JsonValueKind.Array && k.GetArrayLength() > 0
            ? k.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
            : AllKinds;

    // The orientation to measure against: plain project axes by default, or the angle of a chosen
    // grid when the building is deliberately rotated.
    public static double ReferenceAngle(Document doc, IReadOnlyDictionary<string, JsonElement> input)
    {
        if (!input.TryGetValue("reference_grid_id", out var g) || g.ValueKind != JsonValueKind.Number) return 0;
        var grid = NativeToolUtil.Element(doc, g.GetInt64()) as Grid ?? throw new ToolInputException("reference_grid_id is not a grid.");
        if (grid.Curve is not Line l) throw new ToolInputException("The reference grid is not straight.");
        var a = l.GetEndPoint(0); var b = l.GetEndPoint(1);
        return Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
    }

    // Measuring in a rotated frame: rotate the points by -reference about the origin.
    public static IEnumerable<Item> Relative(IEnumerable<Item> items, double referenceDeg, double step)
    {
        if (referenceDeg == 0) { foreach (var i in items) yield return i; yield break; }
        var t = Transform.CreateRotation(XYZ.BasisZ, -referenceDeg * Math.PI / 180);
        foreach (var i in items)
        {
            var (dev, corr) = Measure(t.OfPoint(i.Start), t.OfPoint(i.End), step);
            yield return i with { DeviationDeg = dev, CorrectionDeg = corr };
        }
    }
}

public sealed class DetectOffAxis : IRevitTool
{
    public string Name => "detect_off_axis";
    public string Description =>
        "Find grids, walls, beams, reference planes and model/detail lines that are ALMOST but not exactly orthogonal " +
        "(e.g. 0.003° off) — the source of dimensions like 2999.97, walls that won't join and inherited skew. " +
        "Read-only. Measures against project axes, or against reference_grid_id for a rotated building. " +
        "Then use fix_off_axis on the reported ids.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["kinds"] = NativeToolUtil.Array("string", "Any of grids, walls, beams, reference_planes, lines. Default all."),
        ["max_deviation_deg"] = NativeToolUtil.Field("number", "Report deviations up to this many degrees (default 1). Larger angles are taken as intentional."),
        ["min_deviation_deg"] = NativeToolUtil.Field("number", "Ignore deviations below this (default 1e-6, numerical noise)."),
        ["step_deg"] = NativeToolUtil.Field("number", "90 (default) checks orthogonality; 45 also accepts exact diagonals."),
        ["reference_grid_id"] = NativeToolUtil.Field("integer", "Optional grid whose direction is 'straight' for a rotated building."),
        ["active_view_only"] = NativeToolUtil.Field("boolean", "Only elements visible in the active view (default false)."),
        ["limit"] = NativeToolUtil.Field("integer", "Max items listed (default 200; the count is always exact).")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var max = ToolInput.OptionalDouble(input, "max_deviation_deg") ?? 1.0;
        var min = ToolInput.OptionalDouble(input, "min_deviation_deg") ?? 1e-6;
        var step = ToolInput.OptionalDouble(input, "step_deg") ?? 90;
        if (step is not (90 or 45)) throw new ToolInputException("step_deg must be 90 or 45.");
        var limit = Math.Clamp(ToolInput.OptionalInt(input, "limit") ?? 200, 1, 2000);
        var view = ToolInput.Flag(input, "active_view_only") ? ToolContext.UiDocument(app)?.ActiveView : null;
        var reference = OffAxisGeometry.ReferenceAngle(doc, input);
        var items = OffAxisGeometry.Relative(OffAxisGeometry.Collect(doc, OffAxisGeometry.Kinds(input), view, step), reference, step)
            .Where(i => i.DeviationDeg > min && i.DeviationDeg <= max)
            .OrderByDescending(i => i.DeviationDeg).ToList();
        return Services.Json.Serialize(new
        {
            count = items.Count,
            reference_angle_deg = reference,
            by_kind = items.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count()),
            items = items.Take(limit).Select(i => new
            {
                id = i.Id.Value, kind = i.Kind, angle_deg = Math.Round(i.AngleDeg, 6),
                deviation_deg = i.DeviationDeg, correction_deg = i.CorrectionDeg,
                length_mm = Math.Round(i.Start.DistanceTo(i.End) * Units.MmPerFoot, 1)
            }),
            truncated = items.Count > limit
        });
    }
}

public sealed class FixOffAxis : IRevitTool
{
    public string Name => "fix_off_axis";
    public string Description =>
        "Rotate almost-orthogonal grids/walls/beams/reference planes/lines onto the exact axis, each about its own start " +
        "point. preview defaults true (applies and rolls back, reporting what would move and Revit's warnings); send " +
        "preview=false to apply. Pinned elements are skipped. Only corrections up to max_deviation_deg are applied.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public bool RequiresConfirmation => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["element_ids"] = NativeToolUtil.Array("integer", "Ids from detect_off_axis."),
        ["max_deviation_deg"] = NativeToolUtil.Field("number", "Safety limit: refuse to rotate anything further off than this (default 1)."),
        ["step_deg"] = NativeToolUtil.Field("number", "90 (default) or 45."),
        ["reference_grid_id"] = NativeToolUtil.Field("integer", "Same reference as used for detection, for a rotated building."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "element_ids");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var preview = NativeToolUtil.Preview(input);
        var max = ToolInput.OptionalDouble(input, "max_deviation_deg") ?? 1.0;
        var step = ToolInput.OptionalDouble(input, "step_deg") ?? 90;
        if (step is not (90 or 45)) throw new ToolInputException("step_deg must be 90 or 45.");
        var wanted = NativeToolUtil.Ids(ToolInput.Required(input, "element_ids"), 5000).Select(i => i.Value).ToHashSet();
        var reference = OffAxisGeometry.ReferenceAngle(doc, input);
        var items = OffAxisGeometry.Relative(OffAxisGeometry.Collect(doc, OffAxisGeometry.AllKinds, null, step), reference, step)
            .Where(i => wanted.Contains(i.Id.Value)).ToList();
        var found = items.Select(i => i.Id.Value).ToHashSet();
        var skipped = wanted.Where(id => !found.Contains(id)).Select(id => (object)new { id, reason = "not a straight grid/wall/beam/reference plane/line" }).ToList();
        var (moved, warnings) = NativeToolUtil.Commit(doc, "Claude: fix off-axis", preview, () =>
        {
            var done = new List<object>();
            foreach (var i in items)
            {
                ToolContext.ThrowIfCancelled();
                if (i.DeviationDeg == 0) { skipped.Add(new { id = i.Id.Value, reason = "already exact" }); continue; }
                if (i.DeviationDeg > max) { skipped.Add(new { id = i.Id.Value, reason = $"{i.DeviationDeg:0.###}° exceeds max_deviation_deg" }); continue; }
                var e = doc.GetElement(i.Id);
                if (e.Pinned) { skipped.Add(new { id = i.Id.Value, reason = "pinned" }); continue; }
                var axis = Line.CreateBound(i.Start, i.Start + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, i.Id, axis, i.CorrectionDeg * Math.PI / 180);
                done.Add(new { id = i.Id.Value, kind = i.Kind, rotated_deg = i.CorrectionDeg });
            }
            return done;
        });
        return Services.Json.Serialize(new { preview, moved_count = moved.Count, moved, skipped, revit_warnings = warnings });
    }
}
