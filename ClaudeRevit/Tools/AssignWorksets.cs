using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Workset assignment by rule. In a workshared model the discipline of "structure in the Structure
// workset, MEP in MEP" is what keeps a team's model loadable — but Revit gives no bulk way to
// enforce it, so it's normally a manual slog. This creates the target workset if needed and moves
// whole categories/levels into it in one transaction.
public class AssignWorksets : IRevitTool
{
    public string Name => "assign_worksets";

    public string Description =>
        "Move elements into a workset in a workshared model, by category (optionally on one level) or an " +
        "explicit id list. Creates the workset if it doesn't exist (create_if_missing, default true). " +
        "Elements that are pinned, not editable by you, or owned by another user are reported rather than " +
        "silently skipped. Use list_worksets to see what exists. Requires a workshared document.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["workset_name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Target workset, e.g. 'Structure'."
            }),
            ["category"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Move every element of this category, e.g. 'Structural Columns'."
            }),
            ["element_ids"] = JsonSerializer.SerializeToElement(new
            {
                type = "array", description = "Explicit elements instead of a category.", items = new { type = "integer" }
            }),
            ["on_level"] = JsonSerializer.SerializeToElement(new { type = "string", description = "With `category`: restrict to this level." }),
            ["create_if_missing"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean", description = "Create the workset when it doesn't exist. Default true."
            })
        },
        Required = ["workset_name"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        if (!doc.IsWorkshared)
            throw new InvalidOperationException(
                "This document is not workshared — worksets don't exist. Enable worksharing in Revit first.");

        var name = input["workset_name"].GetString()
            ?? throw new InvalidOperationException("workset_name is required.");
        var createIfMissing = !input.TryGetValue("create_if_missing", out var c) || c.ValueKind != JsonValueKind.False;

        var target = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)
            .FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));

        if (target == null)
        {
            if (!createIfMissing)
                throw new InvalidOperationException($"Workset '{name}' not found (create_if_missing is false).");
            if (!WorksetTable.IsWorksetNameUnique(doc, name))
                throw new InvalidOperationException($"'{name}' can't be used as a new workset name.");
            target = Workset.Create(doc, name);
        }

        var elements = Collect(doc, input);
        var moved = new List<long>();
        var skipped = new List<object>();

        foreach (var el in elements)
        {
            try
            {
                var p = el.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (p == null) { skipped.Add(new { id = el.Id.Value, reason = "no workset parameter (not a model element)" }); continue; }
                if (p.IsReadOnly) { skipped.Add(new { id = el.Id.Value, reason = "workset is read-only here (element may be borrowed by another user)" }); continue; }
                if (p.AsInteger() == target.Id.IntegerValue) continue;   // already there

                p.Set(target.Id.IntegerValue);
                moved.Add(el.Id.Value);
            }
            catch (Exception ex)
            {
                if (skipped.Count < 50) skipped.Add(new { id = el.Id.Value, reason = Short(ex.Message) });
            }
        }

        return JsonSerializer.Serialize(new
        {
            workset = target.Name,
            workset_id = target.Id.IntegerValue,
            considered = elements.Count,
            moved = moved.Count,
            already_there = elements.Count - moved.Count - skipped.Count,
            skipped,
            sample = moved.Take(100)
        });
    }

    private static List<Element> Collect(Document doc, IReadOnlyDictionary<string, JsonElement> input)
    {
        if (input.TryGetValue("element_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            return ids.EnumerateArray().Select(e => doc.GetElement(new ElementId(e.GetInt64())))
                      .Where(e => e != null).ToList()!;

        if (!input.TryGetValue("category", out var cat) || cat.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Provide either element_ids or category.");

        var bic = CategoryResolve.Parse(cat.GetString());
        var q = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToList();

        if (input.TryGetValue("on_level", out var lv) && lv.ValueKind == JsonValueKind.String)
        {
            var lname = lv.GetString();
            var lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(x => string.Equals(x.Name, lname, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Level '{lname}' not found.");
            q = q.Where(e => e.LevelId == lvl.Id).ToList();
        }
        return q;
    }

    private static string Short(string s) => s.Length <= 120 ? s : s.Substring(0, 120) + "…";
}
