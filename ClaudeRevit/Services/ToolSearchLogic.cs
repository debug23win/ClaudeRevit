using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClaudeRevit.Services;

// The pure, Revit-free core of progressive tool loading: the curated core-tool set, the
// group-preload keywords, and the find_tools scorer. Kept free of any Autodesk.Revit / Anthropic
// type so it can be unit-tested on any OS (ToolCatalog is the thin Revit-side wrapper over it).
public static class ToolSearchLogic
{
    // A tool reduced to what the search needs — decoupled from IRevitTool.
    public readonly record struct ToolInfo(string Name, string Description, string Category, bool IsCore);

    public sealed record SearchResult(List<string> Categories, string Message);

    // Core = the tools reached in almost every session. Curated by NAME so it stays tight; a tool
    // omitted here isn't lost, it just costs one find_tools round the first time it's needed.
    public static readonly HashSet<string> CoreToolNames = new(StringComparer.Ordinal)
    {
        // Query / inspection
        "get_selection", "query_elements", "filter_elements", "get_element_parameters", "get_type_parameters",
        "get_element_locations", "get_element_bounding_box", "get_levels", "get_model_statistics",
        "get_project_catalog", "get_project_info", "get_active_view_info", "list_family_types",
        "list_loaded_families", "list_materials", "measure_distance", "analyze_warnings",
        // Core modelling
        "create_wall", "create_wall_type", "create_floor", "create_floor_type", "create_roof",
        "create_level", "create_grid", "create_structural_column", "create_beam", "create_room",
        "place_family_instance", "place_door", "place_window", "create_material",
        "set_element_material", "create_direct_shape", "clone_element_geometry", "set_parameter",
        "set_type_parameter", "change_element_type", "move_elements", "copy_elements",
        "rotate_elements", "mirror_elements", "array_elements", "delete_elements", "rename_element",
        "join_geometry", "duplicate_family_type", "load_family",
        // Code / learning / self-extension
        "execute_csharp", "run_dynamo_python", "save_tool", "delete_tool", "list_custom_tools",
        "get_tool_source", "get_script_journal", "get_full_result", "save_memory",
        "save_project_memory", "generate_diagnostic_report", "find_tools", "run_batch",
    };

    // Keywords (EN + RU) that pre-load a whole deferred group when they appear in the user's
    // message, so the common cases ("добавь арматуру", "make a section") skip the find_tools round.
    private static readonly (string Category, string[] Keys)[] GroupKeywords =
    {
        ("Rebar", new[] { "rebar", "reinforc", "stirrup", "арматур", "армир", "хомут", "стержн" }),
        ("MEP", new[] { "duct", "pipe", "hvac", "воздуховод", "труб", "инженерн" }),
        ("Schedules", new[] { "schedule", "quantit", "специфик", "ведомост", "расписан", "таблиц" }),
        ("Sheets", new[] { "sheet", "titleblock", "viewport", "лист", "штамп", "титул" }),
        ("Export", new[] { "export", "dwg", "pdf", "экспорт", "выгруз" }),
        ("Groups", new[] { "group", "групп" }),
        ("Annotation", new[] { "tag", "dimension", "spot ", "revision", "detail line",
            "filled region", "размер", "марк", "аннотац", "выноск", "надпис", "облако" }),
        ("Views", new[] { "section", "elevation", "callout", "camera", "drafting view", "3d view",
            "view template", "crop", "view range", "разрез", "фасад", "камер", "видовой",
            "шаблон вид", "секущ" }),
        ("Family editor", new[] { "family editor", "family parameter", "reference plane",
            "семейств", "опорн плоск", "формул" }),
        ("Visibility", new[] { "isolate", "override", "hide categ", "graphic", "изолир",
            "переопредел", "фильтр вид" }),
    };

    public static IEnumerable<string> AllGroups => GroupKeywords.Select(g => g.Category);

    // Groups to pre-load based on the user's message. Only deferred groups are returned.
    public static List<string> Prewarm(string prompt)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(prompt)) return result;
        var p = prompt.ToLowerInvariant();
        foreach (var (cat, keys) in GroupKeywords)
            if (keys.Any(k => p.Contains(k))) result.Add(cat);
        return result;
    }

    // Score the deferred tools against the query, reveal the GROUPS the best matches belong to
    // (related tools travel together), and return a compact listing of what is now callable.
    public static SearchResult Search(IEnumerable<ToolInfo> tools, string query)
    {
        var terms = (query ?? "")
            .Split(new[] { ' ', ',', '.', '"', '\'', '(', ')', '/', '-', ':', ';', '\n', '\t' },
                   StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length >= 3)
            .Distinct()
            .ToList();

        var deferred = tools.Where(t => !t.IsCore).ToList();

        int Score(ToolInfo t)
        {
            var name = t.Name.ToLowerInvariant();
            var desc = (t.Description ?? "").ToLowerInvariant();
            var cat = t.Category.ToLowerInvariant();
            int s = 0;
            foreach (var term in terms)
            {
                if (name.Contains(term)) s += 3;
                if (cat.Contains(term)) s += 2;
                if (desc.Contains(term)) s += 1;
            }
            return s;
        }

        var scored = deferred.Select(t => (Tool: t, S: Score(t)))
            .Where(x => x.S > 0).OrderByDescending(x => x.S).ToList();

        if (scored.Count == 0)
        {
            var groups = string.Join(", ", AllGroups);
            return new SearchResult(new List<string>(),
                $"No specialised tools matched \"{query}\". Available on-demand groups: {groups}. " +
                "Re-search with a word from the group you need (e.g. \"section\", \"tag\", \"schedule\", " +
                "\"rebar\"), or just use execute_csharp if code execution is enabled.");
        }

        var cats = scored.Take(12).Select(x => x.Tool.Category).Distinct().Take(4).ToList();
        var revealed = deferred.Where(t => cats.Contains(t.Category))
            .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

        var sb = new StringBuilder();
        sb.Append("Loaded ").Append(revealed.Count).Append(" tool(s) — now callable directly:\n");
        foreach (var t in revealed)
            sb.Append("• ").Append(t.Name).Append(" — ").Append(FirstSentence(t.Description, 90)).Append('\n');
        return new SearchResult(cats, sb.ToString().TrimEnd());
    }

    private static string FirstSentence(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Trim();
        var dot = t.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 0 && dot < max) return t.Substring(0, dot);
        return t.Length <= max ? t : t.Substring(0, max) + "…";
    }
}
