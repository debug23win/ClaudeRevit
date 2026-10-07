using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class PlanTokenSchemaTests
{
    [Fact]
    public void OnlyPreviewToolsGetThePlanToken()
    {
        var preview = new JsonObject { ["preview"] = new JsonObject { ["type"] = "boolean" } };
        PlanTokenSchema.AddTo(preview);
        Assert.True(preview.ContainsKey("plan_token"));
        var plain = new JsonObject { ["count"] = new JsonObject { ["type"] = "integer" } };
        PlanTokenSchema.AddTo(plain);
        Assert.False(plain.ContainsKey("plan_token"));
        Assert.False(PlanTokenSchema.Applies(new Dictionary<string, JsonElement> { ["preview"] = default, ["plan_token"] = default }));
    }
}
