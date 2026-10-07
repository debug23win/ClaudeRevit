using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// Reinforcement quality: bars that actually sit in concrete, with the cover and clear spacing the
// code asks for, and stirrups laid out in support/span zones. Ideas from the HorizunGroup
// horizun-revit-mcp (post-commit containment check, stirrup zones; Apache-2.0) and okuno-dsi
// revit-mcp-toolkit (rebar spacing check; Apache-2.0); implementations are native.
internal static class RebarGeometry
{
    public static readonly BuiltInCategory[] ConcreteHosts =
    [
        BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFoundation,
        BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Stairs, BuiltInCategory.OST_GenericModel
    ];

    public static List<Solid> Solids(Element e)
    {
        var result = new List<Solid>();
        void Walk(GeometryElement? g, int depth)
        {
            if (g == null || depth > 6) return;
            foreach (var o in g)
                if (o is Solid s && s.Volume > 1e-9) result.Add(s);
                else if (o is GeometryInstance gi) Walk(gi.GetInstanceGeometry(), depth + 1);
        }
        Walk(e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }), 0);
        return result;
    }

    public static double InsideLength(IEnumerable<Solid> solids, Curve curve)
    {
        double inside = 0;
        var options = new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside };
        foreach (var s in solids)
        {
            try
            {
                var r = s.IntersectWithCurve(curve, options);
                for (int i = 0; i < r.SegmentCount; i++) inside += r.GetCurveSegment(i).Length;
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
        }
        return Math.Min(inside, curve.Length);
    }

    public static bool PointInside(IEnumerable<Solid> solids, XYZ p)
    {
        var probe = Line.CreateBound(p, p + XYZ.BasisZ * (0.5 / Units.MmPerFoot));
        return InsideLength(solids, probe) > 1e-6;
    }

    // Clear cover at a point: distance to the nearest face of the host that is an outer surface
    // of the concrete (a face against an adjoining concrete element is an interface, not cover).
    public static double? CoverAt(List<Face> hostFaces, List<Solid> concrete, XYZ p)
    {
        double best = double.MaxValue;
        foreach (var f in hostFaces)
        {
            var hit = f.Project(p);
            if (hit == null || hit.Distance >= best) continue;
            var n = f.ComputeNormal(hit.UVPoint);
            if (PointInside(concrete, hit.XYZPoint + n * (2 / Units.MmPerFoot))) continue;
            best = hit.Distance;
        }
        return best == double.MaxValue ? null : best;
    }

    public static IList<Curve> Centerline(Rebar bar, int position) =>
        bar.GetTransformedCenterlineCurves(false, false, false, MultiplanarOption.IncludeAllMultiplanarCurves, position);

    public static double Diameter(Document doc, Rebar bar) =>
        (doc.GetElement(bar.GetTypeId()) as RebarBarType)?.BarModelDiameter ?? 0;

    public static IEnumerable<XYZ> Samples(IEnumerable<Curve> curves, double step, int maxPerCurve = 40)
    {
        foreach (var c in curves)
        {
            var n = Math.Clamp((int)Math.Ceiling(c.Length / step), 1, maxPerCurve);
            for (int i = 0; i <= n; i++) yield return c.Evaluate((double)i / n, true);
        }
    }

    public static List<Solid> NearbyConcrete(Document doc, Element host, BoundingBoxXYZ box)
    {
        var outline = new Outline(box.Min - new XYZ(1, 1, 1), box.Max + new XYZ(1, 1, 1));
        return new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(ConcreteHosts)).WhereElementIsNotElementType()
            .WherePasses(new BoundingBoxIntersectsFilter(outline)).Where(e => e.Id == host.Id || RebarHostData.GetRebarHostData(e)?.IsValidHost() == true)
            .SelectMany(Solids).ToList();
    }

    public static double RequiredCover(Document doc, Element host)
    {
        var data = RebarHostData.GetRebarHostData(host);
        var covers = new[] { BuiltInParameter.CLEAR_COVER_TOP, BuiltInParameter.CLEAR_COVER_BOTTOM, BuiltInParameter.CLEAR_COVER_OTHER }
            .Select(p => host.get_Parameter(p)?.AsElementId()).Where(id => id != null && id != ElementId.InvalidElementId)
            .Select(id => (doc.GetElement(id!) as RebarCoverType)?.CoverDistance ?? 0).Where(d => d > 0).ToList();
        return covers.Count > 0 ? covers.Min() : 0;
    }
}

public sealed class AuditRebar : IRevitTool
{
    public string Name => "audit_rebar";
    public string Description =>
        "Check modelled reinforcement against its concrete: (1) containment — every bar's centre line lies in concrete; " +
        "a part outside its host but inside an adjoining element (anchorage into a column) is reported separately from " +
        "a part in the air; (2) cover — clear cover to the outer concrete surface vs the host's rebar cover settings " +
        "or min_cover_mm; (3) clear spacing between bars of a set vs max(d, 25 mm) (СП 63.13330.2018 п. 10.3.5) or " +
        "min_clear_spacing_mm, and max_spacing_mm if given; (4) bar–bar intersections between different rebar sets of a " +
        "host. Scope: host_ids or rebar_ids. Read-only; a check that could not be measured is reported as not checked, " +
        "never as passed.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["host_ids"] = NativeToolUtil.Array("integer", "Hosts whose rebar to check."),
        ["rebar_ids"] = NativeToolUtil.Array("integer", "Rebar elements to check."),
        ["checks"] = NativeToolUtil.Array("string", "containment, cover, spacing, clashes (default all)."),
        ["min_cover_mm"] = NativeToolUtil.Field("number", "Required clear cover (default: the host's smallest rebar cover setting)."),
        ["min_clear_spacing_mm"] = NativeToolUtil.Field("number", "Required clear distance between bars (default max(d, 25))."),
        ["max_spacing_mm"] = NativeToolUtil.Field("number", "Optional maximum centre-to-centre spacing in a set."),
        ["limit"] = NativeToolUtil.Field("integer", "Max issues listed (default 200).")
    });

    private sealed record Issue(string Check, long RebarId, int Position, long HostId, double Measured, double Required, string Note, double[]? PointMm);

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var bars = new List<Rebar>();
        if (input.TryGetValue("rebar_ids", out var rid) && rid.ValueKind == JsonValueKind.Array)
            bars.AddRange(NativeToolUtil.Ids(rid, 5000).Select(id => doc.GetElement(id) as Rebar ?? throw new ToolInputException($"Element {id.Value} is not a Rebar.")));
        if (input.TryGetValue("host_ids", out var hid) && hid.ValueKind == JsonValueKind.Array)
        {
            var hosts = NativeToolUtil.Ids(hid, 500).ToHashSet();
            bars.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().Where(r => hosts.Contains(r.GetHostId())));
        }
        bars = bars.GroupBy(b => b.Id).Select(g => g.First()).ToList();
        if (bars.Count == 0) throw new ToolInputException("No rebar found: give host_ids or rebar_ids.");
        if (bars.Count > 5000) throw new ToolInputException("Check at most 5000 rebar elements at a time.");
        var checks = input.TryGetValue("checks", out var ch) && ch.ValueKind == JsonValueKind.Array ? ch.EnumerateArray().Select(c => c.GetString()).ToHashSet() : ["containment", "cover", "spacing", "clashes"];
        double? Mm(string key) => input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() / Units.MmPerFoot : null;
        var minCover = Mm("min_cover_mm"); var minClear = Mm("min_clear_spacing_mm"); var maxSpacing = Mm("max_spacing_mm");
        var issues = new List<Issue>(); var notChecked = new List<object>();
        var stats = new Dictionary<string, int>();
        void Count(string k) => stats[k] = stats.GetValueOrDefault(k) + 1;
        int positions = 0;
        double[] P(XYZ p) => NativeToolUtil.Mm(p);

        foreach (var hostGroup in bars.GroupBy(b => b.GetHostId()))
        {
            ToolContext.ThrowIfCancelled();
            var host = doc.GetElement(hostGroup.Key);
            if (host == null) { foreach (var b in hostGroup) notChecked.Add(new { rebar_id = b.Id.Value, reason = "host not found" }); continue; }
            var hostSolids = RebarGeometry.Solids(host);
            var box = host.get_BoundingBox(null);
            if (hostSolids.Count == 0 || box == null) { foreach (var b in hostGroup) notChecked.Add(new { rebar_id = b.Id.Value, reason = "host has no solid geometry" }); continue; }
            var concrete = RebarGeometry.NearbyConcrete(doc, host, box);
            var faces = hostSolids.SelectMany(s => s.Faces.Cast<Face>()).ToList();
            var requiredCover = minCover ?? RebarGeometry.RequiredCover(doc, host);
            var curvesByBar = new Dictionary<(long, int), IList<Curve>>();
            foreach (var bar in hostGroup)
            {
                var d = RebarGeometry.Diameter(doc, bar);
                for (int i = 0; i < bar.NumberOfBarPositions; i++)
                {
                    if (!bar.DoesBarExistAtPosition(i)) continue;
                    if (++positions > 40000) throw new ToolInputException("More than 40 000 bar positions; narrow the scope.");
                    var curves = RebarGeometry.Centerline(bar, i);
                    curvesByBar[(bar.Id.Value, i)] = curves;
                    if (checks.Contains("containment"))
                    {
                        Count("containment_checked");
                        var total = curves.Sum(c => c.Length);
                        var inHost = curves.Sum(c => RebarGeometry.InsideLength(hostSolids, c));
                        var inConcrete = curves.Sum(c => RebarGeometry.InsideLength(concrete, c));
                        var outside = total - inConcrete; var anchored = inConcrete - inHost;
                        if (outside > 1 / Units.MmPerFoot)
                        {
                            var at = RebarGeometry.Samples(curves, 0.1).FirstOrDefault(p => !RebarGeometry.PointInside(concrete, p));
                            issues.Add(new("outside_concrete", bar.Id.Value, i, host.Id.Value, Math.Round(outside * Units.MmPerFoot, 1), 0, $"{outside / total:P0} of the bar is outside any concrete", at == null ? null : P(at)));
                        }
                        else if (anchored > 1 / Units.MmPerFoot) Count("anchored_in_adjoining_element");
                    }
                    if (checks.Contains("cover"))
                    {
                        if (requiredCover <= 0) { Count("cover_not_checked_no_requirement"); }
                        else
                        {
                            Count("cover_checked");
                            double worst = double.MaxValue; XYZ? worstAt = null;
                            foreach (var p in RebarGeometry.Samples(curves, 0.25, 24))
                            {
                                if (!RebarGeometry.PointInside(hostSolids, p)) continue;
                                var c = RebarGeometry.CoverAt(faces, concrete, p);
                                if (c is { } cv && cv - d / 2 < worst) { worst = cv - d / 2; worstAt = p; }
                            }
                            if (worst < requiredCover - 1 / Units.MmPerFoot && worstAt != null)
                                issues.Add(new("cover", bar.Id.Value, i, host.Id.Value, Math.Round(worst * Units.MmPerFoot, 1), Math.Round(requiredCover * Units.MmPerFoot, 1), "clear cover to the outer concrete surface", P(worstAt)));
                        }
                    }
                }
                if (checks.Contains("spacing") && bar.NumberOfBarPositions > 1)
                {
                    Count("spacing_checked");
                    var mids = Enumerable.Range(0, bar.NumberOfBarPositions).Where(bar.DoesBarExistAtPosition)
                        .Select(i => curvesByBar.TryGetValue((bar.Id.Value, i), out var cs) ? Mid(cs) : null).Where(p => p != null).ToList();
                    for (int k = 0; k + 1 < mids.Count; k++)
                    {
                        var cc = mids[k]!.DistanceTo(mids[k + 1]!);
                        var clear = cc - d;
                        var need = minClear ?? Math.Max(d, 25 / Units.MmPerFoot);
                        if (clear < need - 0.5 / Units.MmPerFoot)
                        { issues.Add(new("clear_spacing", bar.Id.Value, k, host.Id.Value, Math.Round(clear * Units.MmPerFoot, 1), Math.Round(need * Units.MmPerFoot, 1), "clear distance between adjacent bars of the set", P(mids[k]!))); break; }
                        if (maxSpacing is { } ms && cc > ms + 0.5 / Units.MmPerFoot)
                        { issues.Add(new("max_spacing", bar.Id.Value, k, host.Id.Value, Math.Round(cc * Units.MmPerFoot, 1), Math.Round(ms * Units.MmPerFoot, 1), "centre-to-centre spacing", P(mids[k]!))); break; }
                    }
                }
            }
            if (checks.Contains("clashes"))
            {
                var items = curvesByBar.ToList();
                if (items.Count > 3000) { notChecked.Add(new { host_id = host.Id.Value, reason = "more than 3000 bars in the host; clash check skipped" }); continue; }
                var diam = hostGroup.ToDictionary(b => b.Id.Value, b => RebarGeometry.Diameter(doc, b));
                var boxes = items.Select(kv => Box(kv.Value)).ToList();
                for (int i = 0; i < items.Count; i++)
                    for (int j = i + 1; j < items.Count; j++)
                    {
                        if (items[i].Key.Item1 == items[j].Key.Item1) continue;
                        var r = (diam[items[i].Key.Item1] + diam[items[j].Key.Item1]) / 2;
                        if (!Overlap(boxes[i], boxes[j], r)) continue;
                        Count("clash_pairs_tested");
                        var hit = RebarGeometry.Samples(items[i].Value, 0.05, 60)
                            .Select(p => (p, d: items[j].Value.Select(c => c.Project(p)?.Distance ?? double.MaxValue).Min()))
                            .OrderBy(x => x.d).First();
                        if (hit.d < r - 1 / Units.MmPerFoot)
                            issues.Add(new("bar_clash", items[i].Key.Item1, items[i].Key.Item2, host.Id.Value, Math.Round(hit.d * Units.MmPerFoot, 1), Math.Round(r * Units.MmPerFoot, 1),
                                $"bars of {items[i].Key.Item1} and {items[j].Key.Item1} overlap (centre distance below the sum of radii)", P(hit.p)));
                    }
            }
        }
        var limit = input.TryGetValue("limit", out var lim) && lim.ValueKind == JsonValueKind.Number ? Math.Clamp(lim.GetInt32(), 1, 2000) : 200;
        return Json.Serialize(new
        {
            rebar_elements = bars.Count, bar_positions = positions, stats,
            issues_by_check = issues.GroupBy(x => x.Check).ToDictionary(g => g.Key, g => g.Count()),
            issues = issues.Take(limit).Select(x => new { check = x.Check, rebar_id = x.RebarId, position = x.Position, host_id = x.HostId, measured_mm = x.Measured, required_mm = x.Required, note = x.Note, point_mm = x.PointMm }),
            truncated = issues.Count > limit, not_checked = notChecked
        });
    }

    private static XYZ? Mid(IList<Curve> curves)
    {
        if (curves.Count == 0) return null;
        var longest = curves.OrderByDescending(c => c.Length).First();
        return longest.Evaluate(0.5, true);
    }
    private static (XYZ Min, XYZ Max) Box(IList<Curve> curves)
    {
        var pts = curves.SelectMany(c => c.Tessellate()).ToList();
        return (new XYZ(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z)), new XYZ(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z)));
    }
    private static bool Overlap((XYZ Min, XYZ Max) a, (XYZ Min, XYZ Max) b, double pad) =>
        a.Min.X - pad <= b.Max.X && b.Min.X - pad <= a.Max.X && a.Min.Y - pad <= b.Max.Y && b.Min.Y - pad <= a.Max.Y && a.Min.Z - pad <= b.Max.Z && b.Min.Z - pad <= a.Max.Z;
}

public sealed class CreateStirrupZones : IRevitTool
{
    public string Name => "create_stirrup_zones";
    public string Description =>
        "Stirrups along a straight beam or column in zones, e.g. zones [{length_mm:900, spacing_mm:100}, " +
        "{spacing_mm:200}, {length_mm:900, spacing_mm:100}]: support zones keep their exact spacing, the one zone without " +
        "a length fills the middle with an even spacing no larger than asked, and no stirrup is duplicated at a zone " +
        "boundary. The stirrup is a closed rectangle inset by the cover (rectangular sections). One rebar set per zone, " +
        "then every bar is checked to lie in concrete. preview defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["host_id"] = NativeToolUtil.Field("integer", "Straight structural beam or column."),
        ["zones"] = NativeToolUtil.Any("Array of {length_mm?, spacing_mm}; at most one zone without length_mm."),
        ["bar_type_name"] = NativeToolUtil.Field("string", "Stirrup bar type (default the first loaded)."),
        ["cover_mm"] = NativeToolUtil.Field("number", "Clear cover to the stirrup (default the host's cover setting, else 25)."),
        ["end_offset_mm"] = NativeToolUtil.Field("number", "First/last stirrup from the member ends (default 50)."),
        ["hook_type_name"] = NativeToolUtil.Field("string", "Hook at both stirrup ends, e.g. a 135° hook (default none)."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "host_id", "zones");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var host = ReinforcementHelpers.GetValidRebarHost(doc, input);
        if (host.Location is not LocationCurve { Curve: Line axisLine }) throw new ToolInputException("Stirrup zones need a straight beam or column (line location).");
        var barType = ReinforcementHelpers.ResolveBarType(doc, input);
        var zones = ToolInput.RequiredArray(input, "zones").EnumerateArray().Select(z => new StirrupZone(
            z.TryGetProperty("length_mm", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() : null,
            z.TryGetProperty("spacing_mm", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : throw new ToolInputException("Every zone needs spacing_mm."))).ToList();
        var axis = axisLine.Direction.Normalize();
        var u = Math.Abs(axis.Z) > 0.9 ? XYZ.BasisX : axis.CrossProduct(XYZ.BasisZ).Normalize();
        var v = axis.CrossProduct(u).Normalize();
        if (v.Z < 0) { v = -v; u = -u; }
        var origin = axisLine.GetEndPoint(0);
        var verts = RebarGeometry.Solids(host).SelectMany(sd => sd.Edges.Cast<Edge>().SelectMany(e => e.Tessellate())).ToList();
        if (verts.Count == 0) throw new ToolInputException("The host has no solid geometry.");
        double T(XYZ p) => (p - origin).DotProduct(axis); double U(XYZ p) => (p - origin).DotProduct(u); double V(XYZ p) => (p - origin).DotProduct(v);
        double t0 = verts.Min(T), t1 = verts.Max(T), u0 = verts.Min(U), u1 = verts.Max(U), v0 = verts.Min(V), v1 = verts.Max(V);
        var cover = input.TryGetValue("cover_mm", out var cv) && cv.ValueKind == JsonValueKind.Number ? cv.GetDouble() / Units.MmPerFoot
            : RebarGeometry.RequiredCover(doc, host) is > 0 and var hc ? hc : 25 / Units.MmPerFoot;
        var inset = cover + barType.BarModelDiameter / 2;
        if (u1 - u0 <= 2 * inset || v1 - v0 <= 2 * inset) throw new ToolInputException("The section is too small for this cover and bar.");
        var endOffset = input.TryGetValue("end_offset_mm", out var eo) && eo.ValueKind == JsonValueKind.Number ? eo.GetDouble() : 50;
        List<StirrupRun> runs;
        try { runs = StirrupZones.Layout((t1 - t0) * Units.MmPerFoot, zones, endOffset, endOffset); }
        catch (ArgumentException ex) { throw new ToolInputException(ex.Message); }
        RebarHookType? hook = null;
        var hookName = NativeToolUtil.Text(input, "hook_type_name");
        if (hookName.Length > 0)
        {
            var hooks = new FilteredElementCollector(doc).OfClass(typeof(RebarHookType)).Cast<RebarHookType>().ToList();
            hook = hooks.FirstOrDefault(h => h.Name == hookName) ?? throw NameResolve.Missing(hookName, "Rebar hook type", hooks.Select(h => h.Name));
        }
        var preview = NativeToolUtil.Preview(input);
        var ((created, outside), warnings) = NativeToolUtil.Commit(doc, "Claude: зоны хомутов", preview, () =>
        {
            var made = new List<object>(); var bad = 0;
            foreach (var run in runs)
            {
                var t = t0 + run.StartMm / Units.MmPerFoot;
                XYZ Pt(double a, double b) => origin + axis * t + u * a + v * b;
                var a0 = u0 + inset; var a1 = u1 - inset; var b0 = v0 + inset; var b1 = v1 - inset;
                var loop = new List<Curve>
                {
                    Line.CreateBound(Pt(a0, b1), Pt(a1, b1)), Line.CreateBound(Pt(a1, b1), Pt(a1, b0)),
                    Line.CreateBound(Pt(a1, b0), Pt(a0, b0)), Line.CreateBound(Pt(a0, b0), Pt(a0, b1))
                };
#if REVIT2027
                using var terminations = new BarTerminationsData(doc)
                {
                    HookTypeIdAtStart = hook?.Id ?? ElementId.InvalidElementId, HookTypeIdAtEnd = hook?.Id ?? ElementId.InvalidElementId,
                    TerminationOrientationAtStart = RebarTerminationOrientation.Right, TerminationOrientationAtEnd = RebarTerminationOrientation.Right
                };
                var bar = Rebar.CreateFromCurves(doc, RebarStyle.StirrupTie, barType, host, axis, loop, terminations, true, true)
#else
                var bar = Rebar.CreateFromCurves(doc, RebarStyle.StirrupTie, barType, hook, hook, host, axis, loop,
                    RebarHookOrientation.Right, RebarHookOrientation.Right, true, true)
#endif
                    ?? throw new InvalidOperationException("Revit could not build a stirrup shape for this section.");
                if (run.Count > 1) bar.GetShapeDrivenAccessor().SetLayoutAsNumberWithSpacing(run.Count, run.SpacingMm / Units.MmPerFoot, true, true, true);
                doc.Regenerate();
                var hostSolids = RebarGeometry.Solids(host);
                for (int i = 0; i < bar.NumberOfBarPositions; i++)
                {
                    var cs = RebarGeometry.Centerline(bar, i);
                    if (cs.Sum(c => c.Length) - cs.Sum(c => RebarGeometry.InsideLength(hostSolids, c)) > 1 / Units.MmPerFoot) bad++;
                }
                made.Add(new { zone = run.Zone, rebar_id = bar.Id.Value, count = run.Count, spacing_mm = Math.Round(run.SpacingMm, 1), from_mm = Math.Round(run.StartMm, 1), to_mm = Math.Round(run.EndMm, 1) });
            }
            return (made, bad);
        });
        return Json.Serialize(new
        {
            preview, host_id = host.Id.Value, member_length_mm = Math.Round((t1 - t0) * Units.MmPerFoot), section_mm = new[] { Math.Round((u1 - u0) * Units.MmPerFoot), Math.Round((v1 - v0) * Units.MmPerFoot) },
            cover_mm = Math.Round(cover * Units.MmPerFoot, 1), stirrups = runs.Sum(r => r.Count), zones = created,
            stirrups_outside_concrete = outside, verdict = outside == 0 ? "all stirrups lie in the host" : "some stirrups leave the host — check the section shape and cover",
            revit_warnings = warnings
        });
    }
}
