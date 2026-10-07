using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// Norm audit against Russian codes (СП РФ) and drawing callouts for the findings. Idea from the
// NewLevelHub fork of mcp-servers-for-revit (check_* normatives + apply_norm_result); the rules,
// measurement and annotation here are native and follow СП 1.13130.2020 / СП 54.13330.2022.
public sealed class AuditNorms : IRevitTool
{
    public string Name => "audit_norms";
    public string Description =>
        "Check the model against Russian code requirements (СП 1.13130.2020 evacuation, СП 54.13330.2022 residential): " +
        "evacuation door clear width/height, corridor width, stair run width / riser / tread / slope, ceiling heights of " +
        "living rooms and apartment circulation, minimum areas of living rooms, bedrooms and kitchens, railing heights. " +
        "Rooms are classified by name (Кухня, Спальня, Коридор, Лестничная клетка…). Thresholds follow the clause variants " +
        "for functional_class (e.g. Ф1.3), climate_subregion and corridor_occupants; overrides set project values. " +
        "Read-only. Returns failures with the clause, measured vs required value and how it was measured, plus " +
        "annotate_input to pass to annotate_norm_findings. list_rules=true returns the rule catalog. The result is a " +
        "screening aid; a responsible engineer confirms applicability and the edition in force.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["rules"] = NativeToolUtil.Array("string", "Rule ids to run (default all). See list_rules."),
        ["list_rules"] = NativeToolUtil.Field("boolean", "Return the rule catalog only."),
        ["level"] = NativeToolUtil.Field("string", "Only elements on this level."),
        ["functional_class"] = NativeToolUtil.Field("string", "Building functional fire-hazard class, e.g. Ф1.3 (selects stair width variant)."),
        ["climate_subregion"] = NativeToolUtil.Field("string", "Climate subregion, e.g. IIВ; IA/IБ/IГ/IД/IVА raise living-room height to 2.7 m."),
        ["corridor_occupants"] = NativeToolUtil.Field("integer", "People evacuating along corridors; more than 50 raises corridor width to 1.2 m."),
        ["apartment_parameter"] = NativeToolUtil.Field("string", "Room parameter holding the apartment number; enables the one-room-apartment variants and excludes in-apartment corridors from evacuation corridor checks."),
        ["overrides"] = NativeToolUtil.Any("Object {rule_id: value} in the rule's unit (mm, m², ratio), for project-specific requirements."),
        ["include_passed"] = NativeToolUtil.Field("boolean", "Also list passing checks (default false)."),
        ["limit"] = NativeToolUtil.Field("integer", "Max findings listed (default 300).")
    });

    private sealed record Finding(string Rule, long ElementId, string Element, string? Level, double Measured, NormThreshold Threshold, bool Passed, string Method, double[]? PointMm);

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        if (ToolInput.Flag(input, "list_rules"))
            return Json.Serialize(new { rules = NormRules.Ru.Select(r => new { id = r.Id, document = r.Document, clause = r.Clause, subject = r.Subject, measure = r.Measure, comparison = r.AtLeast ? ">=" : "<=", value = r.Value, unit = r.Unit, requirement = r.Requirement }) });

        var doc = NativeToolUtil.Doc(app);
        var selected = input.TryGetValue("rules", out var rs) && rs.ValueKind == JsonValueKind.Array
            ? rs.EnumerateArray().Select(e => NormRules.Get(e.GetString() ?? "").Id).ToHashSet()
            : NormRules.Ru.Select(r => r.Id).ToHashSet();
        var overrides = new Dictionary<string, double>();
        if (input.TryGetValue("overrides", out var ov) && ov.ValueKind == JsonValueKind.Object)
            foreach (var p in ov.EnumerateObject()) overrides[NormRules.Get(p.Name).Id] = p.Value.GetDouble();
        var baseContext = new NormContext(NativeToolUtil.Text(input, "functional_class"), NativeToolUtil.Text(input, "climate_subregion"),
            input.TryGetValue("corridor_occupants", out var co) && co.ValueKind == JsonValueKind.Number ? co.GetInt32() : null, false, overrides);
        var levelName = NativeToolUtil.Text(input, "level");
        Level? level = null;
        if (levelName.Length > 0)
            level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == levelName)
                ?? throw NameResolve.MissingLevel(doc, levelName);
        bool OnLevel(ElementId id) => level == null || id == level.Id;
        var phase = doc.Phases.Size > 0 ? doc.Phases.get_Item(doc.Phases.Size - 1) : null;
        var aptParam = NativeToolUtil.Text(input, "apartment_parameter");

        var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
            .Cast<Room>().Where(r => r.Area > 0 && r.Location != null).ToList();
        string? Apartment(Room r) => aptParam.Length == 0 ? null : r.LookupParameter(aptParam)?.AsValueString() is { Length: > 0 } v ? v : r.LookupParameter(aptParam)?.AsString();
        var oneRoom = rooms.Where(r => Apartment(r) is { Length: > 0 }).GroupBy(r => Apartment(r)!)
            .Where(g => g.Count(r => NormRules.IsLiving(NormRules.Classify(DraftingTable.RoomName(r)))) == 1).SelectMany(g => g).Select(r => r.Id).ToHashSet();
        var view3d = AuditView(doc);
        var findings = new List<Finding>();
        void Check(string ruleId, Element e, string label, double measured, string method, NormContext? ctx = null)
        {
            if (!selected.Contains(ruleId) || !double.IsFinite(measured)) return;
            var t = NormRules.Threshold(NormRules.Get(ruleId), ctx ?? baseContext);
            var lvl = e.LevelId != ElementId.InvalidElementId ? doc.GetElement(e.LevelId)?.Name : null;
            findings.Add(new(ruleId, e.Id.Value, label, lvl, Math.Round(measured, 1), t, NormRules.Passes(t, measured), method,
                DrawingAnchor.Of(e) is { } p ? NativeToolUtil.Mm(p) : null));
        }
        string RoomLabel(Room r) => $"{r.Number} {DraftingTable.RoomName(r)}".Trim();

        // Doors on evacuation paths: into a corridor, hall or stair, or to the outside.
        if (selected.Overlaps(["evac_exit_width", "evac_exit_height"]))
        {
            foreach (var door in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                if (!OnLevel(door.LevelId)) continue;
                var from = phase != null ? door.get_FromRoom(phase) : door.FromRoom;
                var to = phase != null ? door.get_ToRoom(phase) : door.ToRoom;
                // Without rooms every door is a candidate; with rooms, a door between two unbounded
                // sides (a closet in an unroomed area, a door in a curtain panel) says nothing.
                var evac = rooms.Count == 0 || ((from != null || to != null) && (from == null || to == null ||
                    NormRules.IsEvacuationSpace(NormRules.Classify(DraftingTable.RoomName(from))) || NormRules.IsEvacuationSpace(NormRules.Classify(DraftingTable.RoomName(to)))));
                if (!evac) continue;
                var label = $"Дверь {door.Symbol?.Family?.Name} : {door.Name}" + (door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() is { Length: > 0 } m ? $" ({m})" : "");
                var (w, wm) = Dimension(door, ["Ширина в свету", "Clear Width", "ADSK_Размер_Ширина в свету"], BuiltInParameter.DOOR_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM);
                var (h, hm) = Dimension(door, ["Высота в свету", "Clear Height", "ADSK_Размер_Высота в свету"], BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM);
                if (w is { } wv) Check("evac_exit_width", door, label, wv, wm);
                if (h is { } hv) Check("evac_exit_height", door, label, hv, hm);
            }
        }

        foreach (var room in rooms.Where(r => OnLevel(r.LevelId)))
        {
            var kind = NormRules.Classify(DraftingTable.RoomName(room));
            var ctx = baseContext with { OneRoomApartment = oneRoom.Contains(room.Id) };
            var areaM2 = room.Area * 0.09290304;
            if (kind == RoomKind.Corridor && Apartment(room) is not { Length: > 0 })
                Check("corridor_width", room, RoomLabel(room), NormRules.EquivalentWidth(areaM2, room.Perimeter * 0.3048) * 1000,
                    "ширина равновеликого прямоугольника по площади и периметру помещения");
            if (kind == RoomKind.CommonLiving) Check("area_common_living", room, RoomLabel(room), areaM2, "площадь помещения Revit", ctx);
            if (kind is RoomKind.Bedroom or RoomKind.LivingGeneric) Check("area_bedroom", room, RoomLabel(room), areaM2, "площадь помещения Revit", ctx);
            if (kind == RoomKind.Kitchen) Check("area_kitchen", room, RoomLabel(room), areaM2, "площадь помещения Revit", ctx);
            var heightRule = NormRules.IsLiving(kind) || kind is RoomKind.Kitchen or RoomKind.KitchenDining ? "ceiling_height_living"
                : kind is RoomKind.ApartmentHall or RoomKind.ApartmentCorridor ? "ceiling_height_circulation" : null;
            if (heightRule != null && selected.Contains(heightRule))
            {
                var (height, method) = ClearHeight(doc, room, view3d);
                Check(heightRule, room, RoomLabel(room), height, method, ctx);
            }
        }

        if (selected.Overlaps(["stair_run_width", "stair_riser_max", "stair_riser_min", "stair_tread_min", "stair_slope_max"]))
        {
            foreach (var stairs in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Stairs).WhereElementIsNotElementType().OfType<Stairs>())
            {
                var baseLevel = stairs.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
                if (!OnLevel(baseLevel)) continue;
                var label = $"Лестница {stairs.Name} (id {stairs.Id.Value})";
                var riser = stairs.ActualRiserHeight * Units.MmPerFoot; var tread = stairs.ActualTreadDepth * Units.MmPerFoot;
                Check("stair_riser_max", stairs, label, riser, "фактическая высота подступенка");
                Check("stair_riser_min", stairs, label, riser, "фактическая высота подступенка");
                Check("stair_tread_min", stairs, label, tread, "фактическая глубина проступи");
                if (tread > 0) Check("stair_slope_max", stairs, label, riser / tread, "отношение подступенка к проступи");
                var widths = stairs.GetStairsRuns().Select(id => doc.GetElement(id)).OfType<StairsRun>().Select(r => r.ActualRunWidth * Units.MmPerFoot).ToList();
                if (widths.Count > 0) Check("stair_run_width", stairs, label, widths.Min(), "наименьшая фактическая ширина марша");
            }
        }

        if (selected.Overlaps(["railing_height_interior", "railing_height_exterior"]))
        {
            foreach (var railing in new FilteredElementCollector(doc).OfClass(typeof(Railing)).Cast<Railing>())
            {
                if (doc.GetElement(railing.GetTypeId()) is not RailingType type) continue;
                var onStair = railing.HasHost && doc.GetElement(railing.HostId) is Stairs;
                if (!onStair && !OnLevel(railing.LevelId)) continue;
                Check(onStair ? "railing_height_interior" : "railing_height_exterior", railing, $"Ограждение {type.Name} (id {railing.Id.Value})",
                    type.TopRailHeight * Units.MmPerFoot,
                    onStair ? "высота верхнего поручня типа; ограждение на лестнице" : "высота верхнего поручня типа; ограждение не на лестнице — проверено как балкон/лоджия/наружное");
            }
        }

        var limit = input.TryGetValue("limit", out var lim) && lim.ValueKind == JsonValueKind.Number ? Math.Clamp(lim.GetInt32(), 1, 2000) : 300;
        var failed = findings.Where(f => !f.Passed).ToList();
        object Row(Finding f) => new
        {
            rule = f.Rule, document = f.Threshold.Rule.Document, clause = f.Threshold.Rule.Clause, element_id = f.ElementId, element = f.Element, level = f.Level,
            measured = f.Measured, required = (f.Threshold.Rule.AtLeast ? ">= " : "<= ") + f.Threshold.Value, unit = f.Threshold.Rule.Unit,
            basis = f.Threshold.Basis, passed = f.Passed, method = f.Method, point_mm = f.PointMm
        };
        string Note(Finding f) => $"{f.Threshold.Rule.Document} {f.Threshold.Rule.Clause}: {f.Threshold.Rule.Measure} {f.Measured:0.##} {(f.Threshold.Rule.AtLeast ? "<" : ">")} {f.Threshold.Value:0.##} {f.Threshold.Rule.Unit}";
        return Json.Serialize(new
        {
            summary = findings.GroupBy(f => f.Rule).Select(g => new { rule = g.Key, checked_count = g.Count(), failed = g.Count(f => !f.Passed) }),
            failed_count = failed.Count,
            findings = (ToolInput.Flag(input, "include_passed") ? findings.OrderBy(f => f.Passed) : failed.AsEnumerable()).Take(limit).Select(Row),
            truncated = (ToolInput.Flag(input, "include_passed") ? findings.Count : failed.Count) > limit,
            annotate_input = failed.Take(limit).GroupBy(f => f.ElementId).Select(g => new { element_id = g.Key, text = string.Join("\n", g.Select(Note)) }),
            disclaimer = "Пороговые значения — общий случай указанных пунктов с учётом переданного контекста. Проверьте применимость норм, редакцию и исключения для объекта; результат — инструмент предварительной проверки, а не заключение экспертизы."
        });
    }

    // Clear dimension: a modelled clear-width/height parameter if the family has one, else the
    // nominal size (doors are sized by the leaf opening, so nominal overstates the clear width).
    private static (double? Value, string Method) Dimension(FamilyInstance fi, string[] clearNames, params BuiltInParameter[] nominal)
    {
        foreach (var n in clearNames)
            foreach (var e in new Element?[] { fi, fi.Symbol })
                if (e?.LookupParameter(n) is { StorageType: StorageType.Double, HasValue: true } p && p.AsDouble() > 0)
                    return (p.AsDouble() * Units.MmPerFoot, $"параметр «{n}» (в свету)");
        foreach (var bip in nominal)
            foreach (var e in new Element?[] { fi, fi.Symbol })
                if (e?.get_Parameter(bip) is { StorageType: StorageType.Double, HasValue: true } p && p.AsDouble() > 0)
                    return (p.AsDouble() * Units.MmPerFoot, "номинальный размер (параметра «в свету» нет — фактическая ширина в свету меньше)");
        return (null, "");
    }

    // Floor-to-ceiling height measured by rays from the room point: up to the first ceiling, floor
    // or roof, down to the floor slab top. Without a usable 3D view the room's upper limit is used.
    // Rays only see what the view shows: prefer the default {3D} view, any other non-perspective
    // 3D view otherwise. Hidden categories or a section box make a ray miss and fall back.
    internal static View3D? AuditView(Document doc)
    {
        var views = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate && !v.IsPerspective).ToList();
        return views.FirstOrDefault(v => v.Name.StartsWith("{3D", StringComparison.Ordinal)) ?? views.FirstOrDefault(v => !v.IsSectionBoxActive) ?? views.FirstOrDefault();
    }

    internal static (double Height, string Method) ClearHeight(Document doc, Room room, View3D? view)
    {
        if (view == null || room.Location is not LocationPoint lp)
            return (room.UnboundedHeight * Units.MmPerFoot, "верхняя граница помещения (нет 3D-вида для замера)");
        var origin = lp.Point + new XYZ(0, 0, 1);
        var up = new ReferenceIntersector(new ElementMulticategoryFilter([BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs]), FindReferenceTarget.Face, view)
            .FindNearest(origin, XYZ.BasisZ);
        if (up == null) return (room.UnboundedHeight * Units.MmPerFoot, "верхняя граница помещения (потолок/перекрытие над точкой не найдены)");
        var down = new ReferenceIntersector(new ElementCategoryFilter(BuiltInCategory.OST_Floors), FindReferenceTarget.Face, view).FindNearest(origin, -XYZ.BasisZ);
        var floorTop = down != null && down.Proximity < 2 ? origin.Z - down.Proximity : lp.Point.Z;
        return ((origin.Z + up.Proximity - floorTop) * Units.MmPerFoot, down != null && down.Proximity < 2
            ? "луч от верха пола до потолка/перекрытия в точке помещения" : "луч от уровня до потолка/перекрытия в точке помещения");
    }
}

public sealed class AnnotateNormFindings : IRevitTool
{
    public string Name => "annotate_norm_findings";
    public string Description =>
        "Mark norm-audit findings on a drawing: a text note with a leader to each element, optionally a revision cloud " +
        "around it (on the latest revision, or a new 'Нормоконтроль' revision) and the note written to a text parameter. " +
        "Pass audit_norms' annotate_input as findings. View: a plan, section, elevation or detail (default the active view); " +
        "elements not visible in it are skipped and reported. preview defaults true (created then rolled back).";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["findings"] = NativeToolUtil.Any("Array of {element_id, text}; e.g. audit_norms annotate_input."),
        ["view_id"] = NativeToolUtil.Field("integer", "Target view (default active view)."),
        ["text_type"] = NativeToolUtil.Field("string", "Text note type name (default the project's default)."),
        ["offset_mm"] = NativeToolUtil.Field("number", "Paper offset of the note from the element, default 12 mm."),
        ["cloud"] = NativeToolUtil.Field("boolean", "Draw a revision cloud around each element (default true)."),
        ["write_parameter"] = NativeToolUtil.Field("string", "Also append the note to this text parameter, e.g. 'Комментарии'."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "findings");

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var preview = NativeToolUtil.Preview(input);
        var view = input.TryGetValue("view_id", out var vid) && vid.ValueKind == JsonValueKind.Number
            ? doc.GetElement(new ElementId(vid.GetInt64())) as View ?? throw NameResolve.MissingId(vid.GetInt64(), "View")
            : ToolContext.UiDocument(app)?.ActiveView ?? throw new ToolInputException("No active view.");
        if (view.IsTemplate || view.ViewType is not (ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.AreaPlan or ViewType.Section or ViewType.Elevation or ViewType.Detail))
            throw new ToolInputException($"'{view.Name}' is a {view.ViewType}; annotate on a plan, section, elevation or detail view.");
        var items = ToolInput.RequiredArray(input, "findings").EnumerateArray()
            .Select(f => (Id: f.GetProperty("element_id").GetInt64(), Text: f.TryGetProperty("text", out var t) ? t.GetString() ?? "" : ""))
            .Where(f => f.Text.Length > 0).GroupBy(f => f.Id).Select(g => (g.Key, Text: string.Join("\n", g.Select(x => x.Text).Distinct()))).ToList();
        if (items.Count == 0 || items.Count > 500) throw new ToolInputException("Supply 1..500 findings with element_id and text.");
        var typeName = NativeToolUtil.Text(input, "text_type");
        var textTypes = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();
        var typeId = typeName.Length > 0
            ? (textTypes.FirstOrDefault(t => t.Name == typeName) ?? throw NameResolve.Missing(typeName, "Text note type", textTypes.Select(t => t.Name))).Id
            : doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        var cloud = !input.TryGetValue("cloud", out var c) || c.ValueKind != JsonValueKind.False;
        var offsetMm = input.TryGetValue("offset_mm", out var om) && om.ValueKind == JsonValueKind.Number ? om.GetDouble() : 12;
        var paramName = NativeToolUtil.Text(input, "write_parameter");
        var paper = view.Scale / Units.MmPerFoot;   // model feet per paper mm

        var ((notes, clouds, written, skipped, revision), warnings) = NativeToolUtil.Commit(doc, "Claude: norm findings", preview, () =>
        {
            var notesMade = new List<long>(); var cloudsMade = new List<long>(); var writtenIds = new List<long>(); var skippedItems = new List<object>();
            ElementId? revisionId = null;
            if (cloud)
            {
                var revisions = Revision.GetAllRevisionIds(doc);
                if (revisions.Count > 0) revisionId = revisions[^1];
                else { var r = Revision.Create(doc); r.Description = "Нормоконтроль"; revisionId = r.Id; }
            }
            foreach (var (id, text) in items)
            {
                var e = doc.GetElement(new ElementId(id));
                if (e == null) { skippedItems.Add(new { element_id = id, reason = "element not found" }); continue; }
                var anchor = DrawingAnchor.Of(e, view);
                if (anchor == null) { skippedItems.Add(new { element_id = id, reason = "no location" }); continue; }
                var p = DrawingAnchor.OnPlane(view, anchor);
                var pos = p + (view.RightDirection + view.UpDirection) * (offsetMm * paper);
                var note = TextNote.Create(doc, view.Id, pos, text, typeId);
                note.AddLeader(TextNoteLeaderTypes.TNLT_STRAIGHT_L).End = p;
                notesMade.Add(note.Id.Value);
                if (cloud && revisionId != null && DrawingAnchor.Rectangle(view, e, 2 * paper) is { } rect)
                    cloudsMade.Add(RevisionCloud.Create(doc, view, revisionId, rect).Id.Value);
                if (paramName.Length > 0)
                {
                    var prm = e.LookupParameter(paramName);
                    if (prm is { StorageType: StorageType.String, IsReadOnly: false })
                    {
                        var old = prm.AsString() ?? "";
                        if (!old.Contains(text)) prm.Set(old.Length == 0 ? text : old + "; " + text);
                        writtenIds.Add(id);
                    }
                    else skippedItems.Add(new { element_id = id, reason = $"parameter '{paramName}' missing or read-only (note placed)" });
                }
            }
            return (notesMade, cloudsMade, writtenIds, skippedItems, revisionId?.Value);
        });
        return Json.Serialize(new { preview, view = view.Name, notes = notes.Count, note_ids = notes, clouds = clouds.Count, revision_id = revision, parameter_written = written.Count, skipped, revit_warnings = warnings });
    }
}

// Where to point at an element on a drawing.
internal static class DrawingAnchor
{
    public static XYZ? Of(Element e, View? view = null)
    {
        switch (e.Location)
        {
            case LocationPoint lp: return lp.Point;
            case LocationCurve lc: return lc.Curve.Evaluate(0.5, true);
        }
        var box = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
        return box == null ? null : (box.Min + box.Max) / 2;
    }

    public static XYZ OnPlane(View view, XYZ p)
    {
        var n = view.ViewDirection;
        return p - n * (p - view.Origin).DotProduct(n);
    }

    // Element extents in the view's right/up axes as a closed rectangle on the view plane.
    public static IList<Curve>? Rectangle(View view, Element e, double pad)
    {
        var box = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
        if (box == null) return null;
        var t = box.Transform;
        var corners = new[] { 0, 1 }.SelectMany(i => new[] { 0, 1 }.SelectMany(j => new[] { 0, 1 }.Select(k =>
            t.OfPoint(new XYZ(i == 0 ? box.Min.X : box.Max.X, j == 0 ? box.Min.Y : box.Max.Y, k == 0 ? box.Min.Z : box.Max.Z))))).ToList();
        var o = view.Origin; var r = view.RightDirection; var u = view.UpDirection;
        var us = corners.Select(p => (p - o).DotProduct(r)).ToList(); var vs = corners.Select(p => (p - o).DotProduct(u)).ToList();
        double u0 = us.Min() - pad, u1 = us.Max() + pad, v0 = vs.Min() - pad, v1 = vs.Max() + pad;
        if (u1 - u0 < 1e-3 || v1 - v0 < 1e-3) return null;
        XYZ P(double a, double b) => o + r * a + u * b;
        return [Line.CreateBound(P(u0, v0), P(u1, v0)), Line.CreateBound(P(u1, v0), P(u1, v1)), Line.CreateBound(P(u1, v1), P(u0, v1)), Line.CreateBound(P(u0, v1), P(u0, v0))];
    }
}
