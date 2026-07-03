using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

public class GetScriptJournal : IRevitTool
{
    public string Name => "get_script_journal";

    public string Description =>
        "Returns the learning journal of past execute_csharp / run_dynamo_python calls in " +
        "this environment: the code, whether it succeeded, and the exact MODEL DELTA it " +
        "produced (elements added/modified/deleted by category). Check it before writing a " +
        "new script — reuse a snippet that is already proven to work here instead of " +
        "reinventing it, and learn which API calls actually behave in this Revit/Dynamo " +
        "version. Recurring patterns here are also candidates for new dedicated tools.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["limit"] = JsonSerializer.SerializeToElement(new
            {
                type = "integer",
                minimum = 1,
                maximum = 50,
                description = "How many most-recent entries to return (default 10)."
            })
        },
        Required = []
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var limit = ToolInput.OptionalInt(input, "limit") ?? 10;
        limit = Math.Clamp(limit, 1, 50);

        var entries = ScriptJournal.ReadRecent(limit);
        return JsonSerializer.Serialize(new { count = entries.Count, entries });
    }
}
