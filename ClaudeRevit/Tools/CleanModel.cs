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
        "Remove model bloat: native Purge Unused family types, unused materials, explicitly selected non-linked CAD imports, " +
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
                description = "What to clean. Default excludes non-linked CAD imports; exploded_imports explicitly selects all non-linked imports.",
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
        var doc = ToolContext.UiDocument(app)?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var apply = input.TryGetValue("apply", out var a) && a.ValueKind == JsonValueKind.True;

        var all = new[] { "unused_family_types", "unused_materials", "unplaced_rooms", "empty_groups" };
        var targets = input.TryGetValue("targets", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToHashSet()
            : all.ToHashSet();

        var report = new List<object>();
        // Purge analysis is a whole-model pass; both default targets share one result per call.
        var purgeable = new Lazy<ISet<ElementId>>(() => doc.GetUnusedElements(new HashSet<ElementId>()));
        var toDelete = new List<ElementId>();

        if (targets.Contains("unused_family_types")) report.Add(UnusedFamilyTypes(doc, toDelete, purgeable.Value));
        if (targets.Contains("unused_materials")) report.Add(UnusedMaterials(doc, toDelete, purgeable.Value));
        if (targets.Contains("exploded_imports")) report.Add(ExplodedImports(doc, toDelete));
        if (targets.Contains("unplaced_rooms")) report.Add(UnplacedRooms(doc, toDelete));
        if (targets.Contains("empty_groups")) report.Add(EmptyGroups(doc, toDelete));

        var deleted = 0;
        var failures = new List<object>();
        var cascades = new List<object>();
        var candidates = toDelete.Distinct().Where(id => doc.GetElement(id) != null).ToHashSet();
        // Rehearse actual Revit deletion in a subtransaction even for dry runs, and never cascade
        // outside the candidate set, including on apply. The whole set is rehearsed in one delete
        // first: one regeneration instead of one per candidate, which on a large model is the
        // difference between seconds and minutes. Only when that shows a cascade or an error is
        // each candidate rehearsed on its own, to find which ones to refuse.
        var clean = false;
        if (candidates.Count > 0)
        {
            ToolContext.ThrowIfCancelled();
            using var rehearsal = new SubTransaction(doc);
            rehearsal.Start();
            try
            {
                var removed = doc.Delete(candidates).ToArray();
                clean = removed.All(candidates.Contains);
                if (clean && apply) { rehearsal.Commit(); deleted = removed.Length; }
                else rehearsal.RollBack();
            }
            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                if (rehearsal.GetStatus() == TransactionStatus.Started) rehearsal.RollBack();
            }
        }
        if (!clean)
            foreach (var id in candidates)
            {
                ToolContext.ThrowIfCancelled();
                if (doc.GetElement(id) == null) continue;
                using var sub = new SubTransaction(doc);
                sub.Start();
                try
                {
                    var removed = doc.Delete(id).ToArray();
                    var unexpected = removed.Where(x => !candidates.Contains(x)).ToArray();
                    if (!apply || unexpected.Length > 0) sub.RollBack();
                    else { sub.Commit(); deleted += removed.Length; }
                    if (unexpected.Length > 0)
                    {
                        cascades.Add(new { id = id.Value, unexpected_ids = unexpected.Take(20).Select(x => x.Value).ToArray(), unexpected_count = unexpected.Length });
                        failures.Add(new { id = id.Value, reason = "Dependent elements outside the candidate set: deletion refused." });
                    }
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (sub.GetStatus() == TransactionStatus.Started) sub.RollBack();
                    failures.Add(new { id = id.Value, reason = Short(ex.Message) });
                }
            }

        // Samples stay bounded so the per-target details (what the user is shown before apply)
        // are never pushed out of the response by long id lists.
        return Services.Json.Serialize(new
        {
            preview = !apply,
            mode = apply ? "applied" : "dry_run",
            candidates = candidates.Count,
            deleted_elements = apply ? deleted : 0,
            details = report,
            failures_total = failures.Count,
            failures = failures.Take(50),
            cascades_total = cascades.Count,
            cascades = cascades.Take(50),
            note = apply
                ? "Applied safe candidates only; candidates whose deletion would also remove other elements were refused (see cascades). Each tool call is one undo step."
                : "DRY RUN — nothing was deleted. Show this to the user, then call again with apply=true to proceed."
        });
    }

    // Native Purge Unused understands references in parameters, nested types,
    // painted faces and other model data that instance counting misses.
    private static object UnusedFamilyTypes(Document doc, List<ElementId> sink, ISet<ElementId> purgeable)
    {
        var candidates = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(s => purgeable.Contains(s.Id) && s.Family?.IsInPlace != true).ToList();
        // Retain at least one type in every loaded family.
        var selected = new List<FamilySymbol>();
        foreach (var group in candidates.GroupBy(s => s.Family.Id))
        {
            var family = group.First().Family;
            var ordered = group.OrderBy(s => s.Id.Value).ToList();
            var remove = ordered.Take(Math.Min(ordered.Count, Math.Max(0, family.GetFamilySymbolIds().Count - 1)));
            selected.AddRange(remove);
        }
        sink.AddRange(selected.Select(s => s.Id));
        return new { target = "unused_family_types", count = selected.Count, sample = selected.Take(50).Select(s => $"{s.Family.Name}: {s.Name}") };
    }

    private static object UnusedMaterials(Document doc, List<ElementId> sink, ISet<ElementId> purgeable)
    {
        var materials = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
            .Where(m => purgeable.Contains(m.Id)).ToList();
        sink.AddRange(materials.Select(m => m.Id));
        return new { target = "unused_materials", count = materials.Count, sample = materials.Take(50).Select(m => m.Name) };
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
                if (r.Location != null) continue; // placed but unbounded/redundant rooms are NOT unplaced
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
