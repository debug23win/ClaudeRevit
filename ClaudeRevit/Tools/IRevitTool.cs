using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public interface IRevitTool
{
    string Name { get; }
    string Description { get; }
    InputSchema InputSchema { get; }
    bool RequiresTransaction { get; }

    // When true, the chat pane shows an Allow/Deny prompt (with the tool's input)
    // before the call runs. Use for destructive or arbitrary-code operations.
    bool RequiresConfirmation => false;

    string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app);
}
