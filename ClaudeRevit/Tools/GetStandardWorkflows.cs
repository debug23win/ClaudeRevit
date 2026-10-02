using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class GetStandardWorkflows : IRevitTool
{
    public string Name => "get_standard_workflows";
    public string Description => "Get practical verified BIMStarter, ADSK or Samolet EIR v5.0 workflows with source pages, applicable stage, tool sequence and limitations. Samolet is opt-in, not a global modelling restriction. Its project FOP, UPM/PIM/naming appendices are required for a complete check; EIR alone contains no verified parameter GUID catalog.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["profile"] = NativeToolUtil.Field("string", "Optional BIMStarter, ADSK or Samolet."),
        ["query"] = NativeToolUtil.Field("string", "Optional substring in rule/topic/text."),
        ["offset"] = NativeToolUtil.Field("integer", "Default 0."),
        ["limit"] = NativeToolUtil.Field("integer", "Default 20; 1..100.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var profile = NativeToolUtil.Text(input, "profile"); var query = NativeToolUtil.Text(input, "query");
        var offset = Math.Max(0, ToolInput.OptionalInt(input, "offset") ?? 0);
        var limit = Math.Clamp(ToolInput.OptionalInt(input, "limit") ?? 20, 1, 100);
        var data = Services.StandardWorkflows.Data;
        var rows = data.GetProperty("rules").EnumerateArray().Where(r =>
            (profile.Length == 0 || r.GetProperty("profile").GetString()!.Contains(profile, StringComparison.OrdinalIgnoreCase)) &&
            (query.Length == 0 || r.GetRawText().Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        return Services.Json.Serialize(new { sources = data.GetProperty("sources"), limitations = data.GetProperty("limitations"), total = rows.Length, offset,
            rules = rows.Skip(offset).Take(limit).ToArray(), next_offset = offset + limit < rows.Length ? (int?)(offset + limit) : null });
    }
}
