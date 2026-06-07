using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class DuplicateGroupType : IRevitTool
{
    public string Name => "duplicate_group_type";

    public string Description =>
        "Duplicates a model group type under a new name. Edits to the original type's members no longer " +
        "affect the duplicate. Pair with place_group to drop instances of the new type.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["source_group_type_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Source GroupType id." }),
            ["new_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "New name (must be unique)." })
        },
        Required = ["source_group_type_id", "new_name"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var srcId = new ElementId(input["source_group_type_id"].GetInt64());
        var src = doc.GetElement(srcId) as GroupType
            ?? throw new InvalidOperationException($"Element {srcId.Value} is not a GroupType.");

        var newName = input["new_name"].GetString()!;
        var dup = src.Duplicate(newName) as GroupType
            ?? throw new InvalidOperationException("Duplicate failed.");

        return JsonSerializer.Serialize(new
        {
            id = dup.Id.Value,
            name = dup.Name,
            duplicated_from = src.Name
        });
    }
}
