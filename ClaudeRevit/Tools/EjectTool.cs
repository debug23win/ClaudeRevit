using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// Makes a built-in tool editable on the fly: copies its source to an override file under
// %AppData%\ClaudeRevit\tools that shadows the compiled version. After ejecting, edit it with
// save_tool (same name) and revert with delete_tool (which restores the compiled built-in).
public class EjectTool : IRevitTool
{
    public string Name => "eject_tool";

    public string Description =>
        "Makes a BUILT-IN tool editable: copies its source to an override file that shadows the compiled " +
        "version, so you can then change its behaviour with save_tool (same name) and revert with " +
        "delete_tool (restores the original). Use get_tool_source first to see the code. Requires the " +
        "code-execution opt-in.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "The tool's name (built-in or custom)."
            })
        },
        Required = ["name"]
    };

    public bool RequiresTransaction => false;
    public bool RequiresConfirmation => true;
    public bool RequiresCodeExecutionOptIn => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var name = input["name"].GetString();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("name is empty.");

        var result = DynamicToolLoader.Eject(name);
        return Json.Serialize(new
        {
            ejected = true,
            name,
            file = result.File,
            note = "Now editable — change it with save_tool (same name), or revert with delete_tool."
        });
    }
}
