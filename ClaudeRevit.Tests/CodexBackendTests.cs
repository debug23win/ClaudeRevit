using System;
using System.Collections.Generic;
using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class CodexBackendTests
{
    [Fact]
    public void ArgumentsConfigureMcpWithoutExposingTokenAndResumeExactSession()
    {
        var args = CodexBackend.Arguments("http://127.0.0.1:8788/mcp", "session-test");
        Assert.Contains("--ignore-user-config", args);
        Assert.Contains("read-only", args);
        Assert.Contains("resume", args);
        Assert.Equal("session-test", args[args.IndexOf("resume") + 1]);
        Assert.Contains(args, arg => arg.Contains("bearer_token_env_var=\"CLAUDEREVIT_MCP_TOKEN\""));
        Assert.Contains(args, arg => arg.Contains("required=true"));
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", args);
        Assert.Equal("-", args[^1]);
    }

    [Fact]
    public void EventsReportMessagesToolsAndUsageWithoutDisplayingReasoning()
    {
        var result = new CodexBackend.Result();
        var text = new List<string>();
        var tools = new List<string>();
        foreach (var line in new[] {
            """{"type":"thread.started","thread_id":"session-test"}""",
            """{"type":"item.completed","item":{"type":"reasoning","text":"private"}}""",
            """{"type":"item.started","item":{"type":"mcp_tool_call","tool":"get_levels"}}""",
            """{"type":"item.completed","item":{"type":"agent_message","text":"Done"}}""",
            """{"type":"turn.completed","usage":{"input_tokens":100,"output_tokens":20}}""" })
            CodexBackend.ParseLine(line, result, text.Add, tools.Add);
        Assert.Equal("session-test", result.SessionId);
        Assert.Equal(new[] { "Done" }, text);
        Assert.Equal(new[] { "get_levels" }, tools);
        Assert.True(result.Completed);
        Assert.Equal(100, result.InputTokens);
        Assert.Equal(20, result.OutputTokens);
    }

    [Fact]
    public void FailureDoesNotBecomeSuccess()
    {
        var result = new CodexBackend.Result();
        CodexBackend.ParseLine("""{"type":"turn.failed","error":{"message":"Login required"}}""", result, _ => { }, _ => { });
        Assert.False(result.Completed);
        Assert.Equal("Login required", result.Error);
    }
}
