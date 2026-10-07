using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Stairs, railings and floor openings: the circulation elements no tool could create. Inputs in
// millimetres, like the other native tools.
internal static class Circulation
{
    public static Level Level(Document doc, IReadOnlyDictionary<string, JsonElement> input, string key)
    {
        var name = ToolInput.RequiredText(input, key);
        return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == name)
            ?? throw NameResolve.MissingLevel(doc, name);
    }

    public static List<XYZ> Points(IReadOnlyDictionary<string, JsonElement> input, string key, double z, int min)
    {
        var pts = ToolInput.RequiredArray(input, key).EnumerateArray().Select(p =>
        {
            var a = p.EnumerateArray().Select(v => v.GetDouble()).ToArray();
            if (a.Length < 2 || a.Any(v => !double.IsFinite(v))) throw new ToolInputException($"Each point in '{key}' must be [x_mm, y_mm].");
            return new XYZ(a[0] / Units.MmPerFoot, a[1] / Units.MmPerFoot, z);
        }).ToList();
        if (pts.Count < min) throw new ToolInputException($"'{key}' needs at least {min} points.");
        return pts;
    }

    public static CurveLoop Loop(List<XYZ> pts, bool close)
    {
        var loop = new CurveLoop();
        var n = close ? pts.Count : pts.Count - 1;
        for (var i = 0; i < n; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            if (a.DistanceTo(b) < 1e-4) throw new ToolInputException($"Points {i} and {(i + 1) % pts.Count} coincide.");
            loop.Append(Line.CreateBound(a, b));
        }
        return loop;
    }
}

public sealed class CreateStair : IRevitTool
{
    public string Name => "create_stair";
    public string Description =>
        "Create a straight-run stair between two levels. The riser count follows from the level-to-level height and the " +
        "stair type's maximum riser; the run is laid out from start_mm in the given direction. Returns the stair id, " +
        "riser count, actual riser height and run length. preview=true (default) only computes the layout; preview=false creates it.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["base_level"] = NativeToolUtil.Field("string", "Base level name."),
        ["top_level"] = NativeToolUtil.Field("string", "Top level name."),
        ["start_mm"] = NativeToolUtil.Array("number", "[x, y] of the run start (first riser), mm."),
        ["direction_deg"] = NativeToolUtil.Field("number", "Run direction in plan, degrees from +X (default 0)."),
        ["width_mm"] = NativeToolUtil.Field("number", "Run width (default 1200)."),
        ["stair_type"] = NativeToolUtil.Field("string", "Stair type name (default: the project's default stair type)."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "base_level", "top_level", "start_mm");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var preview = NativeToolUtil.Preview(input);
        var baseLevel = Circulation.Level(doc, input, "base_level");
        var topLevel = Circulation.Level(doc, input, "top_level");
        var height = topLevel.Elevation - baseLevel.Elevation;
        if (height <= 0) throw new ToolInputException("top_level must be above base_level.");
        var types = new FilteredElementCollector(doc).OfClass(typeof(StairsType)).Cast<StairsType>().ToList();
        var typeName = NativeToolUtil.Text(input, "stair_type");
        var type = typeName.Length > 0
            ? types.FirstOrDefault(t => t.Name == typeName) ?? throw NameResolve.Missing(typeName, "Stair type", types.Select(t => t.Name))
            : doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.StairsType)) as StairsType ?? types.FirstOrDefault()
              ?? throw new ToolInputException("The project has no stair types; load or create one first.");
        var risers = (int)Math.Ceiling(height / type.MaxRiserHeight - 1e-9);
        var riser = height / risers;
        var tread = type.MinTreadDepth;
        var length = (risers - 1) * tread;
        var start = ToolInput.RequiredArray(input, "start_mm").EnumerateArray().Select(v => v.GetDouble() / Units.MmPerFoot).ToArray();
        if (start.Length < 2) throw new ToolInputException("start_mm must be [x, y].");
        var a = (ToolInput.OptionalDouble(input, "direction_deg") ?? 0) * Math.PI / 180;
        var width = (ToolInput.OptionalDouble(input, "width_mm") ?? 1200) / Units.MmPerFoot;
        var p0 = new XYZ(start[0], start[1], baseLevel.Elevation);
        var p1 = p0 + new XYZ(Math.Cos(a), Math.Sin(a), 0) * length;
        var layout = new
        {
            risers, riser_height_mm = Math.Round(riser * Units.MmPerFoot, 1), tread_depth_mm = Math.Round(tread * Units.MmPerFoot, 1),
            run_length_mm = Math.Round(length * Units.MmPerFoot, 1), width_mm = Math.Round(width * Units.MmPerFoot, 1), stair_type = type.Name,
            start_mm = NativeToolUtil.Mm(p0), end_mm = NativeToolUtil.Mm(p1)
        };
        if (preview) return Services.Json.Serialize(new { preview, layout, note = "Nothing was created. Send preview=false to build it." });

        // A stair is edited through StairsEditScope, which Revit only allows outside any transaction.
        ElementId stairsId;
        using (var scope = new StairsEditScope(doc, "Claude: create stair"))
        {
            stairsId = scope.Start(baseLevel.Id, topLevel.Id);
            using (var tx = new Transaction(doc, "Claude: stair run"))
            {
                tx.Start();
                var stairs = (Stairs)doc.GetElement(stairsId);
                stairs.ChangeTypeId(type.Id);
                stairs.DesiredRisersNumber = risers;
                var run = StairsRun.CreateStraightRun(doc, stairsId, Line.CreateBound(p0, p1), StairsRunJustification.Center);
                run.ActualRunWidth = width;
                tx.Commit();
            }
            scope.Commit(new NativeFailures());
        }
        var created = (Stairs)doc.GetElement(stairsId);
        return Services.Json.Serialize(new { preview, stairs_id = stairsId.Value, layout, actual_risers = created.ActualRisersNumber });
    }
}

public sealed class CreateRailing : IRevitTool
{
    public string Name => "create_railing";
    public string Description =>
        "Create a railing either on a stair (stairs_id; placed on treads or stringers) or along a path of plan points at " +
        "a level. preview defaults true (created and rolled back, ids reported); preview=false keeps it.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["stairs_id"] = NativeToolUtil.Field("integer", "Host stair. Use this OR path_mm + level."),
        ["placement"] = NativeToolUtil.Field("string", "For stairs: treads (default) or stringers."),
        ["path_mm"] = NativeToolUtil.Any("Array of [x, y] points (mm) for a free railing."),
        ["level"] = NativeToolUtil.Field("string", "Level for a free railing."),
        ["railing_type"] = NativeToolUtil.Field("string", "Railing type name (default: project default)."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var preview = NativeToolUtil.Preview(input);
        var types = new FilteredElementCollector(doc).OfClass(typeof(RailingType)).Cast<RailingType>().ToList();
        var typeName = NativeToolUtil.Text(input, "railing_type");
        var type = typeName.Length > 0
            ? types.FirstOrDefault(t => t.Name == typeName) ?? throw NameResolve.Missing(typeName, "Railing type", types.Select(t => t.Name))
            : doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.StairsRailingType)) as RailingType ?? types.FirstOrDefault()
              ?? throw new ToolInputException("The project has no railing types.");
        var (ids, warnings) = NativeToolUtil.Commit(doc, "Claude: create railing", preview, () =>
        {
            if (input.TryGetValue("stairs_id", out var s) && s.ValueKind == JsonValueKind.Number)
            {
                var stairs = NativeToolUtil.Element(doc, s.GetInt64()) as Stairs ?? throw new ToolInputException("stairs_id is not a stair.");
                var where = NativeToolUtil.Text(input, "placement", "treads") == "stringers" ? RailingPlacementPosition.Stringer : RailingPlacementPosition.Treads;
                return Railing.Create(doc, stairs.Id, type.Id, where).Select(id => id.Value).ToList();
            }
            var level = Circulation.Level(doc, input, "level");
            var loop = Circulation.Loop(Circulation.Points(input, "path_mm", level.Elevation, 2), close: false);
            return new List<long> { Railing.Create(doc, loop, type.Id, level.Id).Id.Value };
        });
        return Services.Json.Serialize(new { preview, railing_ids = ids, railing_type = type.Name, revit_warnings = warnings });
    }
}

public sealed class CreateFloorOpening : IRevitTool
{
    public string Name => "create_floor_opening";
    public string Description =>
        "Cut an opening through one floor (or roof/ceiling) by a closed plan boundary. For an opening through several " +
        "storeys use create_shaft_opening. preview defaults true; preview=false keeps it.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["host_id"] = NativeToolUtil.Field("integer", "Floor, roof or ceiling to cut."),
        ["boundary_mm"] = NativeToolUtil.Any("Closed boundary: array of [x, y] points in mm (3 or more)."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "host_id", "boundary_mm");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var preview = NativeToolUtil.Preview(input);
        var host = NativeToolUtil.Element(doc, ToolInput.RequiredLong(input, "host_id"));
        if (host is not (Floor or RoofBase or Ceiling)) throw new ToolInputException("host_id must be a floor, roof or ceiling.");
        var pts = Circulation.Points(input, "boundary_mm", 0, 3);
        var curves = new CurveArray();
        foreach (var c in Circulation.Loop(pts, close: true)) curves.Append(c);
        var (id, warnings) = NativeToolUtil.Commit(doc, "Claude: floor opening", preview,
            () => doc.Create.NewOpening(host, curves, true).Id.Value);
        return Services.Json.Serialize(new { preview, opening_id = id, host_id = host.Id.Value, revit_warnings = warnings });
    }
}
