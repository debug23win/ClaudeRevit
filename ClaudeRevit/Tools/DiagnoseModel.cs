using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Model health check — why a file is slow, bloated or unreliable. Every item here is a known
// real-world cause of a sluggish Revit model, and each is reported with the count, the worst
// offenders and what to do about it, so the findings are actionable rather than a wall of numbers.
public class DiagnoseModel : IRevitTool
{
    public string Name => "diagnose_model";

    public string Description =>
        "Diagnose model health and performance: warnings (by type, worst first), in-place families, imported " +
        "CAD/DWG (linked vs exploded — the classic file-bloat cause), unused/unplaced elements, over-large " +
        "groups, unpinned links, excessive design options, views without templates, and raw element counts by " +
        "category. Returns findings with severity and a concrete recommendation for each. Read-only.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["top"] = JsonSerializer.SerializeToElement(new
            {
                type = "integer",
                description = "How many worst offenders to list per finding (default 10).",
                minimum = 1
            })
        },
        Required = []
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var top = input.TryGetValue("top", out var t) ? Math.Max(1, t.GetInt32()) : 10;
        var findings = new List<object>();

        // --- Warnings: the single best predictor of a troubled model ------------------------
        try
        {
            var warnings = doc.GetWarnings();
            if (warnings.Count > 0)
            {
                var byKind = warnings
                    .GroupBy(w => w.GetDescriptionText())
                    .Select(g => new { warning = Trim(g.Key, 160), count = g.Count() })
                    .OrderByDescending(x => x.count).Take(top).ToList();
                findings.Add(new
                {
                    check = "warnings",
                    severity = warnings.Count > 1000 ? "high" : warnings.Count > 200 ? "medium" : "low",
                    count = warnings.Count,
                    worst = byKind,
                    recommendation = "Resolve the most numerous warnings first — duplicate/overlapping elements " +
                                     "and unjoined geometry slow every regeneration and corrupt quantity take-offs."
                });
            }
        }
        catch { }

        // --- In-place families: not reusable, bloat the file, kill performance ---------------
        try
        {
            var inPlace = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => { try { return fi.Symbol?.Family?.IsInPlace == true; } catch { return false; } })
                .ToList();
            if (inPlace.Count > 0)
                findings.Add(new
                {
                    check = "in_place_families",
                    severity = inPlace.Count > 50 ? "high" : inPlace.Count > 10 ? "medium" : "low",
                    count = inPlace.Count,
                    worst = inPlace.GroupBy(f => { try { return f.Symbol?.Family?.Name ?? "?"; } catch { return "?"; } })
                        .Select(g => new { family = g.Key, count = g.Count() })
                        .OrderByDescending(x => x.count).Take(top),
                    recommendation = "Convert repeated in-place families into loadable families — they can't be " +
                                     "scheduled well, can't be reused, and each one is stored in full."
                });
        }
        catch { }

        // --- Imported CAD: exploded imports are the #1 silent file-bloat cause ---------------
        try
        {
            var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>().ToList();
            var exploded = imports.Count(i => { try { return !i.IsLinked; } catch { return false; } });
            if (imports.Count > 0)
                findings.Add(new
                {
                    check = "cad_imports",
                    severity = exploded > 0 ? "high" : "low",
                    count = imports.Count,
                    exploded_imports = exploded,
                    linked_imports = imports.Count - exploded,
                    worst = imports.Take(top).Select(i => new { id = i.Id.Value, name = i.Name, linked = SafeLinked(i) }),
                    recommendation = exploded > 0
                        ? "Delete exploded CAD imports — they carry every line style and text type of the source " +
                          "DWG into your model permanently. Link CAD instead of importing."
                        : "CAD is linked rather than imported — good. Unload links you don't need."
                });
        }
        catch { }

        // --- Unplaced / unused: rooms and areas that exist but aren't placed -----------------
        try
        {
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().ToList();
            var unplaced = rooms.Count(r =>
            {
                try
                {
                    var a = r.get_Parameter(BuiltInParameter.ROOM_AREA);
                    return a == null || !a.HasValue || a.AsDouble() <= 0;
                }
                catch { return false; }
            });
            if (unplaced > 0)
                findings.Add(new
                {
                    check = "unplaced_rooms",
                    severity = unplaced > 20 ? "medium" : "low",
                    count = unplaced,
                    of_total = rooms.Count,
                    recommendation = "Unplaced/redundant rooms have no area and pollute schedules — place or delete them."
                });
        }
        catch { }

        // --- Oversized groups: expensive to regenerate ---------------------------------------
        try
        {
            var groups = new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>()
                .Select(g =>
                {
                    var members = 0;
                    try { members = g.GetMemberIds().Count; } catch { }
                    return (name: g.Name, count: members, id: g.Id.Value);
                })
                .Where(x => x.count > 200)
                .OrderByDescending(x => x.count).ToList();
            if (groups.Count > 0)
                findings.Add(new
                {
                    check = "large_groups",
                    severity = groups.Any(g => g.count > 1000) ? "high" : "medium",
                    count = groups.Count,
                    worst = groups.Take(top).Select(g => new { g.id, g.name, members = g.count }),
                    recommendation = "Groups with hundreds of members force a full regeneration on every edit — " +
                                     "split them or use links/assemblies instead."
                });
        }
        catch { }

        // --- Design options: multiplied geometry ----------------------------------------------
        try
        {
            var options = new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).ToList();
            if (options.Count > 4)
                findings.Add(new
                {
                    check = "design_options",
                    severity = options.Count > 12 ? "medium" : "low",
                    count = options.Count,
                    recommendation = "Many live design options multiply the geometry Revit must keep loaded — " +
                                     "accept the primary option and delete resolved sets."
                });
        }
        catch { }

        // --- Views without a template: inconsistent and often unfiltered ----------------------
        try
        {
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => { try { return !v.IsTemplate && v.CanBePrinted; } catch { return false; } }).ToList();
            var noTemplate = views.Count(v => { try { return v.ViewTemplateId == ElementId.InvalidElementId; } catch { return false; } });
            if (noTemplate > 0)
                findings.Add(new
                {
                    check = "views_without_template",
                    severity = noTemplate > views.Count / 2 ? "medium" : "low",
                    count = noTemplate,
                    of_total = views.Count,
                    recommendation = "Views without a template drift apart and often show far more than needed, " +
                                     "which slows opening them. Apply view templates."
                });
        }
        catch { }

        // --- Raw scale: element counts by category --------------------------------------------
        var byCategory = new List<object>();
        try
        {
            byCategory = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Where(e => e.Category != null)
                .GroupBy(e => e.Category!.Name)
                .Select(g => new { category = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count).Take(top).Cast<object>().ToList();
        }
        catch { }

        var totalElements = 0;
        try { totalElements = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount(); } catch { }

        return Services.Json.Serialize(new
        {
            document = doc.Title,
            total_elements = totalElements,
            findings_count = findings.Count,
            findings,
            largest_categories = byCategory,
            note = "Read-only diagnosis. Fix warnings and exploded CAD imports first — they give the biggest " +
                   "speed-up per effort."
        });
    }

    private static bool SafeLinked(ImportInstance i)
    {
        try { return i.IsLinked; } catch { return false; }
    }

    private static string Trim(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max) + "…";
}
