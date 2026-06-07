using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class CreateWallFoundation : IRevitTool
{
    public string Name => "create_wall_foundation";

    public string Description =>
        "Adds a wall foundation (strip footing) hosted to an existing structural wall. " +
        "If type_name is omitted, the first available wall foundation type is used.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["wall_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Host wall id." }),
            ["type_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional wall foundation type name." })
        },
        Required = ["wall_id"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var wallId = new ElementId(input["wall_id"].GetInt64());
        var wall = doc.GetElement(wallId) as Wall
            ?? throw new InvalidOperationException($"Element {wallId.Value} is not a Wall.");

        WallFoundationType footingType;
        if (input.TryGetValue("type_name", out var tn) && tn.ValueKind == JsonValueKind.String)
        {
            var name = tn.GetString();
            footingType = new FilteredElementCollector(doc).OfClass(typeof(WallFoundationType)).Cast<WallFoundationType>()
                .FirstOrDefault(t => t.Name == name)
                ?? throw new InvalidOperationException($"Wall foundation type '{name}' not found.");
        }
        else
        {
            footingType = new FilteredElementCollector(doc).OfClass(typeof(WallFoundationType)).Cast<WallFoundationType>()
                .FirstOrDefault()
                ?? throw new InvalidOperationException("No wall foundation types loaded.");
        }

        var foundation = WallFoundation.Create(doc, footingType.Id, wallId);

        return JsonSerializer.Serialize(new
        {
            id = foundation.Id.Value,
            type = "WallFoundation",
            type_name = footingType.Name,
            host_wall = wall.Name
        });
    }
}
