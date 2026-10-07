using System;
using System.Text.Json.Nodes;

namespace ClaudeRevit.Services;

// A hard ceiling on one tool result. Some Get*/List* tools have no limit of their own, and a
// single call on a large model could return hundreds of thousands of characters — tens of
// thousands of tokens in the very round it happens, before aging or compaction can help, and
// again on every later request. Over the cap, the full result is archived (get_full_result
// brings it back) and the model gets the head plus a note telling it to narrow the query.
public static class ToolResultCap
{
    public const int MaxChars = 40_000;
    public const int HeadChars = 30_000;

    public static string Apply(string result, string toolName)
    {
        if (result.Length <= MaxChars) return result;
        var id = "cap_" + Guid.NewGuid().ToString("N")[..12];
        ToolResultArchive.Record(id, result);
        return new JsonObject
        {
            ["ok"] = true,
            ["truncated"] = true,
            ["total_chars"] = result.Length,
            ["shown_chars"] = HeadChars,
            ["full_result_id"] = id,
            ["note"] = $"{toolName} returned {result.Length:N0} characters, more than one result may carry. " +
                       "The start is shown below. Narrow the request (filters, level, category, limit) " +
                       $"rather than paging through it; get_full_result with id '{id}' returns the archived " +
                       "original if you really need it.",
            ["head"] = result[..HeadChars],
            ["warnings"] = new JsonArray(), ["errors"] = new JsonArray()
        }.ToJsonString(ToolResult.Options);
    }
}
