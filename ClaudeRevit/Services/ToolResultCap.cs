using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeRevit.Services;

// Bound the serialized envelope without rewriting failure/preview/commit semantics.
public static class ToolResultCap
{
    public const int MaxChars = 40_000;
    public const int HeadChars = 30_000;
    private static readonly string[] Controls =
    ["ok", "succeeded", "preview", "committed", "rolledBack", "rolled_back", "execution_status",
     "rejected", "idsTemporary", "modelModified", "plan_token", "errorCode", "status"];

    public static string Apply(string result, string toolName)
    {
        if (result.Length <= MaxChars) return result;
        var id = "cap_" + Guid.NewGuid().ToString("N")[..12];
        ToolResultArchive.Record(id, result);
        return Summarize(result, id, toolName, HeadChars);
    }

    public static string Summarize(string result, string id, string toolName, int headChars, bool aged = false)
    {
        var envelope = new JsonObject
        {
            ["ok"] = ToolResult.LooksSuccessful(result), ["truncated"] = true, ["aged"] = aged,
            ["total_chars"] = result.Length, ["full_result_id"] = id,
            ["archive_truncated"] = result.Length > ToolResultArchive.MaxContentChars,
            ["note"] = $"{toolName}: narrow the query, or read get_full_result id='{id}' with offset/limit. Archived results are historical, not live model state."
        };
        try
        {
            if (JsonNode.Parse(ToolResult.Complete(result)) is JsonObject source)
            {
                foreach (var key in Controls)
                    if (source[key] is { } value && value.ToJsonString(ToolResult.Options).Length <= 1_000)
                        envelope[key] = value.DeepClone();
                // An aged summary is replayed on every later request, so its copies stay short; the
                // receipt is already in the head.
                foreach (var key in aged ? new[] { "error", "warnings", "errors" } : new[] { "error", "warnings", "errors", "verification", "plan", "receipt" })
                    if (source[key] is { } value)
                        envelope[key] = Bounded(value, key is "warnings" or "errors", aged ? 300 : 3_000);
            }
        }
        catch (JsonException) { }
        return WithBoundedText(envelope, "head", result, 0, Math.Min(headChars, result.Length));
    }

    private static JsonNode Bounded(JsonNode value, bool array, int limit)
    {
        var raw = value.ToJsonString(ToolResult.Options);
        if (raw.Length <= limit) return value.DeepClone();
        var excerpt = raw[..Math.Min(400, limit / 2)] + "… (see archive)";
        return array ? new JsonArray(new JsonObject { ["code"] = "details_archived", ["message"] = excerpt }) : JsonValue.Create(excerpt)!;
    }

    public static string Page(string id, string content, int offset = 0, int limit = 12_000)
    {
        if (offset < 0 || offset > content.Length || limit < 1 || limit > HeadChars)
            throw new ArgumentOutOfRangeException(nameof(offset), "offset must be within the archived text; limit must be 1..30000.");
        if (offset < content.Length && ((offset > 0 && char.IsLowSurrogate(content[offset]) && char.IsHighSurrogate(content[offset-1])) ||
            (limit == 1 && char.IsHighSurrogate(content[offset]))))
            throw new ArgumentOutOfRangeException(nameof(offset), "Do not split a Unicode surrogate pair; use the returned next_offset and a limit of at least 2.");
        return WithBoundedText(new JsonObject
        {
            // The fields ToolResult.Complete would add are present already, so the dispatcher's
            // completion cannot push a page fitted to the ceiling over it.
            ["ok"] = true, ["id"] = id, ["offset"] = offset, ["total_chars"] = content.Length,
            ["historical"] = true, ["warnings"] = new JsonArray(), ["errors"] = new JsonArray(), ["status"] = "success"
        }, "content", content, offset, Math.Min(limit, content.Length - offset));
    }

    private static string WithBoundedText(JsonObject envelope, string key, string text, int offset, int length)
    {
        while (true)
        {
            if (length > 0 && offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1])) length--;
            envelope[key] = text.Substring(offset, length);
            envelope["shown_chars"] = length;
            if (key == "content")
            {
                envelope["next_offset"] = offset + length < text.Length ? JsonValue.Create(offset + length) : null;
                envelope["has_more"] = offset + length < text.Length;
            }
            var serialized = envelope.ToJsonString(ToolResult.Options);
            if (serialized.Length <= MaxChars) return serialized;
            if (length == 0) throw new InvalidOperationException("Tool control fields exceed the response budget.");
            length /= 2;
        }
    }
}
