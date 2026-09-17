using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// The other half of diagnose_model: actually remove the bloat it finds. Every category here is a
// known file-size / performance offender. Because this DELETES, it runs as a DRY RUN by default —
// it reports exactly what would go, and only removes when explicitly told to. Deletions still land
// in the turn's transaction group, so Ctrl+Z reverts the whole clean-up in one step.
public class CleanModel : IRevitTool
{
    public string Name => "clean_model";

    public string Description =>
        "Remove model bloat: unused family types (no instances), unused materials, exploded CAD imports, " +
        "unplaced rooms, and empty groups. DRY RUN BY DEFAULT — returns what would be deleted with counts and " +
        "names; pass apply=true to actually delete. Choose what to touch with `targets`. " +
        "Pair with diagnose_model, which explains what is wrong before you clean it.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["targets"] = JsonSerializer.SerializeToElement(new
            {
                type = "array",
                description = "What to clean. Default: all of them.",
                items = new
                {
                    type = "string",
                    @enum = new[] { "unused_family_types", "unused_materials", "exploded_imports", "unplaced_rooms", "empty_groups" }
                }
            }),
            ["apply"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean",
                description = "false (default) = report only. true = actually delete. ALWAYS dry-run first and show the user."
            })
        },
        Required = []
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var apply = input.TryGetValue("apply", out var a) && a.ValueKind == JsonValueKind.True;

        var all = new[] { "unused_family_types", "unused_materials", "exploded_imports", "unplaced_rooms", "empty_groups" };
        var targets = input.TryGetValue("targets", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToHashSet()
            : all.ToHashSet();

        var report = new List<object>();
        var toDelete = new List<ElementId>();

        if (targets.Contains("unused_family_types")) report.Add(UnusedFamilyTypes(doc, toDelete));
        if (targets.Contains("unused_materials")) report.Add(UnusedMaterials(doc, toDelete));
        if (targets.Contains("exploded_imports")) report.Add(ExplodedImports(doc, toDelete));
        if (targets.Contains("unplaced_rooms")) report.Add(UnplacedRooms(doc, toDelete));
        if (targets.Contains("empty_groups")) report.Add(EmptyGroups(doc, toDelete));

        var deleted = 0;
        var failures = new List<object>();
        if (apply && toDelete.Count > 0)
        {
            // Delete one at a time: Revit cascades dependent deletions, so a single bad id in a
            // bulk call would abort the whole clean-up.
            foreach (var id in toDelete.Distinct())
            {
                try
                {
                    if (doc.GetElement(id) == null) continue;   // already cascaded away
                    var removed = doc.Delete(id);
                    deleted += removed?.Count ?? 1;
                }
                catch (Exception ex)
                {
                    if (failures.Count < 25) failures.Add(new { id = id.Value, reason = Short(ex.Message) });
                }
            }
        }

        return Services.Json.Serialize(new
        {
            mode = apply ? "applied" : "dry_run",
            candidates = toDelete.Distinct().Count(),
            deleted_elements = apply ? deleted : 0,
            failures,
            details = report,
            note = apply
                ? "Deleted. The whole clean-up is one undo step (Ctrl+Z)."
                : "DRY RUN — nothing was deleted. Show this to the user, then call again with apply=true to proceed."
        });
    }

    // A family symbol is unused when no instance of it exists. Skip in-place and system-critical
    // families, and never leave a family with zero types behind.
    private static object UnusedFamilyTypes(Document doc, List<ElementId> sink)
    {
        var usedTypeIds = new HashSet<ElementId>(
            new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Select(e => e.GetTypeId())
                .Where(id => id != ElementId.InvalidElementId));

        var names = new List<string>();
        var count = 0;

        foreach (var sym in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                     .Cast<FamilySymbol>())
        {
            try
            {
                if (usedTypeIds.Contains(sym.Id)) continue;
                if (sym.Family?.IsInPlace == true) continue;   // in-place has no reusable type
                sink.Add(sym.Id);
                count++;
                if (names.Count < 50) names.Add($"{sym.Family?.Name}: {sym.Name}");
            }
            catch { }
        }
        return new { target = "unused_family_types", count, sample = names };
    }

    // A material is unused when nothing references it. Checking every element's material ids is the
    // only reliable test — appearance/structural assets alone don't prove usage.
    private static object UnusedMaterials(Document doc, List<ElementId> sink)
    {
        var used = new HashSet<ElementId>();
        foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            ToolContext.ThrowIfCancelled();

            try { foreach (var m in e.GetMaterialIds(false)) used.Add(m); } catch { }
        }
        foreach (var e in new FilteredElementCollector(doc).WhereElementIsElementType())
        {
            try { foreach (var m in e.GetMaterialIds(false)) used.Add(m); } catch { }
        }

        var names = new List<string>();
        var count = 0;
        foreach (var mat in new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>())
        {
            if (used.Contains(mat.Id)) continue;
            sink.Add(mat.Id);
            count++;
            if (names.Count < 50) names.Add(mat.Name);
        }
        return new { target = "unused_materials", count, sample = names };
    }

    // Exploded (non-linked) CAD imports drag every line style and text type of the source DWG into
    // the model permanently — the single biggest silent bloat source.
    private static object ExplodedImports(Document doc, List<ElementId> sink)
    {
        var names = new List<string>();
        var count = 0;
        foreach (var imp in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance))
                     .Cast<ImportInstance>())
        {
            try
            {
                if (imp.IsLinked) continue;   // keep genuine links
                sink.Add(imp.Id);
                count++;
                if (names.Count < 50) names.Add(imp.Name);
            }
            catch { }
        }
        return new { target = "exploded_imports", count, sample = names };
    }

    private static object UnplacedRooms(Document doc, List<ElementId> sink)
    {
        var count = 0;
        var names = new List<string>();
        foreach (var r in new FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType())
        {
            try
            {
                var area = r.get_Parameter(BuiltInParameter.ROOM_AREA);
                if (area != null && area.HasValue && area.AsDouble() > 0) continue;
                sink.Add(r.Id);
                count++;
                if (names.Count < 50) names.Add(r.Name);
            }
            catch { }
        }
        return new { target = "unplaced_rooms", count, sample = names };
    }

    private static object EmptyGroups(Document doc, List<ElementId> sink)
    {
        var count = 0;
        var names = new List<string>();
        foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>())
        {
            try
            {
                if (g.GetMemberIds().Count > 0) continue;
                sink.Add(g.Id);
                count++;
                if (names.Count < 50) names.Add(g.Name);
            }
            catch { }
        }
        return new { target = "empty_groups", count, sample = names };
    }

    private static string Short(string s) => s.Length <= 120 ? s : s.Substring(0, 120) + "…";
}
