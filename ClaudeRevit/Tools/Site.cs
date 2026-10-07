using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Site context. Ideas from so0osh's fork of mcp-servers-for-revit (create_toposolid,
// get_project_location; MIT); native implementations.
public sealed class GetProjectLocation : IRevitTool
{
    public string Name => "get_project_location";
    public string Description =>
        "Where the project sits in the world: site latitude/longitude/elevation and place name, the active shared " +
        "location's offset and angle to true north, and the project base point / survey point positions (mm, degrees). " +
        "Read-only. Use it before setting-out, toposolids or anything referenced to shared coordinates.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new());
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var site = doc.SiteLocation;
        var location = doc.ActiveProjectLocation;
        var pos = location.GetProjectPosition(XYZ.Zero);
        object Point(BasePoint? p) => p == null ? null! : new
        {
            id = p.Id.Value,
            position_mm = NativeToolUtil.Mm(p.Position),
            shared_position_mm = NativeToolUtil.Mm(p.SharedPosition),
            clipped = p.Clipped
        };
        return Services.Json.Serialize(new
        {
            site = new
            {
                place = site.PlaceName,
                latitude_deg = site.Latitude * 180 / Math.PI,
                longitude_deg = site.Longitude * 180 / Math.PI,
                elevation_mm = site.Elevation * Units.MmPerFoot,
                time_zone_hours = site.TimeZone
            },
            active_location = new
            {
                name = location.Name,
                east_west_mm = pos.EastWest * Units.MmPerFoot,
                north_south_mm = pos.NorthSouth * Units.MmPerFoot,
                elevation_mm = pos.Elevation * Units.MmPerFoot,
                angle_to_true_north_deg = pos.Angle * 180 / Math.PI
            },
            project_base_point = Point(BasePoint.GetProjectBasePoint(doc)),
            survey_point = Point(BasePoint.GetSurveyPoint(doc)),
            locations = new FilteredElementCollector(doc).OfClass(typeof(ProjectLocation)).Select(l => l.Name).ToArray()
        });
    }
}

public sealed class CreateToposolid : IRevitTool
{
    public string Name => "create_toposolid";
    public string Description =>
        "Create a Toposolid (the 2024+ terrain element) from survey points [x, y, z] in mm, in project coordinates, " +
        "at a level. Use instead of the deprecated topography surface. preview defaults true (created then rolled back); " +
        "preview=false keeps it.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["points_mm"] = NativeToolUtil.Any("Array of [x, y, z] points in mm (at least 3, not collinear)."),
        ["level"] = NativeToolUtil.Field("string", "Level the toposolid is hosted on."),
        ["toposolid_type"] = NativeToolUtil.Field("string", "Type name (default: the project's default toposolid type)."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "points_mm", "level");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var preview = NativeToolUtil.Preview(input);
        var level = Circulation.Level(doc, input, "level");
        var points = ToolInput.RequiredArray(input, "points_mm").EnumerateArray().Select(p =>
        {
            var a = p.EnumerateArray().Select(v => v.GetDouble()).ToArray();
            if (a.Length != 3 || a.Any(v => !double.IsFinite(v))) throw new ToolInputException("Each point must be [x_mm, y_mm, z_mm].");
            return new XYZ(a[0] / Units.MmPerFoot, a[1] / Units.MmPerFoot, a[2] / Units.MmPerFoot);
        }).ToList();
        if (points.Count < 3) throw new ToolInputException("A toposolid needs at least 3 points.");
        var types = new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Cast<ToposolidType>().ToList();
        var typeName = NativeToolUtil.Text(input, "toposolid_type");
        var type = typeName.Length > 0
            ? types.FirstOrDefault(t => t.Name == typeName) ?? throw NameResolve.Missing(typeName, "Toposolid type", types.Select(t => t.Name))
            : types.FirstOrDefault() ?? throw new ToolInputException("The project has no toposolid types.");
        var (id, warnings) = NativeToolUtil.Commit(doc, "Claude: toposolid", preview,
            () => Toposolid.Create(doc, points, type.Id, level.Id).Id.Value);
        return Services.Json.Serialize(new { preview, toposolid_id = id, points = points.Count, toposolid_type = type.Name, revit_warnings = warnings });
    }
}
