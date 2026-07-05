using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Self-extension: turns a proven pattern into a persistent, first-class tool. Claude writes
// a complete C# source file implementing IRevitTool; it is compiled and validated on the
// spot, saved under %AppData%\ClaudeRevit\tools\, and becomes available as a normal tool on
// the next message and in every future session. Gated by the code-execution opt-in.
public class SaveTool : IRevitTool
{
    public string Name => "save_tool";

    public string Description =>
        "Creates or updates a PERSISTENT custom tool from C# source, so a proven pattern becomes a " +
        "first-class tool you can call by name in this and all future sessions (no add-in rebuild). The " +
        "source must be a complete C# file defining a public class implementing " +
        "ClaudeRevit.Tools.IRevitTool. It is compiled and validated immediately; on failure nothing is " +
        "installed and the compiler errors are returned. Requires the code-execution opt-in. TEMPLATE:\n" +
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
        "      var doc = app.ActiveUIDocument.Document;\n" +
        "      var lvl = doc.GetElement(new ElementId(input[\"id\"].GetInt64())) as Level;\n" +
        "      lvl.Name = input[\"name\"].GetString();\n" +
        "      return JsonSerializer.Serialize(new { ok = true, renamed = lvl.Name });\n" +
        "    } } }\n" +
        "```\n" +
        "Set RequiresTransaction => true when the tool modifies the model (the host wraps Execute in a " +
        "managed transaction that rolls back on error and groups into one undo). Return a short JSON " +
        "string. Prefer a dedicated built-in tool when one already exists; use this to capture recurring " +
        "execute_csharp work.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "A short identifier for the tool file (usually the tool's Name)."
            }),
            ["source"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "The complete C# source file implementing ClaudeRevit.Tools.IRevitTool."
            })
        },
        Required = ["name", "source"]
    };

    public bool RequiresTransaction => false;
    public bool RequiresConfirmation => true;
    public bool RequiresCodeExecutionOptIn => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var name = input["name"].GetString();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("name is empty.");
        var source = input["source"].GetString();
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("source is empty.");

        var result = DynamicToolLoader.SaveAndLoad(name, source);
        return JsonSerializer.Serialize(new
        {
            saved = true,
            file = result.File,
            tools = result.ToolNames,
            note = "Available on your next message. Call it by name like any built-in tool."
        });
    }
}
