using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class ValidateCSharp : IRevitTool
{
    public string Name => "validate_csharp";
    public string Description => "Compile a Revit C# snippet without executing it or opening a transaction. Uses exactly the execute_csharp wrapper/reference set; returns compiler diagnostics before modelling. No code opt-in is required for compilation.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => new ExecuteCSharp().InputSchema;
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        try { new ExecuteCSharp().Preflight(input,app); return Services.Json.Serialize(new { ok=true, compiled=true, executed=false }); }
        catch (Exception ex) { return Services.ToolResult.Failure("compilation",ex.Message); }
    }
}
