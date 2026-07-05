using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Sets (or clears) the formula on a family parameter and regenerates, reporting the new
// computed value. This was the single most-repeated Family Editor script operation.
public class SetFamilyParameterFormula : IRevitTool
{
    public string Name => "set_family_parameter_formula";

    public string Description =>
        "Sets the formula on a family parameter in the Family Editor, then regenerates and reports the " +
        "new computed value. Formula syntax is Revit's own (e.g. \"Length / 4\", " +
        "\"roundup(Zone / Step) + 1\", \"if(Count > 1, Count - 1, 1)\"); reference other parameters by " +
        "their exact names. Pass an empty formula to clear it. Works only in the Family Editor.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Exact name of the parameter to drive (case-sensitive)."
            }),
            ["formula"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "The Revit formula. Empty string clears the formula."
            })
        },
        Required = ["name", "formula"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");
        var fm = FamilyEditorUtil.Manager(doc);

        var name = input["name"].GetString() ?? "";
        var p = FamilyEditorUtil.Require(fm, name);
        var formula = input["formula"].GetString() ?? "";

        try
        {
            fm.SetFormula(p, string.IsNullOrWhiteSpace(formula) ? null : formula);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Revit rejected the formula for '{name}': {ex.Message}. Check that every referenced " +
                "parameter exists (get_family_parameters) and the result type matches the parameter's spec.");
        }
        doc.Regenerate();

        var (raw, mm, display) = FamilyEditorUtil.CurrentValue(fm, p);
        return JsonSerializer.Serialize(new
        {
            name,
            formula = string.IsNullOrWhiteSpace(formula) ? null : formula,
            cleared = string.IsNullOrWhiteSpace(formula),
            value_raw = raw,
            value_mm = mm,
            value_display = display
        });
    }
}
