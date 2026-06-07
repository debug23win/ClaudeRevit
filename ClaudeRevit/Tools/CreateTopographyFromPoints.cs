using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class CreateTopographyFromPoints : IRevitTool
{
    public string Name => "create_topography_from_points";

    public string Description =>
        "Creates a TopographySurface from a list of 3D points (x, y, z in feet). Minimum 3 non-collinear points. " +
        "Useful for site modeling — survey data, terrain.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["points"] = JsonSerializer.SerializeToElement(new
            {
                type = "array", minItems = 3,
                description = "3D points {x, y, z} in feet.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        x = new { type = "number" },
                        y = new { type = "number" },
                        z = new { type = "number" }
                    },
                    required = new[] { "x", "y", "z" }
                }
            })
        },
        Required = ["points"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var pts = input["points"].EnumerateArray()
            .Select(p => new XYZ(
                p.GetProperty("x").GetDouble(),
                p.GetProperty("y").GetDouble(),
                p.GetProperty("z").GetDouble()))
            .ToList();

        if (pts.Count < 3)
            throw new InvalidOperationException($"Need at least 3 points (got {pts.Count}).");

        var topo = TopographySurface.Create(doc, pts);

        return JsonSerializer.Serialize(new
        {
            id = topo.Id.Value,
            type = "TopographySurface",
            point_count = pts.Count
        });
    }
}
