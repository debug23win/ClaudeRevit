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
}
