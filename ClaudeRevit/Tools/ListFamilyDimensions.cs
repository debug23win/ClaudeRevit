using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// Lists dimensions with the family parameter they label and the references they measure —
// the recurring "which dimension drives which parameter, and does it still hold" inspection
// in the Family Editor. Pairs with create_family_dimension.
public class ListFamilyDimensions : IRevitTool
{
    public string Name => "list_family_dimensions";

    public string Description =>
        "Lists dimensions in the active document with their value (mm), the family parameter they label " +
        "(if any) and the reference planes / elements they measure between. By default only labeled " +
        "dimensions (the ones driving or reporting family parameters); set all=true to include every " +
        "dimension. Use it in the Family Editor to see which dimensions are wired to which parameters.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["all"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean",
                description = "Include unlabeled dimensions too (default false)."
            })
        },
        Required = []
    };

    public bool RequiresTransaction => false;

    private const double FeetToMm = Units.MmPerFoot;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");
        var includeAll = ToolInput.Flag(input, "all");

        var list = new List<object>();
        foreach (var d in new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>())
        {
            // FamilyLabel only makes sense in a family document and can throw elsewhere.
            string? label = null;
            try { label = d.FamilyLabel?.Definition.Name; } catch { /* not a family / no label */ }
            if (!includeAll && label == null) continue;

            double? valueMm = null;
            try { valueMm = d.Value.HasValue ? d.Value.Value * FeetToMm : null; } catch { }

            var refs = new List<string>();
            try
            {
                foreach (Reference r in d.References)
                {
                    var el = doc.GetElement(r.ElementId);
                    refs.Add(el is ReferencePlane rp
                        ? $"plane:'{rp.Name}'#{rp.Id.Value}"
                        : el != null ? $"{el.GetType().Name}#{r.ElementId.Value}" : $"#{r.ElementId.Value}");
                }
            }
            catch { /* references not enumerable */ }

            list.Add(new
            {
                id = d.Id.Value,
                label,
                value_mm = valueMm,
                references = refs
            });
        }

        return Json.Serialize(new { count = list.Count, dimensions = list });
    }
}
