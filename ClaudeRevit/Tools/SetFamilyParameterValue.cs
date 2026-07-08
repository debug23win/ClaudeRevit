using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Sets the value of a family parameter under the current type (only works on parameters not
// driven by a formula). Lengths accept millimetres directly, which is how families are
// authored — avoids the /Units.MmPerFoot conversion the scripts repeated everywhere.
public class SetFamilyParameterValue : IRevitTool
{
    public string Name => "set_family_parameter_value";

    public string Description =>
        "Sets a family parameter's value under the family's current type in the Family Editor. For a " +
        "length parameter, 'value' is in MILLIMETRES; for integer/number it is the raw number; for a " +
        "yes/no parameter pass true/false; for text pass a string. Fails if the parameter is driven by " +
        "a formula (clear the formula first). Works only in the Family Editor.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Exact parameter name (case-sensitive)."
            }),
            ["value"] = JsonSerializer.SerializeToElement(new
            {
                description = "Length in mm (number), or raw number/integer, or boolean, or string — " +
                              "matching the parameter's type.",
                oneOf = new object[]
                {
                    new { type = "number" },
                    new { type = "boolean" },
                    new { type = "string" }
                }
            })
        },
        Required = ["name", "value"]
    };

    public bool RequiresTransaction => true;

    private const double MmToFeet = 1.0 / Units.MmPerFoot;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");
        var fm = FamilyEditorUtil.Manager(doc);

        var name = input["name"].GetString() ?? "";
        var p = FamilyEditorUtil.Require(fm, name);
        if (p.IsDeterminedByFormula)
            throw new InvalidOperationException(
                $"'{name}' is driven by a formula and cannot take a direct value. Clear its formula " +
                "with set_family_parameter_formula (empty formula) first.");
        if (fm.CurrentType == null)
            throw new InvalidOperationException(
                "The family has no types, so there is nothing to set the value on. Add a family type first.");

        var value = input["value"];
        switch (p.StorageType)
        {
            case StorageType.Double:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var d))
                    throw Mismatch(name, "a number");
                // Length params are quoted in mm for the user's convenience; other doubles
                // (Number, Angle in radians…) are set as-is.
                fm.Set(p, FamilyEditorUtil.IsLength(p) ? d * MmToFeet : d);
                break;

            case StorageType.Integer:
                if (value.ValueKind == JsonValueKind.True) fm.Set(p, 1);
                else if (value.ValueKind == JsonValueKind.False) fm.Set(p, 0);
                else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var iv)) fm.Set(p, iv);
                else throw Mismatch(name, "an integer or boolean");
                break;

            case StorageType.String:
                fm.Set(p, value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString());
                break;

            default:
                throw new InvalidOperationException(
                    $"Parameter '{name}' has storage type {p.StorageType}, which this tool cannot set.");
        }
        doc.Regenerate();

        var (raw, mm, display) = FamilyEditorUtil.CurrentValue(fm, p);
        return JsonSerializer.Serialize(new
        {
            name,
            value_raw = raw,
            value_mm = mm,
            value_display = display
        });
    }

    private static InvalidOperationException Mismatch(string name, string expected) =>
        new($"Parameter '{name}' expects {expected}.");
}
