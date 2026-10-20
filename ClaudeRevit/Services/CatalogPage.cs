using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeRevit.Services;

// Preserve catalog field shapes, while exposing real counts and continuation offsets
// for every bounded list. The cached source itself is never cut down to a preview.
public static class CatalogPage
{
    public static JsonObject Select(JsonElement catalog, string[]? sections, string? query, int offset, int limit, string? familyCategory = null)
    {
        if (offset < 0 || limit is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(limit));
        var source = JsonNode.Parse(catalog.GetRawText())!.AsObject();
        var paging = new JsonObject();
        JsonNode? Visit(JsonNode? node, string path)
        {
            if (node is JsonArray array)
            {
                var filtered = array.Where(item => string.IsNullOrWhiteSpace(query) ||
                    (item?.ToJsonString(ToolResult.Options).Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)).ToArray();
                var items = filtered.Skip(offset).Take(limit).ToArray();
                paging[path] = new JsonObject { ["total"] = filtered.Length, ["offset"] = offset,
                    ["next_offset"] = offset + items.Length < filtered.Length ? JsonValue.Create(offset + items.Length) : null };
                return new JsonArray(items.Select(n => n?.DeepClone()).ToArray());
            }
            if (node is JsonObject obj)
            {
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                    if (path != "family_types_by_category" || string.IsNullOrWhiteSpace(familyCategory) || key.Contains(familyCategory, StringComparison.OrdinalIgnoreCase))
                        result[key] = Visit(value, path + "." + key);
                return result;
            }
            return node?.DeepClone();
        }
        if (sections?.Any(key => !source.ContainsKey(key)) == true) throw new ArgumentException("Unknown catalog section.", nameof(sections));
        // A call with no narrowing is the usual first call. Listing every loadable family type of
        // every category would overflow the response, so it gets counts per category instead.
        var overview = sections == null && string.IsNullOrWhiteSpace(familyCategory) && string.IsNullOrWhiteSpace(query);
        var selected = new JsonObject();
        foreach (var (key, value) in source)
        {
            if (sections != null && !sections.Contains(key, StringComparer.Ordinal)) continue;
            if (overview && key == "family_types_by_category" && value is JsonObject byCategory)
                selected["family_type_counts_by_category"] = new JsonObject(byCategory.Select(c =>
                    KeyValuePair.Create(c.Key, (JsonNode?)JsonValue.Create(c.Value is JsonArray list ? list.Count : 0))));
            else selected[key] = Visit(value, key);
        }
        // One offset applies to every returned list, so continuing one list would silently skip
        // the start of all the others.
        if (offset > 0 && paging.Count > 1)
            throw new ArgumentException("offset continues a single list: pass sections (and family_category for family types) " +
                "so that only the list being continued is returned.", nameof(offset));
        var page = new JsonObject { ["paging"] = paging, ["catalog"] = selected };
        if (overview)
            page["note"] = "Family type names are listed when sections includes family_types_by_category or family_category is given.";
        return page;
    }

    // Halve the per-list limit until the response fits the result ceiling, so the paging block
    // and every section survive instead of being cut by the generic cap.
    public static JsonObject SelectFitting(JsonElement catalog, string[]? sections, string? query, int offset, int limit, string? familyCategory, int maxChars)
    {
        var page = Select(catalog, sections, query, offset, limit, familyCategory);
        while (limit > 1 && page.ToJsonString(ToolResult.Options).Length > maxChars)
        {
            limit = Math.Max(1, limit / 2);
            page = Select(catalog, sections, query, offset, limit, familyCategory);
            page["limit_reduced_to"] = limit;
        }
        return page;
    }
}
