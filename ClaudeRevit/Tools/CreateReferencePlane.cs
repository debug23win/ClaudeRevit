using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class CreateReferencePlane : IRevitTool
{
    public string Name => "create_reference_plane";

    public string Description =>
        "Creates a vertical reference plane in the active view from two plan-coordinate points (in feet). " +
        "Optionally name it. Reference planes are useful as construction guides — wall snap targets, " +
        "alignment helpers, etc.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["start_x"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Start X (feet)." }),
            ["start_y"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Start Y (feet)." }),
            ["end_x"] = JsonSerializer.SerializeToElement(new { type = "number", description = "End X (feet)." }),
            ["end_y"] = JsonSerializer.SerializeToElement(new { type = "number", description = "End Y (feet)." }),
            ["name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional reference-plane name (must be unique)." })
        },
        Required = ["start_x", "start_y", "end_x", "end_y"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");
        var view = doc.ActiveView ?? throw new InvalidOperationException("No active view.");

        var start = new XYZ(input["start_x"].GetDouble(), input["start_y"].GetDouble(), 0);
        var end = new XYZ(input["end_x"].GetDouble(), input["end_y"].GetDouble(), 0);
        if (start.IsAlmostEqualTo(end))
            throw new InvalidOperationException("Reference plane has zero length.");

        // NewReferencePlane2's third argument is a POINT the plane must pass through, not a
        // direction. For a vertical plane it has to sit directly above the line — using a fixed
        // (0,0,1) forced the plane through the world origin, tilting it for any line away from
        // the origin. A point straight above 'start' keeps the plane truly vertical.
        var refPlane = doc.Create.NewReferencePlane2(start, end, new XYZ(start.X, start.Y, 1), view);

        if (input.TryGetValue("name", out var n) && n.ValueKind == JsonValueKind.String)
        {
            try { refPlane.Name = n.GetString(); }
            catch { /* name conflict — keep auto */ }
        }

        return JsonSerializer.Serialize(new
        {
            id = refPlane.Id.Value,
            type = "ReferencePlane",
            view = view.Name,
            name = refPlane.Name,
            length_ft = (end - start).GetLength()
        });
    }
}
