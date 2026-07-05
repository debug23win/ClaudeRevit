using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// The retrieval half of tool-result aging (see ToolResultAging): older tool outputs are
// truncated in the conversation to save tokens; the truncation marker carries the id
// this tool takes to return the archived original.
public class GetFullResult : IRevitTool
{
    public string Name => "get_full_result";

    public string Description =>
        "Returns the archived FULL text of an aged tool result. Older tool outputs are " +
        "truncated in the conversation to save tokens; the truncation marker contains the " +
        "id to pass here. Use it only when the preview is insufficient AND the data cannot " +
        "have changed — when elements may have been modified since, re-query the live model " +
        "instead (query_elements etc.).";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["id"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "The id from the aged-result marker (a tool_use id)."
            })
        },
        Required = ["id"]
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var id = input.TryGetValue("id", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Pass the id from the aged-result marker.");

        var content = ToolResultArchive.Lookup(id!)
            ?? throw new InvalidOperationException(
                $"No archived result with id '{id}'. The archive keeps roughly the last 300 " +
                "large results — re-run the original query instead.");

        // The model asked for the full text explicitly, but still bound the response.
        return TextUtil.Truncate(content, 50_000);
    }
}
