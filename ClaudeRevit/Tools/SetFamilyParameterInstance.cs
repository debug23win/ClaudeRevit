using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Flips a family parameter between instance and type. The scripts called fm.MakeInstance on
// several parameters after the fact; this exposes it (and the reverse) directly.
public class SetFamilyParameterInstance : IRevitTool
{
    public string Name => "set_family_parameter_instance";

    public string Description =>
        "Makes a family parameter an instance parameter (is_instance=true) or a type parameter " +
        "(is_instance=false) in the Family Editor. Works only in the Family Editor.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Exact parameter name (case-sensitive)."
            }),
            ["is_instance"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean",
                description = "True → instance parameter; false → type parameter."
            })
        },
        Required = ["name", "is_instance"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");
        var fm = FamilyEditorUtil.Manager(doc);

        var name = input["name"].GetString() ?? "";
        var p = FamilyEditorUtil.Require(fm, name);
        var wantInstance = ToolInput.Flag(input, "is_instance");

        if (p.IsInstance == wantInstance)
            return JsonSerializer.Serialize(new { name, is_instance = p.IsInstance, changed = false });

        if (wantInstance) fm.MakeInstance(p);
        else fm.MakeType(p);
        doc.Regenerate();

        return JsonSerializer.Serialize(new { name, is_instance = p.IsInstance, changed = true });
    }
}
