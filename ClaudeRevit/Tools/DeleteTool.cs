using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Removes a custom tool previously created with save_tool: unregisters it and deletes its
// source file. Built-in tools are never touched.
public class DeleteTool : IRevitTool
{
    public string Name => "delete_tool";

    public string Description =>
        "Removes a custom tool previously created with save_tool, or reverts a built-in override back to " +
        "its compiled original (unregisters the override and deletes its file under " +
        "%AppData%\\ClaudeRevit\\tools). A compiled built-in that was never ejected cannot be removed. " +
        "Requires the code-execution opt-in.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "The custom tool's name (or the identifier it was saved under)."
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

        if (!DynamicToolLoader.Delete(name))
            throw new InvalidOperationException(
                $"No custom tool named '{name}' was found. Built-in tools cannot be deleted.");

        return JsonSerializer.Serialize(new { deleted = true, name });
    }
}
