using System.Collections.Generic;
using System.Text.Json;

namespace ClaudeRevit.Tools;

// Optional-field readers for tool inputs. LLMs routinely send explicit nulls for optional
// fields they don't use ("top_mm": null); JsonElement.GetDouble()/GetInt32() throw on a
// Null-kind element, so absence and null must be treated the same.
internal static class ToolInput
{
    public static double? OptionalDouble(IReadOnlyDictionary<string, JsonElement> input, string name) =>
        input.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static int? OptionalInt(IReadOnlyDictionary<string, JsonElement> input, string name) =>
        input.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    // Boolean flags: LLMs also emit "true"/1 — honor the intent instead of silently
    // treating anything but a JSON true as false.
    public static bool Flag(IReadOnlyDictionary<string, JsonElement> input, string name)
    {
        if (!input.TryGetValue(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => string.Equals(v.GetString(), "true", System.StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number => v.TryGetInt32(out var n) && n != 0,
            _ => false
        };
    }
}
