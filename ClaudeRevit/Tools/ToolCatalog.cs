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
