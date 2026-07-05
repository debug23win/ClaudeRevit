using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// Lists the parameters of the family currently open in the Family Editor: name, whether it
// is an instance or type parameter, its data type, formula, and current value (mm for
// lengths). This replaces the recurring "iterate fm.Parameters and print" script.
public class GetFamilyParameters : IRevitTool
{
    public string Name => "get_family_parameters";

    public string Description =>
        "Lists the family parameters of the family open in the Family Editor: name, instance/type, " +
        "data type, formula (if any) and current value (also in mm for lengths). Use it to discover " +
        "exact parameter names before adding formulas or associations. Works only in the Family " +
        "Editor. Optionally filter by a substring, or show only formula-driven parameters.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["filter"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Only return parameters whose name contains this substring (case-insensitive)."
            }),
            ["formulas_only"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean",
                description = "If true, return only parameters that are driven by a formula."
            })
        },
        Required = []
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");
        var fm = FamilyEditorUtil.Manager(doc);

        var filter = input.TryGetValue("filter", out var f) && f.ValueKind == JsonValueKind.String
            ? f.GetString() : null;
        var formulasOnly = ToolInput.Flag(input, "formulas_only");

        var list = new List<object>();
        var errored = new List<string>();
        foreach (FamilyParameter p in fm.Parameters)
        {
            var name = p.Definition.Name;
            // Health check runs over every parameter (even filtered-out ones), so the
            // errored count reflects the whole family, not just the shown subset.
            var isErrored = FamilyEditorUtil.ValueErrors(fm, p);
            if (isErrored) errored.Add(name);

            if (!string.IsNullOrEmpty(filter) &&
                name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (formulasOnly && !p.IsDeterminedByFormula) continue;

            var (raw, mm, display) = FamilyEditorUtil.CurrentValue(fm, p);
            list.Add(new
            {
                name,
                is_instance = p.IsInstance,
                is_reporting = SafeReporting(p),
                storage = p.StorageType.ToString(),
                spec = FamilyEditorUtil.SpecId(p),
                formula = p.IsDeterminedByFormula ? (p.Formula ?? "") : null,
                value_raw = raw,
                value_mm = mm,
                value_display = display,
                errored = isErrored
            });
        }

        return JsonSerializer.Serialize(new
        {
            family = doc.Title,
            is_family_document = true,
            type_count = fm.Types.Size,
            current_type = SafeCurrentTypeName(fm),
            count = list.Count,
            // Family health: parameters whose value can't be evaluated (broken formula /
            // constraint). errored_count == 0 means the family regenerates cleanly.
            errored_count = errored.Count,
            errored_parameters = errored,
            parameters = list
        });
    }

    private static bool? SafeReporting(FamilyParameter p)
    {
        try { return p.IsReporting; } catch { return null; }
    }

    private static string? SafeCurrentTypeName(FamilyManager fm)
    {
        try { return fm.CurrentType?.Name; } catch { return null; }
    }
}
