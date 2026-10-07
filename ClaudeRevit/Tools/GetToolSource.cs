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
        "Returns the C# source of ANY tool by name — a custom tool created with save_tool, or a built-in " +
        "(its original source is embedded in the add-in). Use it to study a tool, or to refine one: read " +
        "the source, change it, then save_tool with the SAME name. For a built-in, eject_tool first (or " +
        "just save_tool the same name) to install an editable override; delete_tool reverts to the " +
        "compiled original. name=\"template\" returns a working template for a new tool. Follows the code-execution switch in Settings.";

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

    // The save_tool template, served on demand instead of riding in save_tool's description on
    // every request.
    internal const string Template =
        "```\n" +
        "using System; using System.Collections.Generic; using System.Text.Json;\n" +
        "using Anthropic.Models.Beta.Messages; using Autodesk.Revit.DB; using Autodesk.Revit.UI;\n" +
        "namespace ClaudeRevit.Dynamic {\n" +
        "  public class RenameLevel : ClaudeRevit.Tools.IRevitTool {\n" +
        "    public string Name => \"rename_level\";\n" +
        "    public string Description => \"Renames a level by id.\";\n" +
        "    public InputSchema InputSchema => new() { Properties = new Dictionary<string, JsonElement> {\n" +
        "      [\"id\"] = JsonSerializer.SerializeToElement(new { type = \"integer\" }),\n" +
        "      [\"name\"] = JsonSerializer.SerializeToElement(new { type = \"string\" }) }, Required = new[]{\"id\",\"name\"} };\n" +
        "    public bool RequiresTransaction => true;   // dispatcher wraps Execute in a transaction\n" +
        "    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app) {\n" +
        "      var doc = ToolContext.UiDocument(app).Document;\n" +
        "      var lvl = doc.GetElement(new ElementId(input[\"id\"].GetInt64())) as Level;\n" +
        "      lvl.Name = input[\"name\"].GetString();\n" +
        "      return Services.Json.Serialize(new { ok = true, renamed = lvl.Name });\n" +
        "    } } }\n" +
        "```\n";

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var name = ToolInput.RequiredText(input, "name");
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("name is empty.");

        if (name == "template") return Services.Json.Serialize(new { name, source = Template.Replace("```\n", "").Replace("```", "").Trim() });
        var source = DynamicToolLoader.GetSource(name)
            ?? throw new InvalidOperationException(
                $"No custom tool source found for '{name}'. It may be a built-in tool (no editable source), " +
                "or the name is wrong — call list_custom_tools to see the custom ones.");

        return Services.Json.Serialize(new { name, source });
    }
}
