using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class ResetViewOverrides : IRevitTool
{
    public string Name => "reset_view_overrides";

    public string Description =>
        "Clears element graphic overrides (color, line, pattern) in a view for the listed elements, " +
        "restoring their default appearance. Defaults to active view if view_id is omitted.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["element_ids"] = JsonSerializer.SerializeToElement(new
            {
                type = "array", minItems = 1,
                description = "Elements whose overrides should be cleared.",
                items = new { type = "integer" }
            }),
            ["view_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Optional view id (default active view)." })
        },
        Required = ["element_ids"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        View view;
        if (input.TryGetValue("view_id", out var vid) && vid.ValueKind == JsonValueKind.Number)
            view = doc.GetElement(new ElementId(vid.GetInt64())) as View
                ?? throw new InvalidOperationException("view_id is not a view.");
        else
            view = doc.ActiveView ?? throw new InvalidOperationException("No active view.");

        var ids = input["element_ids"].EnumerateArray()
            .Select(e => new ElementId(e.GetInt64())).ToList();

        var blank = new OverrideGraphicSettings();
        foreach (var id in ids)
            view.SetElementOverrides(id, blank);

        return JsonSerializer.Serialize(new
        {
            view = view.Name,
            cleared_count = ids.Count
        });
    }
}
