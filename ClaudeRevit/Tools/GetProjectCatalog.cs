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

    private static readonly ConcurrentDictionary<string, string> Cache = new();

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var cacheKey = doc.Title + "|" + doc.PathName;
        bool refresh = input.TryGetValue("refresh", out var r) && r.ValueKind == JsonValueKind.True;

        if (!refresh && Cache.TryGetValue(cacheKey, out var cached))
            return WrapCached(cached, cached: true);

        var catalog = BuildCatalog(doc);
        Cache[cacheKey] = catalog;
        return WrapCached(catalog, cached: false);
    }

    private static string WrapCached(string catalogJson, bool cached)
    {
        using var parsed = JsonDocument.Parse(catalogJson);
        return JsonSerializer.Serialize(new
        {
            cached,
            note = cached
                ? "Served from the session cache — pass refresh=true if families/types changed since it was built."
                : (string?)null,
            catalog = parsed.RootElement.Clone()
        });
    }

    private static string BuildCatalog(Document doc)
    {
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => l.Elevation)
            .Select(l => new { id = l.Id.Value, name = l.Name, elevation_ft = Math.Round(l.Elevation, 3) })
            .ToList();

        var viewTemplates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => v.IsTemplate)
            .Select(v =>
            {
                string? discipline;
                try { discipline = v.Discipline.ToString(); } catch { discipline = null; }
                return new { id = v.Id.Value, name = v.Name, view_type = v.ViewType.ToString(), discipline };
            })
            .OrderBy(t => t.view_type).ThenBy(t => t.name)
            .ToList();

        // Loadable family types, grouped by category: category → ["Family: Type", …]
        var familyTypes = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(s => s.Category != null)
            .GroupBy(s => s.Category!.Name)
            .OrderBy(g => g.Key)
            .ToDictionary(
                g => g.Key,
                g => g.Select(s => $"{s.FamilyName}: {s.Name}").OrderBy(n => n).ToList());

        var wallTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType))
            .Select(t => t.Name).OrderBy(n => n).ToList();
        var floorTypes = new FilteredElementCollector(doc).OfClass(typeof(FloorType))
            .Select(t => t.Name).OrderBy(n => n).ToList();
        var roofTypes = new FilteredElementCollector(doc).OfClass(typeof(RoofType))
            .Select(t => t.Name).OrderBy(n => n).ToList();

        var materials = new FilteredElementCollector(doc).OfClass(typeof(Material))
            .Select(m => m.Name).OrderBy(n => n).ToList();

        var barTypes = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>()
            .Select(t => new { name = t.Name, diameter_mm = Math.Round(t.BarNominalDiameter * 304.8, 1) })
            .OrderBy(t => t.diameter_mm).ToList();
        var shapes = new FilteredElementCollector(doc).OfClass(typeof(RebarShape))
            .Select(s => s.Name).OrderBy(n => n).ToList();
        var hooks = new FilteredElementCollector(doc).OfClass(typeof(RebarHookType))
            .Select(h => h.Name).OrderBy(n => n).ToList();
        var coverTypes = new FilteredElementCollector(doc).OfClass(typeof(RebarCoverType)).Cast<RebarCoverType>()
            .Select(t => new { id = t.Id.Value, name = t.Name, cover_mm = Math.Round(t.CoverDistance * 304.8, 1) })
            .OrderBy(t => t.cover_mm).ToList();
        var areaTypes = new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcementType))
            .Select(t => t.Name).OrderBy(n => n).ToList();
        var pathTypes = new FilteredElementCollector(doc).OfClass(typeof(PathReinforcementType))
            .Select(t => t.Name).OrderBy(n => n).ToList();

        return JsonSerializer.Serialize(new
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
