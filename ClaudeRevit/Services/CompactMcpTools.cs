using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeRevit.Services;

// A fixed small surface for pane and benchmark CLI turns. Other tools stay reachable through
// schema discovery + invocation; external MCP clients retain the full native catalogue.
public static class CompactMcpTools
{
    public static readonly HashSet<string> DirectNames = new(StringComparer.Ordinal)
    {
        "query_elements", "get_levels", "get_element_parameters", "get_element_locations",
        "get_element_bounding_box", "create_level", "create_grid", "create_wall", "create_floor",
        "create_material", "set_parameter", "rename_element", "run_batch"
    };

    public static List<string> Search(IEnumerable<ToolSearchLogic.ToolInfo> tools, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();
        var terms = query.ToLowerInvariant().Split(new[] { ' ', '_', '-', ',', '/', '.', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var groups = ToolSearchLogic.Prewarm(query);
        return tools.Select(t =>
        {
            var name = t.Name.ToLowerInvariant();
            var description = t.Description.ToLowerInvariant();
            var category = t.Category.ToLowerInvariant();
            var score = string.Equals(t.Name, query.Trim(), StringComparison.OrdinalIgnoreCase) ? 1000 : 0;
            score += terms.Sum(term => (name.Contains(term) ? 6 : 0) + (category.Contains(term) ? 3 : 0) + (description.Contains(term) ? 1 : 0));
            if (groups.Contains(t.Category)) score += 12;
            return (t.Name, Score: score);
        }).Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Name, StringComparer.Ordinal)
          .Select(x => x.Name).ToList();
    }
}
