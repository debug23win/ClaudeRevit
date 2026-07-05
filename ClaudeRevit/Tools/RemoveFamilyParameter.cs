using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Removes a family parameter by name. Useful to recreate a parameter whose spec/instance
// flag was wrong — the scripts did exactly this remove-then-re-add dance repeatedly.
public class RemoveFamilyParameter : IRevitTool
{
    public string Name => "remove_family_parameter";

    public string Description =>
        "Removes a family parameter by name in the Family Editor. Any formula referencing it will " +
        "break, so remove dependents first. Works only in the Family Editor.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Exact parameter name to remove (case-sensitive)."
            })
        },
        Required = ["name"]
    };

    public bool RequiresTransaction => true;
    public bool RequiresConfirmation => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");
        var fm = FamilyEditorUtil.Manager(doc);

        var name = input["name"].GetString() ?? "";
        var p = FamilyEditorUtil.Require(fm, name);
        fm.RemoveParameter(p);
        doc.Regenerate();

        return JsonSerializer.Serialize(new { removed = true, name });
    }
}
