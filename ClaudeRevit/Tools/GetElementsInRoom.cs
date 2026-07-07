using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class GetElementsInRoom : IRevitTool
{
    public string Name => "get_elements_in_room";

    public string Description =>
        "Returns the ids of elements whose location point lies inside a given room. " +
        "Optionally filter to a single category. Useful for 'what furniture is in the kitchen?' / " +
        "'tag all doors in the lobby'.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["room_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Room element id." }),
            ["category"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional category to filter, e.g. 'Furniture', 'Doors'." }),
            ["limit"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Max results (default 200, max 1000).", minimum = 1, maximum = 1000 })
        },
        Required = ["room_id"]
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var roomId = new ElementId(input["room_id"].GetInt64());
        var room = doc.GetElement(roomId) as Room
            ?? throw new InvalidOperationException($"Element {roomId.Value} is not a Room.");

        var limit = input.TryGetValue("limit", out var l) ? l.GetInt32() : 200;
        if (limit < 1 || limit > 1000) limit = 200;

        FilteredElementCollector collector;
        if (input.TryGetValue("category", out var c) && c.ValueKind == JsonValueKind.String)
        {
            var bic = CategoryResolve.Parse(c.GetString());
            collector = new FilteredElementCollector(doc).OfCategory(bic);
        }
        else
        {
            collector = new FilteredElementCollector(doc);
        }

        var inRoom = collector
            .WhereElementIsNotElementType()
            .Where(el =>
            {
                if (el.Location is LocationPoint lp)
                    return room.IsPointInRoom(lp.Point);
                return false;
            })
            .Take(limit + 1)
            .ToList();

        var truncated = inRoom.Count > limit;

        return JsonSerializer.Serialize(new
        {
            room = room.Name + " (" + (room.Number ?? "") + ")",
            count = Math.Min(inRoom.Count, limit),
            truncated,
            elements = inRoom.Take(limit).Select(e => new
            {
                id = e.Id.Value,
                category = e.Category?.Name,
                name = e.Name
            })
        });
    }
}
