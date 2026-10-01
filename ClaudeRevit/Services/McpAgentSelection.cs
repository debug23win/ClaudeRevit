using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ClaudeRevit.Services;

// A turn captures this value before leaving the UI thread. Provider-specific choices never
// share a model override, and legacy benchmark tags still resolve without a pane selection.
public sealed record McpAgentSelection(string Agent, string? Model = null, string? Effort = null)
{
    public static McpAgentSelection? Resolve(string tag, bool subscription, McpAgentSelection? selection)
    {
        if (selection != null)
        {
            if (selection.Agent is not ("codex" or "claudecode"))
                throw new ArgumentException("Unknown MCP agent: " + selection.Agent);
            return selection;
        }
        if (tag == "codex" || tag.StartsWith("codex:", StringComparison.Ordinal))
            return new("codex", tag.Length > 6 ? tag.Substring(6) : null);
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
