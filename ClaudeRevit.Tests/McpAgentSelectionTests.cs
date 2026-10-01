using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class McpAgentSelectionTests
{
    [Fact]
    public void PreviousMessagesKeepTheirAgentWhenThePickerChanges()
    {
        var original = ClaudeRevit.UI.ChatMessage.AssistantLabel;
        try
        {
            ClaudeRevit.UI.ChatMessage.AssistantLabel = "Codex";
            var codexReply = new ClaudeRevit.UI.ChatMessage { Role = "assistant" };
            ClaudeRevit.UI.ChatMessage.AssistantLabel = "Claude Code";
            Assert.Equal("Codex", codexReply.RoleDisplay);
            Assert.Equal("Claude Code", new ClaudeRevit.UI.ChatMessage { Role = "assistant" }.RoleDisplay);
        }
        finally { ClaudeRevit.UI.ChatMessage.AssistantLabel = original; }
    }
    [Theory]
    [InlineData("codex")]
    [InlineData("codex:gpt-test")]
    public void CodexIsNotInterceptedByTheLegacySubscriptionToggle(string tag)
        => Assert.Equal("codex", McpAgentSelection.Resolve(tag, true, null)!.Agent);

    [Fact]
    public void ExplicitPaneSelectionWinsOverLegacyModelAndSubscription()
    {
        var selection = new McpAgentSelection("codex", "gpt-test", "high");
        Assert.Same(selection, McpAgentSelection.Resolve("sonnet-5", true, selection));
        Assert.Null(McpAgentSelection.Resolve("sonnet-5", false, null));
        Assert.Equal("claudecode", McpAgentSelection.Resolve("sonnet-5", true, null)!.Agent);
    }

    [Theory]
    [InlineData("codex", "gpt-test", "gpt-test")]
    [InlineData("claudecode", "gpt-test", null)]
    [InlineData("codex", "claude-opus-test", null)]
    [InlineData("claudecode", "claude-opus-test", "claude-opus-test")]
    public void OldSharedOverrideMigratesOnlyToItsAgent(string agent, string legacy, string? expected)
        => Assert.Equal(expected, McpAgentSelection.MigrateOverride(agent, legacy));

    [Fact]
    public void ModelAndEffortArePassedOnClaudeResumeRuns()
    {
        var args = new List<string> { "--resume", "claude-session" };
        McpAgentSelection.AddClaudeOptions(args, "opus", "high");
        Assert.Equal("opus", args[args.IndexOf("--model") + 1]);
        Assert.Equal("high", args[args.IndexOf("--effort") + 1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("session")]
    public void CodexModelEffortArePassedForFreshAndResumedSessions(string? session)
    {
        var args = CodexBackend.Arguments("http://127.0.0.1:8788/mcp", session, model: "gpt-test", effort: "high");
        Assert.Equal("gpt-test", args[args.IndexOf("--model") + 1]);
        Assert.Contains("model_reasoning_effort=\"high\"", args);
        if (session != null)
        {
            Assert.True(args.IndexOf("--model") < args.IndexOf("resume"));
            Assert.Equal(session, args[args.IndexOf("resume") + 1]);
        }
        Assert.Equal("-", args.Last());
    }

    [Fact]
    public void CatalogFiltersHiddenAndDuplicateModelsAndUsesServerDefaults()
    {
        using var document = JsonDocument.Parse("""
        [
          {"model":"hidden","hidden":true},
          {"model":"gpt-a","displayName":"Model A","isDefault":true,
           "defaultReasoningEffort":"high","supportedReasoningEfforts":[{"reasoningEffort":"low"},{"reasoningEffort":"high"}]},
          {"model":"gpt-a"},
          {"model":"gpt-b","defaultReasoningEffort":"medium"}
        ]
        """);
        var models = CodexModels.Parse(document.RootElement.EnumerateArray());
        Assert.Equal(new[] { "gpt-a", "gpt-b" }, models.Select(m => m.Id));
        Assert.Equal(("gpt-a", "high"), CodexModels.Select(models, null, null));
        Assert.Equal(("gpt-a", "low"), CodexModels.Select(models, "gpt-a", "low"));
        Assert.Throws<IOException>(() => CodexModels.Select(models, "retired-model", null));
        Assert.Throws<IOException>(() => CodexModels.Select(models, "gpt-a", "ultra"));
    }
}
