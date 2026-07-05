using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Adds a new family parameter (or returns the existing one with the same name). Replaces
// the recurring "AddParameter if not present" helper the scripts kept re-declaring.
public class AddFamilyParameter : IRevitTool
{
    public string Name => "add_family_parameter";

    public string Description =>
        "Adds a family parameter in the Family Editor. Specify the name, a data type (length, number, " +
        "integer, yesno, text, angle, area, volume, force), whether it is an instance parameter, and " +
        "optionally the UI group. If a parameter with that name already exists it is returned unchanged " +
        "(created=false). Works only in the Family Editor.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Parameter name (case-sensitive; may contain Cyrillic)."
            }),
            ["type"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                @enum = new[] { "length", "number", "integer", "yesno", "text", "angle", "area", "volume", "force" },
                description = "The parameter's data type."
            }),
            ["is_instance"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean",
                description = "True for an instance parameter, false (default) for a type parameter."
            }),
            ["group"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Optional UI group: geometry (default), constraints, data, text, materials, " +
                              "identity, construction."
            })
        },
        Required = ["name", "type"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");
        var fm = FamilyEditorUtil.Manager(doc);

        var name = input["name"].GetString();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("name is empty.");

        var existing = FamilyEditorUtil.Find(fm, name);
        if (existing != null)
            return JsonSerializer.Serialize(new
            {
                created = false,
                name,
                is_instance = existing.IsInstance,
                spec = FamilyEditorUtil.SpecId(existing),
                note = "A parameter with this name already exists; returned unchanged."
            });

        var spec = FamilyEditorUtil.SpecFor(input["type"].GetString() ?? "");
        var group = FamilyEditorUtil.GroupFor(
            input.TryGetValue("group", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null);
        var isInstance = ToolInput.Flag(input, "is_instance");

        var p = fm.AddParameter(name, group, spec, isInstance);
        doc.Regenerate();

        return JsonSerializer.Serialize(new
        {
            created = true,
            name,
            is_instance = p.IsInstance,
            storage = p.StorageType.ToString(),
            spec = FamilyEditorUtil.SpecId(p)
        });
    }
}
