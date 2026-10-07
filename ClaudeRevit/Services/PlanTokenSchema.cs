using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeRevit.Services;

// Every tool with a preview switch also accepts the plan_token its preview returned (see
// Tools.WritePlans). Added where schemas are published, so no tool has to declare it.
public static class PlanTokenSchema
{
    public static readonly JsonElement Property = JsonSerializer.SerializeToElement(new
    {
        type = "string",
        description = "Optional: the plan_token a preview returned; with preview=false the call applies only if inputs and model are unchanged since that preview, and reports planned vs actual changes."
    });

    public static bool Applies(IReadOnlyDictionary<string, JsonElement>? properties) =>
        properties != null && properties.ContainsKey("preview") && !properties.ContainsKey("plan_token");

    public static void AddTo(JsonObject properties)
    {
        if (properties.ContainsKey("preview") && !properties.ContainsKey("plan_token")) properties["plan_token"] = JsonNode.Parse(Property.GetRawText());
    }
}
