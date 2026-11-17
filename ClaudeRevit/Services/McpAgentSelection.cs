using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ClaudeRevit.Services;

// A turn captures this value before leaving the UI thread. Provider-specific choices never
// share a model override, and legacy benchmark tags still resolve without a pane selection.
public sealed record McpAgentSelection(string Agent, string? Model = null, string? Effort = null)
{
    public static bool IsSubscriptionAgent(string? agent) => agent is "codex" or "claudecode" or "qwen" or "kimi";

    public static McpAgentSelection? Resolve(string tag, bool subscription, McpAgentSelection? selection)
    {
        if (selection != null)
        {
            if (!IsSubscriptionAgent(selection.Agent))
                throw new ArgumentException("Unknown MCP agent: " + selection.Agent);
            return selection;
        }
        if (tag == "codex" || tag.StartsWith("codex:", StringComparison.Ordinal))
            return new("codex", tag.Length > 6 ? tag.Substring(6) : null);
        foreach (var agent in new[] { "qwen", "kimi" })
            if (tag == agent || tag.StartsWith(agent + ":", StringComparison.Ordinal))
                return new(agent, tag.Length > agent.Length ? tag[(agent.Length + 1)..] : null);
        if (tag == "claudecode" || subscription)
            return new("claudecode");
        return null;
    }

    public static void AddClaudeOptions(List<string> args, string? model, string? effort)
    {
        if (!string.IsNullOrWhiteSpace(model)) args.AddRange(new[] { "--model", model.Trim() });
        if (!string.IsNullOrWhiteSpace(effort)) args.AddRange(new[] { "--effort", effort.Trim() });
    }

    public static string? MigrateOverride(string agent, string? legacy)
    {
        // The old shared field only belonged to Claude/Codex. Never apply a Claude model to a
        // newly added provider, even when an old settings file still carries this override.
        if (agent is not ("codex" or "claudecode")) return null;
        if (string.IsNullOrWhiteSpace(legacy)) return null;
        var model = legacy.Trim();
        var codex = model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ||
                    model.StartsWith("codex", StringComparison.OrdinalIgnoreCase);
        return (agent == "codex") == codex ? model : null;
    }
}

internal static class CodexConfiguration
{
    // TOML basic strings accept JSON string escaping for these scalar values.
    public static void Add(List<string> args, string key, string value) =>
        args.AddRange(new[] { "-c", key + "=" + JsonSerializer.Serialize(value) });
}
