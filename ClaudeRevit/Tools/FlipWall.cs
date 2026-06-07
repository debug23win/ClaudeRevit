using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class FlipWall : IRevitTool
{
    public string Name => "flip_wall";

    public string Description =>
        "Flips the orientation of one or more walls (swaps interior/exterior side). Same as Revit's Flip arrow.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["wall_ids"] = JsonSerializer.SerializeToElement(new
            {
                type = "array", minItems = 1,
                description = "Walls to flip.",
                items = new { type = "integer" }
            })
        },
        Required = ["wall_ids"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var ids = input["wall_ids"].EnumerateArray()
            .Select(e => new ElementId(e.GetInt64())).ToList();

        var flipped = new List<long>();
        var skipped = new List<object>();

        foreach (var id in ids)
        {
            var wall = doc.GetElement(id) as Wall;
            if (wall == null) { skipped.Add(new { id = id.Value, reason = "not a Wall" }); continue; }
            try { wall.Flip(); flipped.Add(id.Value); }
            catch (Exception ex) { skipped.Add(new { id = id.Value, reason = ex.Message }); }
        }

        return JsonSerializer.Serialize(new
        {
            flipped_count = flipped.Count,
            skipped_count = skipped.Count,
            skipped
        });
    }
}
