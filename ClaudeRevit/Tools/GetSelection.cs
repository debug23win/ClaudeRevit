using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class GetSelection : IRevitTool
{
    public string Name => "get_selection";

    public string Description =>
        "Returns the elements currently selected in the active Revit view. Each entry has " +
        "id, name, category, type_name, and (when applicable) host level. Call this whenever the " +
        "user refers to 'this', 'these', 'the selected', or wants you to operate on what they have picked. " +
        "Listing is capped (default 200); the full count and a per-category breakdown are always returned, " +
        "so you can act on the whole selection without listing it.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["limit"] = JsonSerializer.SerializeToElement(new
            {
                type = "integer",
                description = "Max elements listed (default 200, max 1000). The count is never capped."
            })
        },
        Required = []
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var uidoc = app.ActiveUIDocument
            ?? throw new InvalidOperationException("No document is open.");
        var doc = uidoc.Document;

        // A user can select tens of thousands of elements. Listing them all would blow the context
        // window in THIS turn — tool-result aging only trims things for the next prompt — so the
        // listing is capped while the count and category breakdown stay exact.
        var limit = input.TryGetValue("limit", out var l) && l.ValueKind == JsonValueKind.Number
            ? Math.Clamp(l.GetInt32(), 1, 1000)
            : 200;

        var ids = uidoc.Selection.GetElementIds();
        var byCategory = new Dictionary<string, int>();
        foreach (var id in ids)
        {
            var c = doc.GetElement(id)?.Category?.Name ?? "(no category)";
            byCategory[c] = byCategory.GetValueOrDefault(c) + 1;
        }

        var elements = ids.Take(limit).Select(id => doc.GetElement(id))
            .Where(e => e != null)
            .Select(e => new
            {
                id = e!.Id.Value,
                name = e.Name,
                category = e.Category?.Name,
                type_name = doc.GetElement(e.GetTypeId())?.Name,
                level = e.LevelId != ElementId.InvalidElementId
                    ? doc.GetElement(e.LevelId)?.Name
                    : null
            })
            .ToList();

        return Services.Json.Serialize(new
        {
            count = ids.Count,
            listed = elements.Count,
            truncated = ids.Count > elements.Count,
            by_category = byCategory,
            elements
        });
    }
}
