using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeRevit.Tools;

// Groups tools into coarse categories so the user can switch whole groups off in Settings.
// The point is token budget: every request re-sends the full tool list (and on
// OpenAI-compatible providers there is no prompt caching, so it is re-counted every turn),
// which alone can blow a free tier's tokens-per-minute limit. Turning off the groups you
// don't need shrinks each request a lot. Categorisation is a coarse, class-name heuristic —
// it only has to be good enough to disable big chunks (MEP, sheets, schedules, views…).
public static class ToolCatalog
{
    // Display order for the settings checkboxes; unknown categories are appended after.
    public static readonly string[] Order =
    {
        "Query", "Modeling", "Family editor", "Rebar", "Annotation", "Views",
        "Visibility", "Sheets", "Schedules", "Groups", "MEP", "Export", "Code & learning"
    };

    public static string CategoryOf(IRevitTool tool)
    {
        var n = tool.GetType().Name;
        bool Has(params string[] keys) => keys.Any(k => n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

        // Specific groups first so a broad keyword (e.g. "Delete", "Family") doesn't steal a
        // tool that belongs to a narrower group.
        if (Has("Rebar", "Reinforcement")) return "Rebar";
        if (Has("FamilyParameter", "FamilyDimension", "LinearArray", "FamilyInstances", "ReferencePlanes", "AssociateFamily"))
            return "Family editor";
        if (Has("SaveTool", "DeleteTool", "EjectTool", "ToolSource", "CustomTools", "Memory", "Journal",
                "DiagnosticReport", "FullResult", "ExecuteCSharp", "RunDynamo", "DependentElements"))
            return "Code & learning";
        if (Has("Duct", "Pipe")) return "MEP";
        if (Has("Schedule")) return "Schedules";
        if (Has("Sheet", "Viewport")) return "Sheets";
        if (Has("Export")) return "Export";
        if (Has("Dimension", "Tag", "TextNote", "DetailLine", "FilledRegion", "Revision", "SpotElevation", "SpotCoordinate"))
            return "Annotation";
        if (Has("View", "Section", "Elevation", "Callout", "Camera", "Crop", "Scale", "Drafting"))
            return "Views";
        if (Has("Group")) return "Groups";
        if (Has("Hide", "Isolate", "Filter", "Color", "ResetView", "CategoryInView")) return "Visibility";
        if (Has("Wall", "Floor", "Roof", "Column", "Beam", "Foundation", "Room", "Grid", "Level",
                "CurtainWall", "Opening", "Topography", "ModelLine", "Shaft", "Material", "SketchPlane",
                "JoinGeometry", "Mirror", "Array", "Move", "Rotate", "Copy", "Delete", "Pin", "Rename",
                "FlipWall", "Symbol", "PlaceFamily", "LoadFamily", "Family", "Door", "Window", "ChangeElementType",
                "SetParameter", "SetType", "PlaceSymbol", "PlaceGroup", "Duplicate"))
            return "Modeling";
        return "Query";
    }

    // ------------------------------------------------------------------------------------------
    // Progressive tool loading. The single biggest token sink is the tool catalogue: ~180 tools
    // are re-sent on EVERY request (fully re-billed on alt providers, which have no prompt cache).
    // So we only ever send a small CORE set plus a find_tools meta-tool; the specialised long tail
    // (rebar, MEP, schedules, sheets, annotation, view creation, family-editor authoring, export,
    // groups, visibility) is loaded on demand — the model calls find_tools, or we pre-load a group
    // when the user's message clearly points at it. Everything stays reachable; it just isn't paid
    // for until needed. This trims the per-request catalogue by roughly two thirds.
    //
    // The curated core set + the search/prewarm scorer live in Services.ToolSearchLogic (a pure,
    // Revit-free class so they can be unit-tested). ToolCatalog is the thin Revit-side adapter.
    public static readonly HashSet<string> CoreToolNames = Services.ToolSearchLogic.CoreToolNames;

    // Custom tools (user's own saved patterns) are always visible — few in number, and they ARE
    // the user's proven work — never hidden behind a search.
    public static bool IsCustom(IRevitTool tool) => tool is DynamicToolProxy;

    public static bool IsCore(IRevitTool tool) =>
        IsCustom(tool) || CoreToolNames.Contains(tool.Name);

    // Groups to pre-load based on the user's message. Only deferred groups are returned.
    public static List<string> PrewarmCategories(string prompt) =>
        Services.ToolSearchLogic.Prewarm(prompt);

    // find_tools backend: adapt the registered tools to plain (name, desc, category, isCore)
    // records and delegate the scoring/reveal to the pure logic.
    public static Services.ToolSearchLogic.SearchResult Search(IEnumerable<IRevitTool> allTools, string query)
    {
        var infos = allTools.Select(t =>
            new Services.ToolSearchLogic.ToolInfo(t.Name, t.Description, CategoryOf(t), IsCore(t)));
        return Services.ToolSearchLogic.Search(infos, query);
    }

    // (category, count) over a set of tools, in display order.
    public static List<(string Category, int Count)> Summarize(IEnumerable<IRevitTool> tools)
    {
        var counts = new Dictionary<string, int>();
        foreach (var t in tools)
        {
            var c = CategoryOf(t);
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }
        var ordered = Order.Where(counts.ContainsKey).Select(c => (c, counts[c]));
        var extra = counts.Keys.Except(Order).OrderBy(c => c).Select(c => (c, counts[c]));
        return ordered.Concat(extra).ToList();
    }
}
