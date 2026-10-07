using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class CreateFloor : IRevitTool
{
    public string Name => "create_floor";

    public string Description =>
        "Creates a floor (slab) from a closed boundary of plan-coordinate points (in feet) on a named level. " +
        "Provide the points in order (CW or CCW); the boundary closes automatically from the last point back to the first. " +
        "Minimum 3 points. All spatial values are in feet — convert from meters/mm before calling. " +
        "Use get_levels first if the user did not specify a level.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["points"] = JsonSerializer.SerializeToElement(new
            {
                type = "array",
                minItems = 3,
                description = "Boundary points in order. Each point is { \"x\": <feet>, \"y\": <feet> }.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        x = new { type = "number" },
                        y = new { type = "number" }
                    },
                    required = new[] { "x", "y" }
                }
            }),
            ["level_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Name of the host level (must match exactly)." }),
            ["floor_type_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional floor type name. Defaults to first available floor type." }),
            ["structural"] = JsonSerializer.SerializeToElement(new { type = "boolean", description = "Native structural slab flag, default false. Required for reinforcement hosts." })
        },
        Required = ["points", "level_name"]
    };

    public bool RequiresTransaction => true;

    public void Preflight(IReadOnlyDictionary<string,JsonElement> input,UIApplication app) =>
        Services.GeometryPreflight.Contour(ToolInput.RequiredArray(input, "points").EnumerateArray().Select(p=>new Services.PlanPoint(p.GetProperty("x").GetDouble()*Units.MmPerFoot,p.GetProperty("y").GetDouble()*Units.MmPerFoot)),NativeToolUtil.Doc(app).Application.ShortCurveTolerance*Units.MmPerFoot);

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = ToolContext.UiDocument(app)?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var levelName = ToolInput.RequiredText(input, "level_name")!;
        var pointsArray = ToolInput.Required(input, "points");
        if (pointsArray.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("'points' must be an array.");

        var pts = pointsArray.EnumerateArray()
            .Select(p => new XYZ(
                p.GetProperty("x").GetDouble(),
                p.GetProperty("y").GetDouble(),
                0))
            .ToList();

        if(pts.Count>3 && pts[0].DistanceTo(pts[^1])<1e-9)pts.RemoveAt(pts.Count-1);

        if (pts.Count < 3)
            throw new InvalidOperationException($"Floor requires at least 3 points (got {pts.Count}).");

        var level = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .FirstOrDefault(l => l.Name == levelName)
            ?? throw NameResolve.MissingLevel(doc, levelName, "Level");

        FloorType floorType;
        if (input.TryGetValue("floor_type_name", out var ft) && ft.ValueKind == JsonValueKind.String)
        {
            var ftName = ft.GetString();
            floorType = NameResolve.ByName<FloorType>(doc, ftName, "Floor type");
        }
        else
        {
            floorType = new FilteredElementCollector(doc)
                .OfClass(typeof(FloorType))
                .Cast<FloorType>()
                .First();
        }

        var loop = new CurveLoop();
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            if (a.IsAlmostEqualTo(b))
                throw new InvalidOperationException(
                    $"Boundary points {i} and {(i + 1) % pts.Count} are identical.");
            loop.Append(Line.CreateBound(a, b));
        }

        var floor = Floor.Create(doc, new List<CurveLoop> { loop }, floorType.Id, level.Id,ToolInput.Flag(input,"structural"),null,0);

        return Services.Json.Serialize(new
        {
            id = floor.Id.Value,
            type = "Floor",
            type_name = floorType.Name,
            level = level.Name,
            boundary_points = pts.Count
        });
    }
}
