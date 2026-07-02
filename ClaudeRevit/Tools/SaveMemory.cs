using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

public class SaveMemory : IRevitTool
{
    public string Name => "save_memory";

    public string Description =>
        "Saves a durable note that you will see in every future conversation (persisted across " +
        "Revit restarts). Use when the user states a lasting preference or project standard " +
        "(units, default types, naming, floor height) or corrects you in a way worth remembering. " +
        "Write one concise fact per call. Do NOT save transient details of the current task.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["note"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "A single concise fact or preference to remember."
            })
        },
        Required = ["note"]
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var note = input["note"].GetString();
        if (string.IsNullOrWhiteSpace(note))
            throw new InvalidOperationException("note is empty.");
        MemoryStore.Append(note);
        return JsonSerializer.Serialize(new { saved = true, note });
    }
}
