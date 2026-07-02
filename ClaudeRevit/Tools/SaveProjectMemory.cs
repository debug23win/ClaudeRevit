using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

public class SaveProjectMemory : IRevitTool
{
    public string Name => "save_project_memory";

    public string Description =>
        "Saves a durable note tied to THIS document (by its title) — you will see it in every " +
        "future session with this project, in the CURRENT DOCUMENT context. Use for " +
        "project-specific standards: default rebar cover type, preferred stirrup/bar shapes, " +
        "the view template used for reinforcement views, naming conventions. For preferences " +
        "that apply to ALL projects use save_memory instead. One concise fact per call.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["note"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "A single concise project-specific fact or default to remember."
            })
        },
        Required = ["note"]
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var note = input["note"].GetString();
        if (string.IsNullOrWhiteSpace(note))
            throw new InvalidOperationException("note is empty.");

        MemoryStore.AppendProject(doc.Title, doc.PathName, note);
        return JsonSerializer.Serialize(new { saved = true, project = doc.Title, note });
    }
}
