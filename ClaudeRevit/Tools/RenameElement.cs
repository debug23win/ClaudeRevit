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


    // Adds or renames something the project catalog lists.

    public bool InvalidatesCatalog => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = ToolContext.UiDocument(app)?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var id = new ElementId(ToolInput.RequiredLong(input, "element_id"));
        var el = doc.GetElement(id)
            ?? throw new InvalidOperationException($"Element {id.Value} not found.");

        var newName = ToolInput.RequiredText(input, "new_name")!;
        var oldName = el.Name;
        el.Name = newName;

        return Services.Json.Serialize(new
        {
            id = id.Value,
            category = el.Category?.Name,
            old_name = oldName,
            new_name = el.Name
        });
    }
}
