using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Structural analytical model, loads and supports (Revit 2023+ analytical API). Ideas from the
// nhantruong96 fork of mcp-servers-for-revit (analytical members/panels, load cases, point/line/
// area loads, boundary conditions; MIT); native implementations.
internal static class AnalyticalUtil
{
    public static AnalyticalToPhysicalAssociationManager Manager(Document doc) =>
        AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc);

    public static readonly BuiltInCategory[] MemberCategories = [BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns];

    public static XYZ Vec(JsonElement e, ForgeTypeId unit)
    {
        var a = e.EnumerateArray().Select(v => v.GetDouble()).ToArray();
        if (a.Length != 3 || a.Any(x => !double.IsFinite(x))) throw new ToolInputException("Force/moment vectors are [x, y, z].");
        return new XYZ(UnitUtils.ConvertToInternalUnits(a[0], unit), UnitUtils.ConvertToInternalUnits(a[1], unit), UnitUtils.ConvertToInternalUnits(a[2], unit));
    }
}

public sealed class GetAnalyticalModel : IRevitTool
{
    public string Name => "get_analytical_model";
    public string Description =>
        "Read the structural analytical model: analytical members and panels with their role, end points (mm), section " +
        "type and associated physical element; physical framing/columns/structural floors and walls that have no " +
        "analytical counterpart; load cases with load counts; boundary conditions. Read-only.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["limit"] = NativeToolUtil.Field("integer", "Max members/panels listed (default 200).")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var limit = input.TryGetValue("limit", out var l) && l.ValueKind == JsonValueKind.Number ? Math.Clamp(l.GetInt32(), 1, 5000) : 200;
        var manager = AnalyticalUtil.Manager(doc);
        var members = new FilteredElementCollector(doc).OfClass(typeof(AnalyticalMember)).Cast<AnalyticalMember>().ToList();
        var panels = new FilteredElementCollector(doc).OfClass(typeof(AnalyticalPanel)).Cast<AnalyticalPanel>().ToList();
        var physical = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(AnalyticalUtil.MemberCategories)).WhereElementIsNotElementType().ToElements()
            .Concat(new FilteredElementCollector(doc).OfClass(typeof(Floor)).Where(f => f.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1))
            .Concat(new FilteredElementCollector(doc).OfClass(typeof(Wall)).Where(w => w.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1))
            .ToList();
        var missing = physical.Where(e => !manager.HasAssociation(e.Id)).ToList();
        var loads = new FilteredElementCollector(doc).OfClass(typeof(LoadBase)).Cast<LoadBase>().ToList();
        return Services.Json.Serialize(new
        {
            members = members.Count, panels = panels.Count,
            member_list = members.Take(limit).Select(m =>
            {
                var c = m.GetCurve();
                return new
                {
                    id = m.Id.Value, role = m.StructuralRole.ToString(), start_mm = NativeToolUtil.Mm(c.GetEndPoint(0)), end_mm = NativeToolUtil.Mm(c.GetEndPoint(1)),
                    section = doc.GetElement(m.SectionTypeId)?.Name, physical_id = manager.GetAssociatedElementId(m.Id) is { } p && p != ElementId.InvalidElementId ? p.Value : (long?)null
                };
            }),
            panel_list = panels.Take(limit).Select(p => new { id = p.Id.Value, role = p.StructuralRole.ToString(), physical_id = manager.GetAssociatedElementId(p.Id) is { } x && x != ElementId.InvalidElementId ? x.Value : (long?)null }),
            physical_without_analytical = missing.GroupBy(e => e.Category?.Name).Select(g => new { category = g.Key, count = g.Count(), sample_ids = g.Take(20).Select(e => e.Id.Value) }),
            load_cases = new FilteredElementCollector(doc).OfClass(typeof(LoadCase)).Cast<LoadCase>().Select(lc => new
            {
                id = lc.Id.Value, name = lc.Name, number = lc.Number, nature = doc.GetElement(lc.NatureId)?.Name,
                loads = loads.Count(x => x.LoadCaseId == lc.Id)
            }),
            loads = loads.GroupBy(x => x.GetType().Name).Select(g => new { kind = g.Key, count = g.Count() }),
            boundary_conditions = new FilteredElementCollector(doc).OfClass(typeof(BoundaryConditions)).Cast<BoundaryConditions>()
                .GroupBy(b => b.GetBoundaryConditionsType()).Select(g => new { type = g.Key.ToString(), count = g.Count() })
        });
    }
}

public sealed class CreateAnalyticalModel : IRevitTool
{
    public string Name => "create_analytical_model";
    public string Description =>
        "Create missing analytical elements for physical structure and associate them: members for framing (beam role) " +
        "and columns (column role, base-to-top line from levels and offsets), optionally panels for structural floors " +
        "(top face outline) and structural walls (centre-line × height). Elements that already have an analytical " +
        "counterpart are skipped. Scope: element_ids, or every structural element. preview defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["element_ids"] = NativeToolUtil.Array("integer", "Physical elements (default all structural framing and columns)."),
        ["include_panels"] = NativeToolUtil.Field("boolean", "Also structural floors and walls (default false)."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var manager = AnalyticalUtil.Manager(doc);
        List<Element> elements;
        if (input.TryGetValue("element_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            elements = NativeToolUtil.Ids(ids).Select(id => doc.GetElement(id) ?? throw NameResolve.MissingId(id.Value)).ToList();
        else
        {
            elements = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(AnalyticalUtil.MemberCategories)).WhereElementIsNotElementType().ToElements().ToList();
            if (ToolInput.Flag(input, "include_panels"))
                elements.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Floor)).Where(f => f.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1)
                    .Concat(new FilteredElementCollector(doc).OfClass(typeof(Wall)).Where(w => w.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1)));
        }
        var todo = elements.Where(e => !manager.HasAssociation(e.Id)).ToList();
        var preview = NativeToolUtil.Preview(input);
        var ((created, skipped), warnings) = NativeToolUtil.Commit(doc, "Claude: аналитическая модель", preview, () =>
        {
            var made = new List<object>(); var skip = new List<object>();
            foreach (var e in todo)
            {
                ToolContext.ThrowIfCancelled();
                try
                {
                    Element? analytical = e switch
                    {
                        FamilyInstance fi when fi.Category?.BuiltInCategory == BuiltInCategory.OST_StructuralColumns => Member(doc, ColumnLine(doc, fi), fi, AnalyticalStructuralRole.StructuralRoleColumn),
                        FamilyInstance fi when fi.Location is LocationCurve lc => Member(doc, lc.Curve, fi, fi.StructuralType == StructuralType.Brace ? AnalyticalStructuralRole.StructuralRoleMember : AnalyticalStructuralRole.StructuralRoleBeam),
                        Floor floor => Panel(doc, FloorLoop(floor), AnalyticalStructuralRole.StructuralRoleFloor),
                        Wall wall => Panel(doc, WallLoop(doc, wall), AnalyticalStructuralRole.StructuralRoleWall),
                        _ => null
                    };
                    if (analytical == null) { skip.Add(new { id = e.Id.Value, reason = "no supported location (point-based framing or unsupported element)" }); continue; }
                    manager.AddAssociation(analytical.Id, e.Id);
                    made.Add(new { physical_id = e.Id.Value, analytical_id = analytical.Id.Value, kind = analytical.GetType().Name });
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ArgumentException or Autodesk.Revit.Exceptions.InvalidOperationException or ToolInputException)
                {
                    skip.Add(new { id = e.Id.Value, reason = ex.Message });
                }
            }
            return (made, skip);
        });
        return Services.Json.Serialize(new { preview, candidates = todo.Count, already_associated = elements.Count - todo.Count, created = created.Count, items = created.Take(300), skipped, revit_warnings = warnings });
    }

    private static AnalyticalMember Member(Document doc, Curve curve, FamilyInstance fi, AnalyticalStructuralRole role)
    {
        if (!AnalyticalMember.IsValidCurve(curve)) throw new ToolInputException("Location curve is not valid for an analytical member.");
        var m = AnalyticalMember.Create(doc, curve);
        if (m.IsValidStructuralRole(role)) m.StructuralRole = role;
        if (m.IsValidSectionTypeId(fi.Symbol.Id)) m.SectionTypeId = fi.Symbol.Id;
        return m;
    }

    private static Curve ColumnLine(Document doc, FamilyInstance column)
    {
        if (column.Location is LocationCurve lc) return lc.Curve;   // slanted column
        var p = ((LocationPoint)column.Location).Point;
        double Elevation(BuiltInParameter level, BuiltInParameter offset) =>
            ((doc.GetElement(column.get_Parameter(level)?.AsElementId() ?? ElementId.InvalidElementId) as Level)?.Elevation ?? throw new ToolInputException("Column has no base/top level."))
            + (column.get_Parameter(offset)?.AsDouble() ?? 0);
        var bottom = Elevation(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);
        var top = Elevation(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM);
        if (top - bottom < 1e-3) throw new ToolInputException("Column top is not above its base.");
        return Line.CreateBound(new XYZ(p.X, p.Y, bottom), new XYZ(p.X, p.Y, top));
    }

    private static AnalyticalPanel Panel(Document doc, CurveLoop loop, AnalyticalStructuralRole role)
    {
        var panel = AnalyticalPanel.Create(doc, loop);
        if (panel.IsValidStructuralRole(role)) panel.StructuralRole = role;
        return panel;
    }

    // Outer boundary of the slab's top face: the loop enclosing the largest area.
    private static CurveLoop FloorLoop(Floor floor)
    {
        var face = HostObjectUtils.GetTopFaces(floor).Select(r => floor.GetGeometryObjectFromReference(r)).OfType<PlanarFace>().OrderByDescending(f => f.Area).FirstOrDefault()
            ?? throw new ToolInputException("Floor has no planar top face.");
        return face.GetEdgesAsCurveLoops().OrderByDescending(Area).First();
    }

    private static double Area(CurveLoop loop)
    {
        var pts = loop.SelectMany(c => c.Tessellate().Take(c.Tessellate().Count - 1)).ToList();
        double a = 0;
        for (int i = 0; i < pts.Count; i++) { var p = pts[i]; var q = pts[(i + 1) % pts.Count]; a += p.X * q.Y - q.X * p.Y; }
        return Math.Abs(a) / 2;
    }

    private static CurveLoop WallLoop(Document doc, Wall wall)
    {
        if (wall.Location is not LocationCurve lc || lc.Curve is not Line line) throw new ToolInputException("Only straight walls get a panel.");
        var baseLevel = doc.GetElement(wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? ElementId.InvalidElementId) as Level
            ?? throw new ToolInputException("Wall has no base level.");
        var bottom = baseLevel.Elevation + (wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0);
        var height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0;
        if (height < 1e-3) throw new ToolInputException("Wall has no height.");
        XYZ At(XYZ p, double z) => new(p.X, p.Y, z);
        var a = line.GetEndPoint(0); var b = line.GetEndPoint(1);
        return CurveLoop.Create(new List<Curve>
        {
            Line.CreateBound(At(a, bottom), At(b, bottom)), Line.CreateBound(At(b, bottom), At(b, bottom + height)),
            Line.CreateBound(At(b, bottom + height), At(a, bottom + height)), Line.CreateBound(At(a, bottom + height), At(a, bottom))
        });
    }
}

public sealed class CreateStructuralLoads : IRevitTool
{
    public string Name => "create_structural_loads";
    public string Description =>
        "Create structural loads in a load case (created with its nature if missing): point loads (on an analytical " +
        "member end, or free at point_mm), line loads (along a whole analytical member, or free between two points) and " +
        "area loads (on a whole analytical panel, or free over loop_mm). Forces in kN, kN/m, kPa (kN/m²) and moments in " +
        "kN·m, as global [x, y, z]; gravity is negative z. preview defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["load_case"] = NativeToolUtil.Field("string", "Load case name, e.g. 'Постоянная' or 'DL1'."),
        ["category"] = NativeToolUtil.Field("string", "For a new case: dead (default), live, wind, snow, roof_live, accidental, temperature, seismic."),
        ["loads"] = NativeToolUtil.Any("Array of {kind:'point', host_id?, end:'start'|'end', point_mm?, force_kn:[x,y,z], moment_knm?} | {kind:'line', host_id? | points_mm:[[x,y,z],[x,y,z]], force_kn_per_m:[x,y,z]} | {kind:'area', host_id? | loop_mm:[[x,y,z],...], force_kpa:[x,y,z]}."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "load_case", "loads");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var caseName = ToolInput.RequiredString(input, "load_case");
        var categoryText = NativeToolUtil.Text(input, "category", "dead").Replace("_", "");
        if (!Enum.TryParse<LoadCaseCategory>(categoryText, true, out var category)) throw new ToolInputException("category: dead, live, wind, snow, roof_live, accidental, temperature or seismic.");
        var loads = ToolInput.RequiredArray(input, "loads").EnumerateArray().ToList();
        if (loads.Count is 0 or > 2000) throw new ToolInputException("Supply 1..2000 loads.");
        var preview = NativeToolUtil.Preview(input);
        var ((created, caseId, newCase), warnings) = NativeToolUtil.Commit(doc, "Claude: нагрузки", preview, () =>
        {
            var lc = new FilteredElementCollector(doc).OfClass(typeof(LoadCase)).Cast<LoadCase>().FirstOrDefault(c => c.Name == caseName);
            var isNew = lc == null;
            if (lc == null)
            {
                var natureName = category.ToString();
                var nature = new FilteredElementCollector(doc).OfClass(typeof(LoadNature)).Cast<LoadNature>().FirstOrDefault(n => n.Name.Equals(natureName, StringComparison.OrdinalIgnoreCase))
                    ?? LoadNature.Create(doc, natureName);
                lc = LoadCase.Create(doc, caseName, nature.Id, category);
            }
            var made = new List<object>();
            foreach (var (l, index) in loads.Select((l, i) => (l, i)))
            {
                var kind = l.TryGetProperty("kind", out var k) ? k.GetString() : null;
                var host = l.TryGetProperty("host_id", out var h) && h.ValueKind == JsonValueKind.Number ? doc.GetElement(new ElementId(h.GetInt64())) ?? throw NameResolve.MissingId(h.GetInt64(), "Analytical host") : null;
                if (host != null && !AnalyticalToPhysicalAssociationManager.IsAnalyticalElement(doc, host.Id))
                    throw new ToolInputException($"loads[{index}]: host {host.Id.Value} is a physical element; use its analytical element (get_analytical_model).");
                XYZ P(JsonElement e) => NativeToolUtil.Point(e);
                LoadBase load = kind switch
                {
                    "point" => host != null
                        ? PointLoad.Create(doc, host.Id, l.TryGetProperty("end", out var end) && end.GetString() == "end" ? AnalyticalElementSelector.EndOrTop : AnalyticalElementSelector.StartOrBase,
                            AnalyticalUtil.Vec(l.GetProperty("force_kn"), UnitTypeId.Kilonewtons), Moment(l), null)
                        : PointLoad.Create(doc, ElementId.InvalidElementId, P(Req(l, "point_mm", index)), AnalyticalUtil.Vec(l.GetProperty("force_kn"), UnitTypeId.Kilonewtons), Moment(l), null),
                    "line" => host != null
                        ? LineLoad.Create(doc, host.Id, AnalyticalUtil.Vec(Req(l, "force_kn_per_m", index), UnitTypeId.KilonewtonsPerMeter), XYZ.Zero, null)
                        : LineLoad.Create(doc, ElementId.InvalidElementId, LineFrom(Req(l, "points_mm", index)), AnalyticalUtil.Vec(Req(l, "force_kn_per_m", index), UnitTypeId.KilonewtonsPerMeter), XYZ.Zero, null),
                    "area" => host != null
                        ? AreaLoad.Create(doc, host.Id, AnalyticalUtil.Vec(Req(l, "force_kpa", index), UnitTypeId.Kilopascals), null)
                        : AreaLoad.Create(doc, ElementId.InvalidElementId, [LoopFrom(Req(l, "loop_mm", index))], AnalyticalUtil.Vec(Req(l, "force_kpa", index), UnitTypeId.Kilopascals), null),
                    _ => throw new ToolInputException($"loads[{index}].kind must be point, line or area.")
                };
                load.LoadCaseId = lc.Id;
                made.Add(new { index, id = load.Id.Value, kind, host_id = host?.Id.Value });
            }
            return (made, lc.Id.Value, isNew);
        });
        return Services.Json.Serialize(new { preview, load_case = caseName, load_case_id = caseId, load_case_created = newCase, loads = created, revit_warnings = warnings });
    }

    private static JsonElement Req(JsonElement l, string name, int index) =>
        l.TryGetProperty(name, out var v) ? v : throw new ToolInputException($"loads[{index}] needs {name}.");
    private static XYZ Moment(JsonElement l) => l.TryGetProperty("moment_knm", out var m) ? AnalyticalUtil.Vec(m, UnitTypeId.KilonewtonMeters) : XYZ.Zero;
    private static Line LineFrom(JsonElement pts)
    {
        var p = pts.EnumerateArray().Select(e => NativeToolUtil.Point(e)).ToList();
        if (p.Count != 2) throw new ToolInputException("points_mm is [[x,y,z],[x,y,z]].");
        return Line.CreateBound(p[0], p[1]);
    }
    private static CurveLoop LoopFrom(JsonElement pts)
    {
        var p = pts.EnumerateArray().Select(e => NativeToolUtil.Point(e)).ToList();
        if (p.Count < 3) throw new ToolInputException("loop_mm needs at least three points.");
        return CurveLoop.Create(p.Select((a, i) => (Curve)Line.CreateBound(a, p[(i + 1) % p.Count])).ToList());
    }
}

public sealed class CreateBoundaryConditions : IRevitTool
{
    public string Name => "create_boundary_conditions";
    public string Description =>
        "Add point supports at analytical member ends: fixed (all translations and rotations fixed), pinned " +
        "(translations fixed, rotations free) or roller (vertical translation fixed only). Targets: member_ids with " +
        "end start|end|both|lowest, or column_bases=true for the lowest end of every analytical column. Ends that already " +
        "carry a support are skipped. preview defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["member_ids"] = NativeToolUtil.Array("integer", "Analytical members."),
        ["column_bases"] = NativeToolUtil.Field("boolean", "Every analytical column's lowest end."),
        ["end"] = NativeToolUtil.Field("string", "start | end | both | lowest (default lowest)."),
        ["condition"] = NativeToolUtil.Field("string", "fixed (default) | pinned | roller."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        List<AnalyticalMember> members;
        if (ToolInput.Flag(input, "column_bases"))
            members = new FilteredElementCollector(doc).OfClass(typeof(AnalyticalMember)).Cast<AnalyticalMember>().Where(m => m.StructuralRole == AnalyticalStructuralRole.StructuralRoleColumn).ToList();
        else if (input.TryGetValue("member_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            members = NativeToolUtil.Ids(ids).Select(id => doc.GetElement(id) as AnalyticalMember ?? throw new ToolInputException($"Element {id.Value} is not an analytical member.")).ToList();
        else throw new ToolInputException("Choose member_ids or column_bases=true.");
        var end = ToolInput.Flag(input, "column_bases") ? "lowest" : NativeToolUtil.Text(input, "end", "lowest");
        if (end is not ("start" or "end" or "both" or "lowest")) throw new ToolInputException("end must be start, end, both or lowest.");
        var condition = NativeToolUtil.Text(input, "condition", "fixed");
        var (tx, ty, tz, r) = condition switch
        {
            "fixed" => (TranslationRotationValue.Fixed, TranslationRotationValue.Fixed, TranslationRotationValue.Fixed, TranslationRotationValue.Fixed),
            "pinned" => (TranslationRotationValue.Fixed, TranslationRotationValue.Fixed, TranslationRotationValue.Fixed, TranslationRotationValue.Release),
            "roller" => (TranslationRotationValue.Release, TranslationRotationValue.Release, TranslationRotationValue.Fixed, TranslationRotationValue.Release),
            _ => throw new ToolInputException("condition must be fixed, pinned or roller.")
        };
        var existing = new FilteredElementCollector(doc).OfClass(typeof(BoundaryConditions)).Cast<BoundaryConditions>()
            .Where(b => b.GetBoundaryConditionsType() == BoundaryConditionsType.Point)
            .Select(b => Key(b.Point)).ToHashSet();
        var preview = NativeToolUtil.Preview(input);
        var ((created, skipped), warnings) = NativeToolUtil.Commit(doc, "Claude: опоры", preview, () =>
        {
            var made = new List<object>(); var skip = new List<object>();
            foreach (var m in members)
            {
                var curve = m.GetCurve();
                var ends = end switch
                {
                    "start" => new[] { AnalyticalCurveSelector.StartPoint },
                    "end" => [AnalyticalCurveSelector.EndPoint],
                    "both" => [AnalyticalCurveSelector.StartPoint, AnalyticalCurveSelector.EndPoint],
                    _ => [curve.GetEndPoint(0).Z <= curve.GetEndPoint(1).Z ? AnalyticalCurveSelector.StartPoint : AnalyticalCurveSelector.EndPoint]
                };
                foreach (var e in ends)
                {
                    var at = curve.GetEndPoint(e == AnalyticalCurveSelector.StartPoint ? 0 : 1);
                    if (!existing.Add(Key(at))) { skip.Add(new { member_id = m.Id.Value, reason = "a point support already exists at this end" }); continue; }
                    using var selector = new AnalyticalModelSelector(curve, e);
                    var reference = m.GetReference(selector);
                    if (reference == null) { skip.Add(new { member_id = m.Id.Value, reason = "no end reference" }); continue; }
                    var bc = doc.Create.NewPointBoundaryConditions(reference, tx, 0, ty, 0, tz, 0, r, 0, r, 0, r, 0);
                    made.Add(new { member_id = m.Id.Value, end = e == AnalyticalCurveSelector.StartPoint ? "start" : "end", id = bc.Id.Value });
                }
            }
            return (made, skip);
        });
        return Services.Json.Serialize(new { preview, condition, created = created.Count, items = created.Take(300), skipped, revit_warnings = warnings });
    }

    private static (long, long, long) Key(XYZ? p) => p == null ? (long.MinValue, 0, 0) :
        ((long)Math.Round(p.X * Units.MmPerFoot), (long)Math.Round(p.Y * Units.MmPerFoot), (long)Math.Round(p.Z * Units.MmPerFoot));
}
