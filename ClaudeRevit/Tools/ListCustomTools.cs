using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Lists the custom tools created with save_tool (name + description), so you can tell your
// own learned tools apart from the built-ins and pick one to refine with get_tool_source.
public class ListCustomTools : IRevitTool
{
    public string Name => "list_custom_tools";

    public string Description =>
        "Lists the custom tools you have created with save_tool (name and description). Use it to see what " +
        "you already built before adding a new one, and to pick a tool to refine (get_tool_source + " +
        "save_tool). Requires the code-execution opt-in.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>(),
        Required = []
    };

    public bool RequiresTransaction => false;
    public bool RequiresCodeExecutionOptIn => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var list = new List<object>();
        foreach (var (name, _) in DynamicToolLoader.ListCustom())
        {
            var tool = ToolRegistry.Instance.Get(name);
            list.Add(new { name, description = tool?.Description });
        }
        return Services.Json.Serialize(new { count = list.Count, tools = list });
    }
}
