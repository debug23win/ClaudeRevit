using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// Lets the model driving Revit over MCP say which model it is.
//
// This exists because the protocol has no such field: initialize carries clientInfo (the CLIENT's
// name and version) and nothing about the model behind it. So the handshake instructions ask for
// one call to this tool at the start of a session, and the answer is shown next to the client name
// in Settings and recorded with benchmark runs.
//
// The answer is the model's own claim, so it is always displayed as self-reported — a model can be
// vague or wrong about its identity, and a client may never call this at all.
public class ReportDrivingModel : IRevitTool
{
    public string Name => "report_driving_model";

    public string Description =>
        "Report which model you are, once per session. The MCP protocol doesn't tell this add-in " +
        "which model is driving it, so this is the only way the user can see who did the work — it " +
        "is shown in Settings and recorded with benchmark runs. Call it once at the start of a " +
        "session, before other work, and AGAIN whenever a tool result asks you to — the user can " +
        "reset the session from the add-in, and a stale answer then names a model that is no longer " +
        "driving. Give the specific model id you know yourself to be (e.g. 'claude-opus-5', " +
        "'gpt-6-astra'), not a family name. Changes nothing in the model.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["model"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Your model identifier, as specific as you know it."
            })
        },
        Required = ["model"]
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var model = input.TryGetValue("model", out var m) ? m.GetString() : null;
        McpSession.ReportModel(model);

        return Json.Serialize(new
        {
            recorded = McpSession.ReportedModel,
            client = McpSession.ClientName,
            note = "Recorded as self-reported. Continue with the user's request."
        });
    }
}
