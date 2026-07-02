using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// One-call project survey: everything an agent normally gathers with a dozen list_* calls
// at the start of a session. The result is cached per document for the Revit session, so
// repeat calls are free — pass refresh=true after loading families or creating types.
public class GetProjectCatalog : IRevitTool
{
    public string Name => "get_project_catalog";

    public string Description =>
        "Returns a one-call survey of the project's assets: levels, view templates (with view " +
        "kind/discipline), loadable family types grouped by category, system types (walls, " +
        "floors, roofs), materials, and the full reinforcement catalog (bar types with " +
        "diameters, rebar shapes, hook types, cover types, area/path reinforcement types). " +
        "Call this ONCE when starting work on a project instead of many separate list_* calls. " +
        "The result is cached for the session; pass refresh=true after loading new families or " +
        "creating new types.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["refresh"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean",
                description = "Rebuild the catalog even if a cached one exists (default false)."
            })
        },
        Required = []
    };

    public bool RequiresTransaction => false;

    // Keyed on the Document INSTANCE hash as well: Title+PathName alone collides for two
    // successive unsaved documents ("Project1" + empty path) and would serve element ids
    // from a closed document.
    private static readonly ConcurrentDictionary<string, JsonElement> Cache = new();

    // Called by ToolDispatcher after any successful mutating tool call — the catalog must
    // not outlive type/family changes made by the assistant itself. (User edits and Ctrl+Z
    // are still invisible; the refresh flag remains the manual escape hatch.)
    public static void Invalidate() => Cache.Clear();

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var cacheKey = doc.Title + "|" + doc.PathName + "|" + doc.GetHashCode();
        bool refresh = input.TryGetValue("refresh", out var r) && r.ValueKind == JsonValueKind.True;

        if (refresh || !Cache.TryGetValue(cacheKey, out var catalog))
        {
            catalog = BuildCatalog(doc);
            Cache[cacheKey] = catalog;
            return JsonSerializer.Serialize(new { cached = false, note = (string?)null, catalog });
        }

        return JsonSerializer.Serialize(new
        {
            cached = true,
            note = "Served from the session cache — pass refresh=true if families/types changed " +
                   "outside this assistant (its own tool calls invalidate the cache automatically).",
            catalog
        });
    }

    // Lists are capped so a big production model can't blow up the response (and the
    // conversation context) — the dedicated list_* tools return full data when needed.
    private static List<string> Cap(List<string> items, int max) =>
        items.Count <= max
            ? items
            : items.Take(max).Append($"… +{items.Count - max} more (use the dedicated list tool for the full set)").ToList();

    private static JsonElement BuildCatalog(Document doc)
    {
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => l.Elevation)
            .Select(l => new { id = l.Id.Value, name = l.Name, elevation_ft = Math.Round(l.Elevation, 3) })
            .ToList();

        var viewTemplates = ListViewTemplates.Survey(doc);

        // Loadable family types, grouped by category: category → ["Family: Type", …]
        var familyTypes = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(s => s.Category != null)
            .GroupBy(s => s.Category!.Name)
            .OrderBy(g => g.Key)
            .ToDictionary(
                g => g.Key,
                g => Cap(g.Select(s => $"{s.FamilyName}: {s.Name}").OrderBy(n => n).ToList(), 60));

        var wallTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType))
            .Select(t => t.Name).OrderBy(n => n).ToList();
        var floorTypes = new FilteredElementCollector(doc).OfClass(typeof(FloorType))
            .Select(t => t.Name).OrderBy(n => n).ToList();
        var roofTypes = new FilteredElementCollector(doc).OfClass(typeof(RoofType))
            .Select(t => t.Name).OrderBy(n => n).ToList();

        var materials = Cap(new FilteredElementCollector(doc).OfClass(typeof(Material))
            .Select(m => m.Name).OrderBy(n => n).ToList(), 150);

        var barTypes = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>()
            .Select(t => new { name = t.Name, diameter_mm = Math.Round(t.BarNominalDiameter * 304.8, 1) })
            .OrderBy(t => t.diameter_mm).ToList();
        var shapes = new FilteredElementCollector(doc).OfClass(typeof(RebarShape))
            .Select(s => s.Name).OrderBy(n => n).ToList();
        var hooks = new FilteredElementCollector(doc).OfClass(typeof(RebarHookType))
            .Select(h => h.Name).OrderBy(n => n).ToList();
        var coverTypes = ListRebarCoverTypes.Survey(doc);
        var areaTypes = new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcementType))
            .Select(t => t.Name).OrderBy(n => n).ToList();
        var pathTypes = new FilteredElementCollector(doc).OfClass(typeof(PathReinforcementType))
            .Select(t => t.Name).OrderBy(n => n).ToList();

        return JsonSerializer.SerializeToElement(new
        {
            document = doc.Title,
            levels,
            view_templates = viewTemplates,
            family_types_by_category = familyTypes,
            system_types = new { walls = wallTypes, floors = floorTypes, roofs = roofTypes },
            materials,
            reinforcement = new
            {
                bar_types = barTypes,
                shapes,
                hook_types = hooks,
                cover_types = coverTypes,
                area_reinforcement_types = areaTypes,
                path_reinforcement_types = pathTypes
            }
        });
    }
}
