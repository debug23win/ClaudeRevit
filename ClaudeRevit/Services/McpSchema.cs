using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace ClaudeRevit.Services;

// Tool-schema shaping for the MCP server. Pure JSON manipulation with no Revit dependency, so it
// lives apart from McpServer and is unit-tested.
//
// Why it exists: Anthropic's API accepts full JSON Schema, but OpenAI's function-calling validator
// does not. It rejects the numeric/array constraint keywords this add-in uses heavily (minimum and
// maximum on dozens of parameters, minItems on dozens more) and accepts anyOf but not oneOf. A tool
// list carrying them is refused wholesale — so an OpenAI-backed client would see NONE of the tools,
// not merely a degraded schema. Hence a portable subset for non-Claude clients.
public static class McpSchema
{
    // Decide from the MCP client's advertised name (initialize -> clientInfo.name) whether it needs
    // the portable subset. Unknown clients get the portable form: the fail-safe direction, since the
    // cost is a little less validation rather than an empty tool list.
    public static bool NeedsPortableSchemas(string? clientName)
    {
        var n = (clientName ?? "").ToLowerInvariant();
        if (n.Length == 0) return false;   // pre-initialize probes: assume the native client
        return !(n.Contains("claude") || n.Contains("anthropic"));
    }

    // Keywords OpenAI's function schema validator rejects. Dropping them silently would lose real
    // guidance ("1-500", "at least one id"), so each constraint is folded into the description
    // instead — the model still learns the bound, just as prose rather than as schema.
    private static readonly (string Key, string Label)[] DroppedConstraints =
    {
        ("minimum", "min"), ("maximum", "max"),
        ("exclusiveMinimum", "exclusive min"), ("exclusiveMaximum", "exclusive max"),
        ("minItems", "min items"), ("maxItems", "max items"),
        ("minLength", "min length"), ("maxLength", "max length"),
        ("multipleOf", "multiple of"), ("pattern", "pattern")
    };

    public static void MakePortable(JsonObject node)
    {
        var notes = new List<string>();
        foreach (var (key, label) in DroppedConstraints)
        {
            if (!node.TryGetPropertyValue(key, out var v) || v == null) continue;
            notes.Add($"{label} {v.ToJsonString().Trim('"')}");
            node.Remove(key);
        }
        if (notes.Count > 0)
        {
            var existing = node["description"]?.GetValue<string>() ?? "";
            var suffix = "(" + string.Join(", ", notes) + ")";
            node["description"] = existing.Length == 0 ? suffix : existing.TrimEnd() + " " + suffix;
        }

        // oneOf has no OpenAI equivalent; anyOf does, and is close enough here — our uses are
        // "string or number" branches that are mutually exclusive anyway.
        if (node.TryGetPropertyValue("oneOf", out var oneOf) && oneOf != null)
        {
            node.Remove("oneOf");
            node["anyOf"] = oneOf.DeepClone();
        }

        // Advisory, and not accepted in strict function schemas.
        node.Remove("default");

        // Strict mode requires objects to forbid extra properties.
        if (node["type"]?.GetValue<string>() == "object" && node["additionalProperties"] == null)
            node["additionalProperties"] = false;

        // Recurse everywhere a sub-schema can live.
        if (node["properties"] is JsonObject props)
            foreach (var kv in props.ToList())
                if (kv.Value is JsonObject child) MakePortable(child);

        if (node["items"] is JsonObject items) MakePortable(items);

        foreach (var branchKey in new[] { "anyOf", "allOf" })
            if (node[branchKey] is JsonArray branches)
                foreach (var b in branches)
                    if (b is JsonObject bo) MakePortable(bo);
    }
}
