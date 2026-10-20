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

    public sealed record SearchResult(List<string> Categories, string Message)
    {
        public List<string> ToolNames { get; init; } = new();
    }
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    { "create", "creates", "make", "the", "and", "with", "for", "tool", "tools", "сделай", "создай", "нужна", "добавь", "пожалуйста" };
    private static readonly char[] Separators =
        { ' ', ',', '.', ':', ';', '-', '_', '"', '\'', '«', '»', '(', ')', '[', ']', '/', '\\', '?', '!', '\n', '\r', '\t' };
    // Russian builds verbs with prefixes ("образмерь", "замаркируй", "сгруппируй", "заармируй"),
    // so a stem may follow one of them; it still has to sit at the start of the word, which keeps
    // "по сетке осей" from reading as a rebar mesh.
    private static readonly string[] RussianPrefixes =
        { "пере", "разо", "раз", "рас", "обо", "об", "под", "при", "про", "от", "за", "вы", "до", "на", "о", "с", "у" };
    private static bool WordMatches(string word, string key)
    {
        if (word.StartsWith(key, StringComparison.Ordinal)) return true;
        if (key.Length < 3 || key[0] < 'а' || key[0] > 'я') return false;
        foreach (var prefix in RussianPrefixes)
            if (word.StartsWith(prefix, StringComparison.Ordinal) && word.AsSpan(prefix.Length).StartsWith(key, StringComparison.Ordinal))
                return true;
        return false;
    }
    private static bool Matches(string text, string keyword)
    {
        var words = text.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        // Split the key the same way, so "технико-эконом" and "сп 1.13130" can match at all.
        var keys = keyword.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (keys.Length == 0) return false;
        for (var i = 0; i + keys.Length <= words.Length; i++)
            if (keys.Select((k, j) => WordMatches(words[i + j], k)).All(x => x)) return true;
        return false;
    }

    // Core = the tools reached in almost every session. Curated by NAME so it stays tight; a tool
    // omitted here isn't lost, it just costs one find_tools round the first time it's needed.
    public static readonly HashSet<string> CoreToolNames = new(StringComparer.Ordinal)
    {
        "read_attachment",
        // Query / inspection
        "get_selection", "query_elements", "filter_elements", "get_element_parameters", "get_type_parameters",
        "get_element_locations", "get_element_bounding_box", "get_levels", "get_model_statistics",
        "get_project_catalog", "get_project_info", "get_project_standards", "get_shared_parameter_catalog", "get_active_view_info", "list_family_types",
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
        "execute_csharp", "run_dynamo_python", "save_tool",
        "get_tool_source", "get_script_journal", "get_full_result", "save_memory",
        "save_project_memory", "generate_diagnostic_report", "find_tools", "run_batch", "undo_last",
    };

    // Keywords (EN + RU) that pre-load a whole deferred group when they appear in the user's
    // message, so the common cases ("добавь арматуру", "make a section") skip the find_tools round.
    private static readonly (string Category, string[] Keys)[] GroupKeywords =
    {
        ("ModPlus", new[] { "modplus", "mod plus", "модплюс", "мод плюс", "mprsumparameters", "сумма параметров" }),
        ("Rebar", new[] { "rebar", "reinforc", "stirrup", "арматур", "армир", "хомут", "стержн", "арматурная сет", "сетка армат", "fabric reinforcement", "reinforcement mesh", "welded mesh", "wire mesh", "fabric sheet", "fabric area", "сварн сетк", "ведомость детал", "bending" }),
        ("MEP", new[] { "duct", "pipe", "hvac", "воздуховод", "труб", "инженерн" }),
        ("Schedules", new[] { "schedule", "quantit", "специфик", "ведомост", "расписан", "таблиц" }),
        ("Sheets", new[] { "sheet", "titleblock", "viewport", "лист", "штамп", "титул" }),
        ("Export", new[] { "export", "dwg", "pdf", "экспорт", "выгруз" }),
        ("Groups", new[] { "group", "групп" }),
        ("Documentation", new[] { "экспликац", "тэп", "технико-эконом", "отделк", "штамп", "нумерац", "нормоконтрол",
            "норматив", "по нормам", "сп 1.13130", "сп 54", "explication", "compliance", "code check" }),
        ("Coordination", new[] { "clash", "коллизи", "ifc", "nwc", "navisworks", "навис", "dwg", "подложк", "версии модел", "сравни модел", "комплект", "к выдаче", "перед выдачей" }),
        ("Structural analysis", new[] { "analytical", "аналитическ", "нагрузк", "load case", "point load", "line load",
            "area load", "boundary condition", "опорн услов", "закреплени", "сочетани", "combination" }),
        ("Annotation", new[] { "tag", "dimension", "spot ", "revision", "detail line",
            "filled region", "размер", "марк", "аннотац", "выноск", "надпис", "облако" }),
        ("Views", new[] { "section", "elevation", "callout", "camera", "drafting view", "3d view",
            "view template", "crop", "view range", "разрез", "фасад", "камер", "видовой",
            "шаблон вид", "секущ" }),
        ("Family editor", new[] { "family editor", "family parameter", "reference plane",
            "семейств", "опорн плоск", "формул", "parametric profile", "параметрическ" }),
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
            if (keys.Any(k => Matches(p, k))) result.Add(cat);
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
            .Where(t => t.Length >= 3 && !StopWords.Contains(t))
            .Distinct()
            .ToList();

        var deferred = tools.Where(t => !t.IsCore).ToList();
        var exact = deferred.FirstOrDefault(t => string.Equals(t.Name, query?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(exact.Name))
            return new SearchResult(new() { exact.Category }, $"Loaded 1 tool: {exact.Name} — {FirstSentence(exact.Description, 90)}")
            { ToolNames = new() { exact.Name } };

        // Tool names and descriptions are English, so a Russian query used to score zero on every
        // tool and "арматура в плите" answered "nothing found" — the same words that the prewarm
        // table already maps to a group. Matching them here too costs one pass and turns a dead end
        // into the right group.
        var keyedCategories = terms.Count == 0
            ? new List<string>()
            : GroupKeywords
                .Where(g => g.Keys.Any(k => Matches(query ?? "", k)))
                .Select(g => g.Category)
                .ToList();

        int TermScore(ToolInfo t)
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
        // A keyword hit names the group directly, so it outranks an incidental word match in
        // some unrelated tool's description.
        int Score(ToolInfo t) => TermScore(t) + (keyedCategories.Contains(t.Category) ? 4 : 0);

        var scored = deferred.Select(t => (Tool: t, S: Score(t), Terms: TermScore(t)))
            .Where(x => x.S > 0).OrderByDescending(x => x.S).ToList();

        if (scored.Count == 0)
        {
            var groups = string.Join(", ", AllGroups);
            return new SearchResult(new List<string>(),
                $"No specialised tools matched \"{query}\". Available on-demand groups: {groups}. " +
                "Re-search with a word from the group you need (e.g. \"section\", \"tag\", \"schedule\", " +
                "\"rebar\"), or just use execute_csharp if the C# tool is available.");
        }

        // Revealing a group is permanent for the session, so a vague query used to cost four whole
        // groups of schemas on every later request. Only groups that actually match well get in:
        // the best one always, the others if they are at least half as good.
        var best = scored[0].S;
        var cats = keyedCategories
            .Where(c => scored.Any(x => x.Tool.Category == c))
            .Concat(scored.Take(12)
                .GroupBy(x => x.Tool.Category)
                .Select(g => (Category: g.Key, S: g.Max(x => x.S)))
                .OrderByDescending(x => x.S)
                .Where((x, i) => i == 0 || x.S * 2 >= best)
                .Select(x => x.Category))
            .Distinct()
            .Take(3)
            .ToList();
        // A group is a search hint, not permission to append every schema in it: tools that the
        // query's own words pick out are loaded, best first, at most 12. A group named only by a
        // keyword gives no word to rank its tools by (tool descriptions are English, so a Russian
        // request scores them all alike), and the first 12 in registration order would hide the
        // rest of it for good. That group is loaded whole, as it was before ranking existed.
        var keywordOnly = cats
            .Where(c => keyedCategories.Contains(c) && !scored.Any(x => x.Tool.Category == c && x.Terms > 0))
            .ToHashSet();
        var ranked = scored.Where(x => cats.Contains(x.Tool.Category) && !keywordOnly.Contains(x.Tool.Category)).ToList();
        var revealed = ranked.Take(12).Select(x => x.Tool)
            .Concat(deferred.Where(t => keywordOnly.Contains(t.Category)).OrderBy(t => t.Name, StringComparer.Ordinal))
            .ToList();
        var omitted = ranked.Skip(12).Select(x => x.Tool.Name).ToList();

        var sb = new StringBuilder();
        sb.Append("Loaded ").Append(revealed.Count).Append(" tool(s) — now callable directly:\n");
        foreach (var t in revealed)
            sb.Append("• ").Append(t.Name).Append(" — ").Append(FirstSentence(t.Description, 90)).Append('\n');
        if (omitted.Count > 0)
            sb.Append("Also matched but not loaded (find_tools with the exact name loads one): ")
              .Append(string.Join(", ", omitted)).Append('\n');
        return new SearchResult(cats, sb.ToString().TrimEnd()) { ToolNames = revealed.Select(t => t.Name).ToList() };
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
