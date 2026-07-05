using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Returns the C# source of a custom tool created with save_tool, so it can be READ and then
// refined: edit the source and call save_tool with the same name to overwrite it. This is
// how you fix or extend an existing tool instead of rebuilding it from scratch.
public class GetToolSource : IRevitTool
{
    public string Name => "get_tool_source";

    public string Description =>
        "Returns the C# source of a custom tool previously created with save_tool. Use it to refine an " +
        "existing tool: read the source, change it, then call save_tool with the SAME name to overwrite " +
        "and reload it. Only custom tools have source (built-in tools do not). Requires the code-execution " +
        "opt-in.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "The custom tool's name."
            })
        },
        Required = ["name"]
    };

    public bool RequiresTransaction => false;
    public bool RequiresCodeExecutionOptIn => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var name = input["name"].GetString();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("name is empty.");

        var source = DynamicToolLoader.GetSource(name)
            ?? throw new InvalidOperationException(
                $"No custom tool source found for '{name}'. It may be a built-in tool (no editable source), " +
                "or the name is wrong — call list_custom_tools to see the custom ones.");

        return Services.Json.Serialize(new { name, source });
    }
}
