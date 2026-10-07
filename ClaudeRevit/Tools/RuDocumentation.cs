using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// Russian documentation set: ТЭП, explications, finish schedule, title block, schedule placement,
// room numbering. Ideas from the NewLevelHub fork of mcp-servers-for-revit (export_tep_data,
// create_floor_explication, create_finish_schedule, fit_schedule_to_sheet; MIT); the
// implementations are native. Tables that Revit schedules cannot express (rooms grouped by floor
// type or finish with a compact room-number list) are drawn as drafting-view tables: a snapshot,
// re-run with replace=true to refresh.
internal static class DraftingTable
{
    public static long Render(Document doc, string name, bool replace, string title, string[] headers, double[] widthsMm,
        IReadOnlyList<string[]> rows, ElementId textTypeId)
    {
        var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewDrafting)).Cast<ViewDrafting>().FirstOrDefault(v => v.Name == name);
        if (existing != null)
        {
            if (!replace) throw new ToolInputException($"A drafting view '{name}' already exists; pass replace=true to redraw it or choose another name.");
            doc.Delete(existing.Id);
        }
        var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.Drafting)
            ?? throw new InvalidOperationException("The project has no drafting view type.");
        var view = ViewDrafting.Create(doc, vft.Id);
        view.Name = name;
        view.Scale = 1;
        var textH = (doc.GetElement(textTypeId) as TextNoteType)?.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() * Units.MmPerFoot ?? 2.5;
        if (!(textH > 0)) textH = 2.5;
        double F(double mm) => mm / Units.MmPerFoot;
        var xs = new List<double> { 0 };
        foreach (var w in widthsMm) xs.Add(xs[^1] + w);
        var opts = new TextNoteOptions(textTypeId) { HorizontalAlignment = HorizontalTextAlignment.Left, VerticalAlignment = VerticalTextAlignment.Top };
        var minWidth = TextNote.GetMinimumAllowedWidth(doc, textTypeId);
        TextNote.Create(doc, view.Id, new XYZ(0, F(3 * textH), 0), title, opts);
        double y = 0;
        var ys = new List<double> { 0 };
        foreach (var (cells, header) in new[] { (headers, true) }.Concat(rows.Select(r => (r, false))))
        {
            var lines = cells.Select((c, i) => RoomNumbering.Lines(c, widthsMm[i], textH)).DefaultIfEmpty(1).Max();
            var h = Math.Max(header ? 15 : 8, lines * textH * 1.7 + 3);
            for (int i = 0; i < cells.Length && i < widthsMm.Length; i++)
                if (!string.IsNullOrEmpty(cells[i]))
                    TextNote.Create(doc, view.Id, new XYZ(F(xs[i] + 1), F(y - 1.5), 0), Math.Max(minWidth, F(widthsMm[i] - 2)), cells[i], opts);
            y -= h;
            ys.Add(y);
        }
        foreach (var yy in ys) doc.Create.NewDetailCurve(view, Line.CreateBound(new XYZ(0, F(yy), 0), new XYZ(F(xs[^1]), F(yy), 0)));
        foreach (var x in xs) doc.Create.NewDetailCurve(view, Line.CreateBound(new XYZ(F(x), 0, 0), new XYZ(F(x), F(y), 0)));
        return view.Id.Value;
    }

    public static ElementId TextType(Document doc, IReadOnlyDictionary<string, JsonElement> input)
    {
        var name = NativeToolUtil.Text(input, "text_type");
        if (name.Length == 0) return doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        var types = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();
        return (types.FirstOrDefault(t => t.Name == name) ?? throw NameResolve.Missing(name, "Text note type", types.Select(t => t.Name))).Id;
    }

    public static List<Room> Rooms(Document doc, IReadOnlyDictionary<string, JsonElement> input)
    {
        var levelName = NativeToolUtil.Text(input, "level");
        Level? level = null;
        if (levelName.Length > 0)
            level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == levelName) ?? throw NameResolve.MissingLevel(doc, levelName);
        return new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>()
            .Where(r => r.Area > 0 && r.Location != null && (level == null || r.LevelId == level.Id)).ToList();
    }

    // Room.Name is "name number"; the name alone is the ROOM_NAME parameter.
    public static string RoomName(Room r) => r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";

    public static string Sq(double ft2) => (ft2 * 0.09290304).ToString("0.00");
}

public sealed class ComputeTep : IRevitTool
{
    public string Name => "compute_tep";
    public string Description =>
        "Технико-экономические показатели (ТЭП) from rooms and levels: footprint and construction volume (approximate, " +
        "from floor slabs), above-ground and total storeys, room area, living area, apartment area with and without summer " +
        "spaces (loggias × 0.5, balconies × 0.3), apartment count by number of living rooms. Rooms are classified by name; " +
        "apartments come from apartment_parameter. Read-only unless render=true, which draws the table on a drafting view " +
        "(preview defaults true).";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["apartment_parameter"] = NativeToolUtil.Field("string", "Room parameter with the apartment number (e.g. ADSK_Номер квартиры)."),
        ["ground_elevation_mm"] = NativeToolUtil.Field("number", "Ground level elevation; levels below it are underground (default 0)."),
        ["render"] = NativeToolUtil.Field("boolean", "Draw the table on a drafting view."),
        ["view_name"] = NativeToolUtil.Field("string", "Drafting view name (default 'ТЭП')."),
        ["replace"] = NativeToolUtil.Field("boolean", "Redraw an existing view of that name."),
        ["text_type"] = NativeToolUtil.Field("string", "Text note type for the table."),
        ["preview"] = NativeToolUtil.Field("boolean", "With render: default true.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var apt = NativeToolUtil.Text(input, "apartment_parameter");
        var ground = input.TryGetValue("ground_elevation_mm", out var g) && g.ValueKind == JsonValueKind.Number ? g.GetDouble() : 0;
        var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>()
            .Where(r => r.Area > 0 && r.Location != null && r.Level != null).ToList();
        if (apt.Length > 0 && rooms.Count > 0 && rooms.All(r => r.LookupParameter(apt) == null))
            throw NameResolve.Missing(apt, "Room parameter", rooms[0].Parameters.Cast<Parameter>().Select(p => p.Definition?.Name));
        var tepRooms = rooms.Select(r => new TepRoom(r.Level.Name, r.Level.Elevation * Units.MmPerFoot, r.Area * 0.09290304, NormRules.Classify(DraftingTable.RoomName(r)),
            apt.Length == 0 ? null : r.LookupParameter(apt) is { } p ? (p.StorageType == StorageType.String ? p.AsString() : p.AsValueString()) : null)).ToList();
        var slabArea = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Floors).WhereElementIsNotElementType()
            .Where(f => f.LevelId != ElementId.InvalidElementId)
            .GroupBy(f => f.LevelId).ToDictionary(gr => gr.Key, gr => gr.Sum(f => f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0) * 0.09290304);
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
        var tepLevels = levels.Select((l, i) => (l, i)).Where(x => slabArea.ContainsKey(x.l.Id))
            .Select(x => new TepLevel(x.l.Name, x.l.Elevation * Units.MmPerFoot, slabArea[x.l.Id],
                x.i + 1 < levels.Count ? (levels[x.i + 1].Elevation - x.l.Elevation) * Units.MmPerFoot : 0)).ToList();
        var tep = TepCalculator.Compute(tepRooms, tepLevels, ground);
        var byLevel = tepRooms.GroupBy(r => (r.Level, r.LevelElevationMm)).OrderBy(gr => gr.Key.LevelElevationMm)
            .Select(gr => new { level = gr.Key.Level, rooms = gr.Count(), area_m2 = Math.Round(gr.Sum(r => r.AreaM2), 2) });
        long? viewId = null; List<string> warnings = new();
        var preview = NativeToolUtil.Preview(input);
        if (ToolInput.Flag(input, "render"))
        {
            var name = NativeToolUtil.Text(input, "view_name", "ТЭП");
            var textType = DraftingTable.TextType(doc, input);
            (viewId, warnings) = NativeToolUtil.Commit(doc, "Claude: ТЭП", preview, () => (long?)DraftingTable.Render(doc, name, ToolInput.Flag(input, "replace"),
                "Технико-экономические показатели", ["Наименование показателя", "Ед. изм.", "Значение", "Примечание"], [70, 15, 25, 75],
                tep.Select(t => new[] { t.Name, t.Unit, t.Value.ToString("0.##"), t.Method }).ToList(), textType));
        }
        return Services.Json.Serialize(new
        {
            indicators = tep.Select(t => new { name = t.Name, unit = t.Unit, value = t.Value, method = t.Method }),
            by_level = byLevel,
            apartments_marked = tepRooms.Any(r => !string.IsNullOrWhiteSpace(r.Apartment)),
            preview = viewId != null ? preview : (bool?)null, view_id = viewId, revit_warnings = warnings
        });
    }
}

public sealed class CreateRoomExplication : IRevitTool
{
    public string Name => "create_room_explication";
    public string Description =>
        "Экспликация помещений as a LIVE native room schedule: Номер, Наименование, Площадь м² (0.01) and optionally a " +
        "category column, grouped by level with level subtotals and a grand total. Optional level filter. " +
        "preview defaults true (created then rolled back).";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["name"] = NativeToolUtil.Field("string", "Schedule name (default 'Экспликация помещений' + level)."),
        ["level"] = NativeToolUtil.Field("string", "Only rooms on this level."),
        ["category_parameter"] = NativeToolUtil.Field("string", "Room parameter for the 'Кат. помещения' column (fire/explosion category), optional."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var levelName = NativeToolUtil.Text(input, "level");
        var level = levelName.Length == 0 ? null
            : new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == levelName) ?? throw NameResolve.MissingLevel(doc, levelName);
        var name = NativeToolUtil.Text(input, "name", "Экспликация помещений" + (level != null ? " — " + level.Name : ""));
        if (new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Any(v => v.Name == name))
            throw new ToolInputException($"A schedule named '{name}' already exists.");
        var categoryParam = NativeToolUtil.Text(input, "category_parameter");
        var preview = NativeToolUtil.Preview(input);
        var (id, warnings) = NativeToolUtil.Commit(doc, "Claude: экспликация помещений", preview, () =>
        {
            var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Rooms));
            schedule.Name = name;
            var def = schedule.Definition;
            var fields = def.GetSchedulableFields();
            ScheduleField Add(BuiltInParameter bip, string heading)
            {
                var sf = fields.FirstOrDefault(f => f.ParameterId == new ElementId(bip)) ?? throw new InvalidOperationException($"Room field {bip} is not schedulable.");
                var field = def.AddField(sf); field.ColumnHeading = heading; return field;
            }
            var levelField = Add(BuiltInParameter.ROOM_LEVEL_ID, "Уровень");
            levelField.IsHidden = true;
            var number = Add(BuiltInParameter.ROOM_NUMBER, "Номер помещения");
            Add(BuiltInParameter.ROOM_NAME, "Наименование");
            var area = Add(BuiltInParameter.ROOM_AREA, "Площадь, м²");
            area.SetFormatOptions(new FormatOptions(UnitTypeId.SquareMeters) { Accuracy = 0.01 });
            area.DisplayType = ScheduleFieldDisplayType.Totals;
            if (categoryParam.Length > 0)
            {
                var sf = fields.FirstOrDefault(f => f.GetName(doc) == categoryParam) ?? throw NameResolve.Missing(categoryParam, "Room schedule field", fields.Select(f => f.GetName(doc)));
                def.AddField(sf).ColumnHeading = "Кат. помещения";
            }
            if (level != null) def.AddFilter(new ScheduleFilter(levelField.FieldId, ScheduleFilterType.Equal, level.Id));
            def.AddFilter(new ScheduleFilter(area.FieldId, ScheduleFilterType.GreaterThan, 0.0));
            def.AddSortGroupField(new ScheduleSortGroupField(levelField.FieldId, ScheduleSortOrder.Ascending) { ShowHeader = true, ShowFooter = true });
            def.AddSortGroupField(new ScheduleSortGroupField(number.FieldId, ScheduleSortOrder.Ascending));
            def.IsItemized = true;
            def.ShowGrandTotal = true;
            def.ShowGrandTotalTitle = true;
            def.GrandTotalTitle = "Итого";
            return schedule.Id.Value;
        });
        return Services.Json.Serialize(new { preview, schedule_id = id, name, level = level?.Name, revit_warnings = warnings });
    }
}

public sealed class CreateFloorExplication : IRevitTool
{
    public string Name => "create_floor_explication";
    public string Description =>
        "Экспликация полов (ГОСТ 21.501 layout) on a drafting view: for each floor type, the rooms it covers (compact " +
        "number list), the type, its layer build-up (material — thickness, top to bottom) and the area. The floor under " +
        "a room is the topmost floor whose top face contains the room point. Snapshot: re-run with replace=true after " +
        "changes. Optional level filter. preview defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["level"] = NativeToolUtil.Field("string", "Only rooms on this level."),
        ["view_name"] = NativeToolUtil.Field("string", "Drafting view name (default 'Экспликация полов' + level)."),
        ["replace"] = NativeToolUtil.Field("boolean", "Redraw an existing view of that name."),
        ["text_type"] = NativeToolUtil.Field("string", "Text note type."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var rooms = DraftingTable.Rooms(doc, input);
        if (rooms.Count == 0) throw new ToolInputException("No placed rooms found.");
        var floors = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().ToList();
        var faces = floors.ToDictionary(f => f.Id, f => HostObjectUtils.GetTopFaces(f).Select(r => f.GetGeometryObjectFromReference(r) as Face).OfType<Face>().ToList());
        var unmatched = new List<string>();
        var groups = new Dictionary<ElementId, List<Room>>();
        foreach (var room in rooms)
        {
            var p = ((LocationPoint)room.Location).Point;
            Floor? best = null; double bestZ = double.MinValue;
            foreach (var f in floors)
            {
                var bb = f.get_BoundingBox(null);
                if (bb == null || p.X < bb.Min.X || p.X > bb.Max.X || p.Y < bb.Min.Y || p.Y > bb.Max.Y || bb.Max.Z < p.Z - 3 || bb.Min.Z > p.Z + 3) continue;
                foreach (var face in faces[f.Id])
                {
                    var hit = face.Project(p);
                    if (hit == null) continue;
                    var q = hit.XYZPoint;
                    if (Math.Abs(q.X - p.X) > 1e-3 || Math.Abs(q.Y - p.Y) > 1e-3) continue;
                    if (q.Z > bestZ) { bestZ = q.Z; best = f; }
                }
            }
            if (best == null) { unmatched.Add(room.Number); continue; }
            if (!groups.TryGetValue(best.GetTypeId(), out var list)) groups[best.GetTypeId()] = list = new();
            list.Add(room);
        }
        var rows = groups.Select(kv =>
        {
            var type = doc.GetElement(kv.Key) as FloorType;
            var layers = type?.GetCompoundStructure()?.GetLayers().Select((l, i) =>
                $"{i + 1}. {(doc.GetElement(l.MaterialId) as Material)?.Name ?? "—"} — {Math.Round(l.Width * Units.MmPerFoot)} мм") ?? [];
            return new[]
            {
                RoomNumbering.CompactList(kv.Value.Select(r => r.Number)),
                type?.Name ?? "—",
                type?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.AsString() ?? "",
                string.Join("\n", layers),
                DraftingTable.Sq(kv.Value.Sum(r => r.Area))
            };
        }).OrderBy(r => RoomNumbering.NaturalKey(r[0])).ToList();
        var levelName = NativeToolUtil.Text(input, "level");
        var name = NativeToolUtil.Text(input, "view_name", "Экспликация полов" + (levelName.Length > 0 ? " — " + levelName : ""));
        var preview = NativeToolUtil.Preview(input);
        var textType = DraftingTable.TextType(doc, input);
        var (viewId, warnings) = NativeToolUtil.Commit(doc, "Claude: экспликация полов", preview, () => DraftingTable.Render(doc, name, ToolInput.Flag(input, "replace"),
            "Экспликация полов", ["Номер помещения", "Тип пола", "Схема пола или тип пола по серии", "Данные элементов пола (наименование, толщина)", "Площадь, м²"],
            [25, 25, 40, 75, 20], rows, textType));
        return Services.Json.Serialize(new { preview, view_id = viewId, view_name = name, rows = rows.Select(r => new { rooms = r[0], floor_type = r[1], area_m2 = r[4] }), rooms_without_floor = unmatched, revit_warnings = warnings });
    }
}

public sealed class CreateFinishSchedule : IRevitTool
{
    public string Name => "create_finish_schedule";
    public string Description =>
        "Ведомость отделки помещений on a drafting view: rooms grouped by identical ceiling and wall finishes (room " +
        "parameters, default the built-in Ceiling/Wall Finish), with ceiling area (= room area) and wall area " +
        "(perimeter × height − door and window openings in the room). Height: wall_height_mm, else the measured " +
        "floor-to-ceiling height. Snapshot: re-run with replace=true. preview defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["level"] = NativeToolUtil.Field("string", "Only rooms on this level."),
        ["ceiling_parameter"] = NativeToolUtil.Field("string", "Room parameter with the ceiling finish (default built-in Ceiling Finish)."),
        ["wall_parameter"] = NativeToolUtil.Field("string", "Room parameter with the wall finish (default built-in Wall Finish)."),
        ["wall_height_mm"] = NativeToolUtil.Field("number", "Finish height for wall area (default measured ceiling height)."),
        ["view_name"] = NativeToolUtil.Field("string", "Drafting view name (default 'Ведомость отделки помещений' + level)."),
        ["replace"] = NativeToolUtil.Field("boolean", "Redraw an existing view of that name."),
        ["text_type"] = NativeToolUtil.Field("string", "Text note type."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var rooms = DraftingTable.Rooms(doc, input);
        if (rooms.Count == 0) throw new ToolInputException("No placed rooms found.");
        string Finish(Room r, string param, BuiltInParameter fallback)
        {
            var p = param.Length > 0 ? r.LookupParameter(param) ?? throw NameResolve.Missing(param, "Room parameter", r.Parameters.Cast<Parameter>().Select(x => x.Definition?.Name)) : r.get_Parameter(fallback);
            var v = p == null ? null : p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
            return string.IsNullOrWhiteSpace(v) ? "—" : v.Trim();
        }
        var ceilingParam = NativeToolUtil.Text(input, "ceiling_parameter"); var wallParam = NativeToolUtil.Text(input, "wall_parameter");
        double? fixedHeight = input.TryGetValue("wall_height_mm", out var wh) && wh.ValueKind == JsonValueKind.Number ? wh.GetDouble() / Units.MmPerFoot : null;
        var phase = doc.Phases.Size > 0 ? doc.Phases.get_Item(doc.Phases.Size - 1) : null;
        var openings = new Dictionary<ElementId, double>();
        foreach (var fi in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter([BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows]))
                     .WhereElementIsNotElementType().OfType<FamilyInstance>())
        {
            double Dim(BuiltInParameter a, BuiltInParameter b) =>
                new Element?[] { fi, fi.Symbol }.Select(e => e?.get_Parameter(a) ?? e?.get_Parameter(b)).FirstOrDefault(p => p is { HasValue: true } && p.AsDouble() > 0)?.AsDouble() ?? 0;
            var area = Dim(BuiltInParameter.DOOR_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM) * Dim(BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM);
            if (area <= 0) continue;
            foreach (var r in new[] { phase != null ? fi.get_FromRoom(phase) : fi.FromRoom, phase != null ? fi.get_ToRoom(phase) : fi.ToRoom }.Where(r => r != null).Select(r => r!.Id).Distinct())
                openings[r] = openings.GetValueOrDefault(r) + area;
        }
        var view3d = fixedHeight == null ? AuditNorms.AuditView(doc) : null;
        var data = rooms.Select(r =>
        {
            var h = fixedHeight ?? AuditNorms.ClearHeight(doc, r, view3d).Height / Units.MmPerFoot;
            var walls = Math.Max(0, r.Perimeter * h - openings.GetValueOrDefault(r.Id));
            return (Room: r, Ceiling: Finish(r, ceilingParam, BuiltInParameter.ROOM_FINISH_CEILING), Wall: Finish(r, wallParam, BuiltInParameter.ROOM_FINISH_WALL), Walls: walls);
        }).ToList();
        var rows = data.GroupBy(d => (d.Ceiling, d.Wall)).Select(gr => new[]
        {
            RoomNumbering.CompactList(gr.Select(d => d.Room.Number)), gr.Key.Ceiling, DraftingTable.Sq(gr.Sum(d => d.Room.Area)),
            gr.Key.Wall, DraftingTable.Sq(gr.Sum(d => d.Walls)), ""
        }).OrderBy(r => RoomNumbering.NaturalKey(r[0])).ToList();
        var levelName = NativeToolUtil.Text(input, "level");
        var name = NativeToolUtil.Text(input, "view_name", "Ведомость отделки помещений" + (levelName.Length > 0 ? " — " + levelName : ""));
        var preview = NativeToolUtil.Preview(input);
        var textType = DraftingTable.TextType(doc, input);
        var (viewId, warnings) = NativeToolUtil.Commit(doc, "Claude: ведомость отделки", preview, () => DraftingTable.Render(doc, name, ToolInput.Flag(input, "replace"),
            "Ведомость отделки помещений", ["Наименование или номер помещения", "Потолок: вид отделки", "Площадь, м²", "Стены и перегородки: вид отделки", "Площадь, м²", "Примечание"],
            [35, 40, 18, 45, 18, 29], rows, textType));
        return Services.Json.Serialize(new
        {
            preview, view_id = viewId, view_name = name,
            rows = rows.Select(r => new { rooms = r[0], ceiling = r[1], ceiling_m2 = r[2], walls = r[3], walls_m2 = r[4] }),
            rooms_without_finish = data.Where(d => d.Ceiling == "—" && d.Wall == "—").Select(d => d.Room.Number),
            revit_warnings = warnings
        });
    }
}

public sealed class FillTitleBlock : IRevitTool
{
    public string Name => "fill_title_block";
    public string Description =>
        "Fill title block (штамп) fields on sheets: values {parameter name: text}, e.g. {'Разработал': 'Иванов', " +
        "'Проверил': 'Петров', 'Стадия': 'Р'}. Each name is looked up on the sheet, then its title block instance, then " +
        "(project_info=true) Project Information. Sheets: sheet_numbers, sheet_ids or all_sheets=true. Unknown names are " +
        "reported with the closest available names. preview defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["values"] = NativeToolUtil.Any("Object {parameter name: value}."),
        ["sheet_numbers"] = NativeToolUtil.Array("string", "Sheet numbers to fill."),
        ["sheet_ids"] = NativeToolUtil.Array("integer", "Sheet element ids to fill."),
        ["all_sheets"] = NativeToolUtil.Field("boolean", "Fill every sheet."),
        ["project_info"] = NativeToolUtil.Field("boolean", "Fall back to Project Information for names not on the sheet or title block."),
        ["overwrite"] = NativeToolUtil.Field("boolean", "Replace non-empty values (default true)."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "values");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var values = ToolInput.Required(input, "values");
        if (values.ValueKind != JsonValueKind.Object) throw new ToolInputException("values must be an object {parameter name: value}.");
        var pairs = values.EnumerateObject().Select(p => (p.Name, Value: p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString())).ToList();
        var all = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
        List<ViewSheet> sheets;
        if (ToolInput.Flag(input, "all_sheets")) sheets = all;
        else if (input.TryGetValue("sheet_numbers", out var sn) && sn.ValueKind == JsonValueKind.Array)
            sheets = sn.EnumerateArray().Select(n => n.GetString() ?? "").Select(n => all.FirstOrDefault(s => s.SheetNumber == n) ?? throw NameResolve.Missing(n, "Sheet number", all.Select(s => s.SheetNumber))).ToList();
        else if (input.TryGetValue("sheet_ids", out var si) && si.ValueKind == JsonValueKind.Array)
            sheets = NativeToolUtil.Ids(si).Select(id => doc.GetElement(id) as ViewSheet ?? throw NameResolve.MissingId(id.Value, "Sheet")).ToList();
        else throw new ToolInputException("Choose sheets: sheet_numbers, sheet_ids or all_sheets=true.");
        var useProject = ToolInput.Flag(input, "project_info");
        var overwrite = !input.TryGetValue("overwrite", out var ow) || ow.ValueKind != JsonValueKind.False;
        var preview = NativeToolUtil.Preview(input);
        var ((written, unknown, failed), warnings) = NativeToolUtil.Commit(doc, "Claude: штамп", preview, () =>
        {
            var w = new Dictionary<string, Dictionary<string, int>>(); var unk = new Dictionary<string, string>(); var fail = new List<object>();
            foreach (var sheet in sheets)
            {
                var tb = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstOrDefault();
                foreach (var (name, value) in pairs)
                {
                    var targets = new (string Where, Element? E)[] { ("sheet", sheet), ("title_block", tb), ("project_info", useProject ? doc.ProjectInformation : null) };
                    var hit = targets.Select(t => (t.Where, P: t.E?.LookupParameter(name))).FirstOrDefault(t => t.P != null);
                    if (hit.P == null)
                    {
                        unk[name] = NameResolve.NotFound(name, "Parameter", targets.Where(t => t.E != null).SelectMany(t => t.E!.Parameters.Cast<Parameter>().Select(p => p.Definition?.Name)).Distinct());
                        continue;
                    }
                    if (hit.P.IsReadOnly) { fail.Add(new { sheet = sheet.SheetNumber, parameter = name, reason = "read-only" }); continue; }
                    var current = hit.P.StorageType == StorageType.String ? hit.P.AsString() : hit.P.AsValueString();
                    if (!overwrite && !string.IsNullOrEmpty(current)) continue;
                    var ok = hit.P.StorageType switch
                    {
                        StorageType.String => hit.P.Set(value),
                        StorageType.Integer => int.TryParse(value, out var iv) && hit.P.Set(iv),
                        StorageType.Double => hit.P.SetValueString(value),
                        _ => false
                    };
                    if (!ok) { fail.Add(new { sheet = sheet.SheetNumber, parameter = name, reason = $"value '{value}' not accepted by a {hit.P.StorageType} parameter" }); continue; }
                    if (!w.TryGetValue(name, out var byWhere)) w[name] = byWhere = new();
                    byWhere[hit.Where] = byWhere.GetValueOrDefault(hit.Where) + 1;
                    if (hit.Where == "project_info") break;   // project-wide: once is enough
                }
            }
            return (w, unk, fail);
        });
        return Services.Json.Serialize(new { preview, sheets = sheets.Count, written, unknown = unknown.Values, failed, revit_warnings = warnings });
    }
}

public sealed class FitScheduleToSheet : IRevitTool
{
    public string Name => "fit_schedule_to_sheet";
    public string Description =>
        "Place a schedule on a sheet inside the drawing frame (ГОСТ Р 21.101: frame 20 mm left, 5 mm elsewhere): " +
        "position above_stamp (default; right-aligned, bottom on top of the main stamp), top_right or top_left. When it " +
        "is taller than the free height and split=true (default) the schedule is split into segments placed side by side " +
        "right to left. Reports any segment that still does not fit. preview defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["schedule"] = NativeToolUtil.Field("string", "Schedule name (or use schedule_id)."),
        ["schedule_id"] = NativeToolUtil.Field("integer", "Schedule element id."),
        ["sheet_number"] = NativeToolUtil.Field("string", "Target sheet number (or use sheet_id)."),
        ["sheet_id"] = NativeToolUtil.Field("integer", "Target sheet id."),
        ["position"] = NativeToolUtil.Field("string", "above_stamp | top_right | top_left."),
        ["stamp_height_mm"] = NativeToolUtil.Field("number", "Main stamp height (default 55, form 3; 40 for form 4)."),
        ["margin_mm"] = NativeToolUtil.Field("number", "Gap to the frame and between segments (default 5)."),
        ["split"] = NativeToolUtil.Field("boolean", "Split into segments when too tall (default true)."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule).ToList();
        var schedule = input.TryGetValue("schedule_id", out var sid) && sid.ValueKind == JsonValueKind.Number
            ? doc.GetElement(new ElementId(sid.GetInt64())) as ViewSchedule ?? throw NameResolve.MissingId(sid.GetInt64(), "Schedule")
            : schedules.FirstOrDefault(s => s.Name == NativeToolUtil.Text(input, "schedule")) ?? throw NameResolve.Missing(NativeToolUtil.Text(input, "schedule"), "Schedule", schedules.Select(s => s.Name));
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
        var sheet = input.TryGetValue("sheet_id", out var shid) && shid.ValueKind == JsonValueKind.Number
            ? doc.GetElement(new ElementId(shid.GetInt64())) as ViewSheet ?? throw NameResolve.MissingId(shid.GetInt64(), "Sheet")
            : sheets.FirstOrDefault(s => s.SheetNumber == NativeToolUtil.Text(input, "sheet_number")) ?? throw NameResolve.Missing(NativeToolUtil.Text(input, "sheet_number"), "Sheet number", sheets.Select(s => s.SheetNumber));
        var tb = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstOrDefault()
            ?? throw new ToolInputException($"Sheet {sheet.SheetNumber} has no title block to fit into.");
        var position = NativeToolUtil.Text(input, "position", "above_stamp");
        if (position is not ("above_stamp" or "top_right" or "top_left")) throw new ToolInputException("position must be above_stamp, top_right or top_left.");
        double Mm(string key, double d) => (input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d) / Units.MmPerFoot;
        var margin = Mm("margin_mm", 5); var stamp = Mm("stamp_height_mm", 55);
        var split = !input.TryGetValue("split", out var sp) || sp.ValueKind != JsonValueKind.False;
        var preview = NativeToolUtil.Preview(input);
        var ((placed, segments, overflow), warnings) = NativeToolUtil.Commit(doc, "Claude: спецификация на лист", preview, () =>
        {
            doc.Regenerate();
            var box = tb.get_BoundingBox(sheet) ?? throw new InvalidOperationException("Title block has no extents.");
            double left = box.Min.X + 20 / Units.MmPerFoot + margin, right = box.Max.X - 5 / Units.MmPerFoot - margin;
            double top = box.Max.Y - 5 / Units.MmPerFoot - margin, bottom = box.Min.Y + 5 / Units.MmPerFoot + margin + (position == "above_stamp" ? stamp : 0);
            var available = top - bottom;
            ScheduleSheetInstance Place(int? segment)
            {
                var inst = segment is { } s ? ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, new XYZ(left, top, 0), s) : ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, new XYZ(left, top, 0));
                doc.Regenerate();
                return inst;
            }
            var first = Place(null);
            var fb = first.get_BoundingBox(sheet);
            var height = fb.Max.Y - fb.Min.Y;
            var count = 1;
            var instances = new List<ScheduleSheetInstance> { first };
            if (height > available && split && !schedule.IsSplit())
            {
                count = (int)Math.Ceiling(height / (available * 0.9));
                doc.Delete(first.Id);
                schedule.Split(count);
                instances = Enumerable.Range(0, schedule.GetSegmentCount()).Select(i => Place(i)).ToList();
            }
            var cursor = position == "top_left" ? left : right;
            var over = new List<int>();
            for (int i = 0; i < instances.Count; i++)
            {
                var b = instances[i].get_BoundingBox(sheet);
                double w = b.Max.X - b.Min.X, h = b.Max.Y - b.Min.Y;
                if (h > available + 1e-6) over.Add(i);
                var targetLeft = position == "top_left" ? cursor : cursor - w;
                var targetTop = position == "above_stamp" ? bottom + h : top;
                ElementTransformUtils.MoveElement(doc, instances[i].Id, new XYZ(targetLeft - b.Min.X, targetTop - b.Max.Y, 0));
                cursor = position == "top_left" ? cursor + w + margin : cursor - w - margin;
            }
            if (position == "top_left" ? cursor - margin > right : cursor + margin < left) over.Add(-1);
            return (instances.Select(x => x.Id.Value).ToList(), instances.Count, over);
        });
        return Services.Json.Serialize(new
        {
            preview, schedule = schedule.Name, sheet = sheet.SheetNumber, instances = placed, segments,
            does_not_fit = overflow.Count == 0 ? null : overflow.Contains(-1) ? "segments are wider than the frame; use a larger sheet or fewer columns" : $"segments {string.Join(", ", overflow)} are taller than the free height",
            revit_warnings = warnings
        });
    }
}

public sealed class NumberRooms : IRevitTool
{
    public string Name => "number_rooms";
    public string Description =>
        "Renumber rooms per level for Russian documentation. Order: reading (rows top-down, left-right), snake, or path " +
        "(along path_mm, a corridor polyline). Format tokens: {L} level ordinal (first level at or above ground = 1), " +
        "{N} sequence, {A} apartment (apartment_parameter; with per_apartment=true the sequence restarts in each " +
        "apartment); ':00' pads — e.g. '{L}{N:00}' → 101, '{A}.{N}' → 12.3. Numbers already used by rooms outside the " +
        "set are reported. preview defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["level"] = NativeToolUtil.Field("string", "Only this level (default every level with rooms)."),
        ["element_ids"] = NativeToolUtil.Array("integer", "Only these rooms."),
        ["order"] = NativeToolUtil.Field("string", "reading (default) | snake | path."),
        ["path_mm"] = NativeToolUtil.Any("For order=path: [[x,y], ...] polyline in mm."),
        ["row_tolerance_mm"] = NativeToolUtil.Field("number", "Row banding for reading/snake (default 1500)."),
        ["format"] = NativeToolUtil.Field("string", "Number format, default '{L}{N:00}'."),
        ["start"] = NativeToolUtil.Field("integer", "First sequence number (default 1)."),
        ["apartment_parameter"] = NativeToolUtil.Field("string", "Room parameter with the apartment number for {A}."),
        ["per_apartment"] = NativeToolUtil.Field("boolean", "Restart the sequence in each apartment."),
        ["ground_elevation_mm"] = NativeToolUtil.Field("number", "Ground elevation for {L} (default 0)."),
        ["skip_names"] = NativeToolUtil.Array("string", "Leave rooms whose name contains any of these unnumbered."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var format = NativeToolUtil.Text(input, "format", "{L}{N:00}");
        RoomNumbering.Format(format, 1, 1, "1");
        var rooms = DraftingTable.Rooms(doc, input);
        if (input.TryGetValue("element_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            var set = NativeToolUtil.Ids(ids).ToHashSet();
            rooms = rooms.Where(r => set.Contains(r.Id)).ToList();
        }
        var skip = input.TryGetValue("skip_names", out var sk) && sk.ValueKind == JsonValueKind.Array ? sk.EnumerateArray().Select(s => s.GetString() ?? "").Where(s => s.Length > 0).ToList() : [];
        rooms = rooms.Where(r => !skip.Any(s => DraftingTable.RoomName(r).Contains(s, StringComparison.OrdinalIgnoreCase))).ToList();
        if (rooms.Count == 0) throw new ToolInputException("No placed rooms to number.");
        var order = NativeToolUtil.Text(input, "order", "reading");
        if (order is not ("reading" or "snake" or "path")) throw new ToolInputException("order must be reading, snake or path.");
        var path = order == "path"
            ? ToolInput.RequiredArray(input, "path_mm").EnumerateArray().Select(p => { var a = p.EnumerateArray().Select(v => v.GetDouble()).ToArray(); return (a[0] / Units.MmPerFoot, a[1] / Units.MmPerFoot); }).ToList()
            : null;
        var tol = (input.TryGetValue("row_tolerance_mm", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetDouble() : 1500) / Units.MmPerFoot;
        var start = input.TryGetValue("start", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 1;
        var apt = NativeToolUtil.Text(input, "apartment_parameter");
        var perApt = ToolInput.Flag(input, "per_apartment");
        if ((format.Contains("{A}") || perApt) && apt.Length == 0) throw new ToolInputException("{A} and per_apartment need apartment_parameter.");
        string? Apt(Room r) => apt.Length == 0 ? null : r.LookupParameter(apt) is { } p ? (p.StorageType == StorageType.String ? p.AsString() : p.AsValueString()) : null;
        var ground = (input.TryGetValue("ground_elevation_mm", out var g) && g.ValueKind == JsonValueKind.Number ? g.GetDouble() : 0) / Units.MmPerFoot;
        var levelsWithRooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>()
            .Where(r => r.Level != null).Select(r => r.Level).GroupBy(l => l.Id).Select(gr => gr.First()).OrderBy(l => l.Elevation).ToList();
        var firstAbove = levelsWithRooms.FindIndex(l => l.Elevation >= ground - 1e-6);
        int Ordinal(Level l) { var i = levelsWithRooms.FindIndex(x => x.Id == l.Id); return firstAbove < 0 ? i + 1 : i - firstAbove + 1; }

        var plan = new List<(Room Room, string Old, string New)>();
        foreach (var byLevel in rooms.GroupBy(r => r.LevelId))
        {
            var list = byLevel.ToList();
            var pts = list.Select(r => { var p = ((LocationPoint)r.Location).Point; return (p.X, p.Y); }).ToList();
            var idx = order == "path" ? RoomNumbering.PathOrder(pts, path!) : RoomNumbering.ReadingOrder(pts, tol, order == "snake");
            var seq = new Dictionary<string, int>();
            foreach (var i in idx)
            {
                var r = list[i]; var a = Apt(r);
                var key = perApt ? a ?? "" : "";
                var n = seq.TryGetValue(key, out var cur) ? cur + 1 : start; seq[key] = n;
                plan.Add((r, r.Number, RoomNumbering.Format(format, Ordinal(r.Level), n, a)));
            }
        }
        var dupes = plan.GroupBy(p => p.New).Where(gr => gr.Count() > 1).Select(gr => gr.Key).ToList();
        if (dupes.Count > 0) throw new ToolInputException($"The format gives duplicate numbers ({string.Join(", ", dupes.Take(10))}); add {{L}} or {{A}}, or number per level.");
        var planned = plan.Select(p => p.Room.Id).ToHashSet();
        var taken = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>()
            .Where(r => !planned.Contains(r.Id)).Select(r => r.Number).ToHashSet();
        var conflicts = plan.Where(p => taken.Contains(p.New)).Select(p => p.New).ToList();
        var preview = NativeToolUtil.Preview(input);
        var (changed, warnings) = NativeToolUtil.Commit(doc, "Claude: нумерация помещений", preview, () =>
        {
            // Two passes: swapping 101 and 102 directly would collide on the way.
            foreach (var p in plan) p.Room.Number = "~" + p.Room.Id.Value;
            foreach (var p in plan) p.Room.Number = p.New;
            return plan.Count(p => p.Old != p.New);
        });
        return Services.Json.Serialize(new
        {
            preview, rooms = plan.Count, changed,
            numbers = plan.Take(300).Select(p => new { id = p.Room.Id.Value, name = DraftingTable.RoomName(p.Room), old = p.Old, @new = p.New }),
            conflicts_with_other_rooms = conflicts, revit_warnings = warnings
        });
    }
}
