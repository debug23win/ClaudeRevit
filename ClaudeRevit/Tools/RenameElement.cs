using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class RenameElement : IRevitTool
{
    public string Name => "rename_element";

    public string Description =>
        "Renames an element (anything with a Name property — views, schedules, sheets, families, types, " +
        "levels, grids, etc.). The new name must be unique within the document for that element kind.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["element_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Element id." }),
            ["new_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "New name." })
        },
        Required = ["element_id", "new_name"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var id = new ElementId(input["element_id"].GetInt64());
        var el = doc.GetElement(id)
            ?? throw new InvalidOperationException($"Element {id.Value} not found.");

        var newName = input["new_name"].GetString()!;
        var oldName = el.Name;
        el.Name = newName;

        return JsonSerializer.Serialize(new
        {
            id = id.Value,
            category = el.Category?.Name,
            old_name = oldName,
            new_name = el.Name
        });
    }
}
