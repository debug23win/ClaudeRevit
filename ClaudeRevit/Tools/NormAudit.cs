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
        "Check the model against Russian code requirements. Base rules (always): СП 1.13130.2020 evacuation doors, " +
        "corridors and stairs; СП 54.13330.2022 ceiling heights, minimum room areas, railing heights. Optional sections: " +
        "sp59 (СП 59.13330.2020 accessibility: doors 0.9 m, paths 1.8 m, universal toilet cabin 2.2×2.25 m) and " +
        "fire_distance (СП 4.13130.2013 table 1 between this building and linked buildings; give fire_classes). An office " +
        "rule file (JSON; default %AppData%/ClaudeRevit/norm-rules.json, or rules_file) changes thresholds, disables rules " +
        "and adds rules of its own over the same measurements — no rebuild needed. Rooms are classified by name. " +
        "Thresholds follow clause variants for functional_class, climate_subregion, corridor_occupants. Read-only; returns " +
        "failures with clause, measured vs required and method, plus annotate_input for annotate_norm_findings. " +
        "list_rules=true returns the catalog in force. A screening aid; an engineer confirms applicability and edition.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["rules"] = NativeToolUtil.Array("string", "Rule ids to run (default: base rules plus requested sections). See list_rules."),
        ["sections"] = NativeToolUtil.Array("string", "Extra rule sets: sp59, fire_distance, office (rules from the office file)."),
        ["list_rules"] = NativeToolUtil.Field("boolean", "Return the rule catalog in force only."),
        ["rules_file"] = NativeToolUtil.Field("string", "Office rule file (JSON) instead of the default %AppData%/ClaudeRevit/norm-rules.json."),
        ["level"] = NativeToolUtil.Field("string", "Only elements on this level."),
        ["functional_class"] = NativeToolUtil.Field("string", "Building functional fire-hazard class, e.g. Ф1.3 (selects stair width variant)."),
        ["climate_subregion"] = NativeToolUtil.Field("string", "Climate subregion, e.g. IIВ; IA/IБ/IГ/IД/IVА raise living-room height to 2.7 m."),
        ["corridor_occupants"] = NativeToolUtil.Field("integer", "People evacuating along corridors; more than 50 raises corridor width to 1.2 m."),
        ["apartment_parameter"] = NativeToolUtil.Field("string", "Room parameter holding the apartment number; enables the one-room-apartment variants and excludes in-apartment corridors from evacuation corridor checks."),
        ["fire_classes"] = NativeToolUtil.Any("fire_distance: {\"this\": \"II C0\", \"<link name>\": \"III C1\"} — fire resistance degree and structural fire hazard class per building; \"default\" for the rest."),
        ["overrides"] = NativeToolUtil.Any("Object {rule_id: value} in the rule's unit (mm, m², ratio), for project-specific requirements."),
        ["include_passed"] = NativeToolUtil.Field("boolean", "Also list passing checks (default false)."),
        ["limit"] = NativeToolUtil.Field("integer", "Max findings listed (default 300).")
    });

    private sealed record Finding(string Rule, long ElementId, string Element, string? Level, double Measured, NormThreshold Threshold, bool Passed, string Method, double[]? PointMm);

    public static string DefaultRulesFile => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeRevit", "norm-rules.json");

    internal static NormCatalog LoadCatalog(IReadOnlyDictionary<string, JsonElement> input)
    {
        var path = NativeToolUtil.Text(input, "rules_file");
        var explicitFile = path.Length > 0;
        if (!explicitFile) path = DefaultRulesFile;
        if (!System.IO.File.Exists(path))
        {
            if (explicitFile) throw new ToolInputException($"Rule file not found: {path}");
            return NormRules.Load(null);
        }
        try { return NormRules.Load(System.IO.File.ReadAllText(path), path); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException or InvalidOperationException)
        { throw new ToolInputException($"Rule file {path}: {ex.Message}"); }
    }

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var catalog = LoadCatalog(input);
        var rulesById = catalog.Rules.ToDictionary(r => r.Id);
        if (ToolInput.Flag(input, "list_rules"))
            return Json.Serialize(new
            {
                rules_file = catalog.Source, default_rules_file = DefaultRulesFile, checks = NormRules.Checks,
                rules = catalog.Rules.Select(r => new
                {
                    id = r.Id, section = r.Section, enabled = !catalog.Disabled.Contains(r.Id), document = r.Document, clause = r.Clause, subject = r.Subject, check = r.Check,
                    comparison = r.AtLeast ? ">=" : "<=", value = catalog.Overrides.TryGetValue(r.Id, out var o) ? o : r.Value, unit = r.Unit, requirement = r.Requirement,
                    kinds = r.Kinds?.Select(k => k.ToString()), name_pattern = r.NamePattern
                })
            });

        var doc = NativeToolUtil.Doc(app);
        Func<string, NormRule> rule = id => rulesById.TryGetValue(id, out var r) ? r : throw new ToolInputException($"Unknown norm rule '{id}'. Known: {string.Join(", ", rulesById.Keys)}.");
        var sections = input.TryGetValue("sections", out var sc) && sc.ValueKind == JsonValueKind.Array ? sc.EnumerateArray().Select(e => e.GetString() ?? "").ToHashSet() : new HashSet<string>();
        var selected = input.TryGetValue("rules", out var rs) && rs.ValueKind == JsonValueKind.Array
            ? rs.EnumerateArray().Select(e => rule(e.GetString() ?? "").Id).ToHashSet()
            : catalog.Rules.Where(r => !catalog.Disabled.Contains(r.Id) && (r.Section == "base" || sections.Contains(r.Section))).Select(r => r.Id).ToHashSet();
        var overrides = new Dictionary<string, double>(catalog.Overrides);
        if (input.TryGetValue("overrides", out var ov) && ov.ValueKind == JsonValueKind.Object)
            foreach (var p in ov.EnumerateObject()) overrides[rule(p.Name).Id] = p.Value.GetDouble();
        var baseContext = new NormContext(NativeToolUtil.Text(input, "functional_class"), NativeToolUtil.Text(input, "climate_subregion"),
            input.TryGetValue("corridor_occupants", out var co) && co.ValueKind == JsonValueKind.Number ? co.GetInt32() : null, false, overrides);
        var active = selected.Select(id => rulesById[id]).ToList();
        IEnumerable<NormRule> With(params string[] checks) => active.Where(r => checks.Contains(r.Check));
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
        var notChecked = new List<object>();
        void Record(NormRule r, NormThreshold t, Element e, string label, double measured, string method)
        {
            if (!double.IsFinite(measured)) return;
            var lvl = e.LevelId != ElementId.InvalidElementId ? doc.GetElement(e.LevelId)?.Name : null;
            findings.Add(new(r.Id, e.Id.Value, label, lvl, Math.Round(measured, 1), t, NormRules.Passes(t, measured), method,
                DrawingAnchor.Of(e) is { } p ? NativeToolUtil.Mm(p) : null));
        }
        void Check(NormRule r, Element e, string label, double measured, string method, NormContext? ctx = null) =>
            Record(r, NormRules.Threshold(r, ctx ?? baseContext), e, label, measured, method);
        string RoomLabel(Room r) => $"{r.Number} {DraftingTable.RoomName(r)}".Trim();

        // Doors. "evacuation": into a corridor, hall or stair, or to the outside.
        var doorRules = With("door_clear_width", "door_clear_height").ToList();
        if (doorRules.Count > 0)
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
                var label = $"Дверь {door.Symbol?.Family?.Name} : {door.Name}" + (door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() is { Length: > 0 } m ? $" ({m})" : "");
                (double? Value, string Method)? w = null, h = null;
                foreach (var r in doorRules)
                {
                    if (r.DoorScope != "all" && !evac) continue;
                    if (r.Check == "door_clear_width")
                    {
                        w ??= Dimension(door, ["Ширина в свету", "Clear Width", "ADSK_Размер_Ширина в свету"], BuiltInParameter.DOOR_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM);
                        if (w.Value.Value is { } wv) Check(r, door, label, wv, w.Value.Method);
                    }
                    else
                    {
                        h ??= Dimension(door, ["Высота в свету", "Clear Height", "ADSK_Размер_Высота в свету"], BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM);
                        if (h.Value.Value is { } hv) Check(r, door, label, hv, h.Value.Method);
                    }
                }
            }
        }

        // Rooms.
        var roomRules = With("corridor_width", "room_width", "room_length", "room_area", "room_height").ToList();
        var patterns = roomRules.Where(r => r.NamePattern != null).ToDictionary(r => r.Id, r => new System.Text.RegularExpressions.Regex(r.NamePattern!, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        foreach (var room in roomRules.Count == 0 ? [] : rooms.Where(r => OnLevel(r.LevelId)))
        {
            var name = DraftingTable.RoomName(room);
            var kind = NormRules.Classify(name);
            var ctx = baseContext with { OneRoomApartment = oneRoom.Contains(room.Id) };
            var areaM2 = room.Area * 0.09290304;
            var widthMm = NormRules.EquivalentWidth(areaM2, room.Perimeter * 0.3048) * 1000;
            (double Height, string Method)? height = null;
            foreach (var r in roomRules)
            {
                var matches = (r.Kinds?.Contains(kind) ?? false) || (patterns.TryGetValue(r.Id, out var rx) && rx.IsMatch(name));
                if (!matches) continue;
                switch (r.Check)
                {
                    case "corridor_width":
                        // In-apartment corridors are not common evacuation corridors.
                        if (Apartment(room) is { Length: > 0 }) break;
                        Check(r, room, RoomLabel(room), widthMm, "ширина равновеликого прямоугольника по площади и периметру помещения");
                        break;
                    case "room_width": Check(r, room, RoomLabel(room), widthMm, "ширина равновеликого прямоугольника", ctx); break;
                    case "room_length": Check(r, room, RoomLabel(room), widthMm > 0 ? areaM2 * 1e6 / widthMm : 0, "длина равновеликого прямоугольника", ctx); break;
                    case "room_area": Check(r, room, RoomLabel(room), areaM2, "площадь помещения Revit", ctx); break;
                    case "room_height":
                        height ??= ClearHeight(doc, room, view3d);
                        Check(r, room, RoomLabel(room), height.Value.Height, height.Value.Method, ctx);
                        break;
                }
            }
        }

        var stairRules = With("stair_run_width", "stair_riser", "stair_tread", "stair_slope").ToList();
        if (stairRules.Count > 0)
        {
            foreach (var stairs in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Stairs).WhereElementIsNotElementType().OfType<Stairs>())
            {
                var baseLevel = stairs.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
                if (!OnLevel(baseLevel)) continue;
                var label = $"Лестница {stairs.Name} (id {stairs.Id.Value})";
                var riser = stairs.ActualRiserHeight * Units.MmPerFoot; var tread = stairs.ActualTreadDepth * Units.MmPerFoot;
                var widths = stairs.GetStairsRuns().Select(id => doc.GetElement(id)).OfType<StairsRun>().Select(r => r.ActualRunWidth * Units.MmPerFoot).ToList();
                foreach (var r in stairRules)
                    switch (r.Check)
                    {
                        case "stair_riser": Check(r, stairs, label, riser, "фактическая высота подступенка"); break;
                        case "stair_tread": Check(r, stairs, label, tread, "фактическая глубина проступи"); break;
                        case "stair_slope" when tread > 0: Check(r, stairs, label, riser / tread, "отношение подступенка к проступи"); break;
                        case "stair_run_width" when widths.Count > 0: Check(r, stairs, label, widths.Min(), "наименьшая фактическая ширина марша"); break;
                    }
            }
        }

        var railingRules = With("railing_height_stair", "railing_height_other").ToList();
        if (railingRules.Count > 0)
        {
            foreach (var railing in new FilteredElementCollector(doc).OfClass(typeof(Railing)).Cast<Railing>())
            {
                if (doc.GetElement(railing.GetTypeId()) is not RailingType type) continue;
                var onStair = railing.HasHost && doc.GetElement(railing.HostId) is Stairs;
                if (!onStair && !OnLevel(railing.LevelId)) continue;
                foreach (var r in railingRules.Where(r => r.Check == (onStair ? "railing_height_stair" : "railing_height_other")))
                    Check(r, railing, $"Ограждение {type.Name} (id {railing.Id.Value})", type.TopRailHeight * Units.MmPerFoot,
                        onStair ? "высота верхнего поручня типа; ограждение на лестнице" : "высота верхнего поручня типа; ограждение не на лестнице — проверено как балкон/лоджия/наружное");
            }
        }

        foreach (var r in With("building_distance"))
            FireDistances(doc, input, r, overrides, findings, notChecked);

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
            rules_file = catalog.Source,
            rules_run = selected.OrderBy(x => x),
            summary = findings.GroupBy(f => f.Rule).Select(g => new { rule = g.Key, checked_count = g.Count(), failed = g.Count(f => !f.Passed) }),
            failed_count = failed.Count,
            findings = (ToolInput.Flag(input, "include_passed") ? findings.OrderBy(f => f.Passed) : failed.AsEnumerable()).Take(limit).Select(Row),
            truncated = (ToolInput.Flag(input, "include_passed") ? findings.Count : failed.Count) > limit,
            not_checked = notChecked,
            annotate_input = failed.Take(limit).GroupBy(f => f.ElementId).Select(g => new { element_id = g.Key, text = string.Join("\n", g.Select(Note)) }),
            disclaimer = "Пороговые значения — общий случай указанных пунктов с учётом переданного контекста. Проверьте применимость норм, редакцию и исключения для объекта; результат — инструмент предварительной проверки, а не заключение экспертизы."
        });
    }

    // Fire distances (СП 4.13130.2013 table 1) between this building and each loaded Revit link
    // treated as a building, and between links. Footprints are the convex hulls of walls in plan —
    // never farther than the real outline, so a distance that passes really passes.
    private static void FireDistances(Document doc, IReadOnlyDictionary<string, JsonElement> input, NormRule rule,
        IReadOnlyDictionary<string, double> overrides, List<Finding> findings, List<object> notChecked)
    {
        var classes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (input.TryGetValue("fire_classes", out var fc) && fc.ValueKind == JsonValueKind.Object)
            foreach (var p in fc.EnumerateObject()) classes[p.Name] = p.Value.GetString() ?? "";
        var buildings = new List<(string Name, Element Anchor, List<(double X, double Y)> Hull)>();
        List<(double X, double Y)> Footprint(Document d, Transform t) => NormRules.Hull(new FilteredElementCollector(d).OfClass(typeof(Wall)).Cast<Wall>()
            .Select(w => w.get_BoundingBox(null)).Where(b => b != null)
            .SelectMany(b => new[] { new XYZ(b!.Min.X, b.Min.Y, 0), new XYZ(b.Max.X, b.Min.Y, 0), new XYZ(b.Max.X, b.Max.Y, 0), new XYZ(b.Min.X, b.Max.Y, 0) })
            .Select(p => t.OfPoint(p)).Select(p => (p.X * Units.MmPerFoot, p.Y * Units.MmPerFoot)));
        var own = Footprint(doc, Transform.Identity);
        var anchor = new FilteredElementCollector(doc).OfClass(typeof(Wall)).FirstElement();
        if (own.Count >= 3 && anchor != null) buildings.Add(("this", anchor, own));
        foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            var ld = link.GetLinkDocument();
            if (ld == null) { notChecked.Add(new { rule = rule.Id, link = link.Name, reason = "link not loaded" }); continue; }
            var hull = Footprint(ld, link.GetTotalTransform());
            if (hull.Count >= 3) buildings.Add((link.Name, link, hull));
        }
        if (buildings.Count < 2) { notChecked.Add(new { rule = rule.Id, reason = "needs this model and at least one linked building (Revit link with walls)" }); return; }
        string? ClassOf(string name) => classes.TryGetValue(name, out var c) ? c : classes.FirstOrDefault(kv => name.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)).Value ?? (classes.TryGetValue("default", out var d) ? d : null);
        for (int i = 0; i < buildings.Count; i++)
            for (int j = i + 1; j < buildings.Count; j++)
            {
                var a = buildings[i]; var b = buildings[j];
                var ca = ClassOf(a.Name); var cb = ClassOf(b.Name);
                double required; string basis;
                if (overrides.TryGetValue(rule.Id, out var o)) { required = o; basis = "project override"; }
                else if (ca == null || cb == null) { notChecked.Add(new { rule = rule.Id, pair = $"{a.Name} — {b.Name}", reason = "give fire_classes for both buildings (e.g. \"II C0\")" }); continue; }
                else
                {
                    try { required = NormRules.FireDistanceMm(ca, cb); basis = $"{ca} / {cb}"; }
                    catch (ArgumentException ex) { notChecked.Add(new { rule = rule.Id, pair = $"{a.Name} — {b.Name}", reason = ex.Message }); continue; }
                }
                var distance = NormRules.FootprintDistance(a.Hull, b.Hull);
                var t = new NormThreshold(rule, required, basis);
                findings.Add(new(rule.Id, b.Anchor.Id.Value, $"{a.Name} — {b.Name}", null, Math.Round(distance), t, NormRules.Passes(t, distance),
                    "кратчайшее расстояние между выпуклыми контурами стен в плане", DrawingAnchor.Of(b.Anchor) is { } p ? NativeToolUtil.Mm(p) : null));
            }
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
