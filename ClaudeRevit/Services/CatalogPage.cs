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
        var selected = new JsonObject();
        foreach (var (key, value) in source)
            if (sections == null || sections.Contains(key, StringComparer.Ordinal)) selected[key] = Visit(value, key);
        if (sections?.Any(key => !source.ContainsKey(key)) == true) throw new ArgumentException("Unknown catalog section.", nameof(sections));
        return new JsonObject { ["catalog"] = selected, ["paging"] = paging };
    }
}
