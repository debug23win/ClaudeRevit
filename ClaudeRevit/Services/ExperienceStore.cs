using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ClaudeRevit.Services;

// The learning layer built on top of ScriptJournal. The journal lives on disk at
// %AppData%\ClaudeRevit\script_journal.jsonl, independent of the conversation — so
// clearing the chat window (which only wipes the in-memory + conversation.json history)
// never erases what the program has learned. Two products come from the same journal:
//
//   * Digest() — a compact, session-stable summary of the script patterns that have
//     already WORKED here, injected into the system prompt so the assistant keeps that
//     experience even in a brand-new or just-cleared window.
//
//   * WriteDiagnosticReport() — a fuller, developer-facing report written when Revit
//     closes. Loaded back into the ClaudeRevit developer conversation, it lists the
//     recurring Dynamo/C# scripts as concrete candidates to promote into dedicated
//     native tools: the recorded model delta is exactly what such a tool must reproduce
//     without running arbitrary code.
public static class ExperienceStore
{
    public static string ReportPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeRevit", "diagnostic_report.md");

    // Built once per process and reused: the system prompt is prompt-cached for 1h, so a
    // digest that shifted every turn would defeat the cache. Within-session learning is
    // still visible through the live history and the get_script_journal tool; the digest
    // captures everything from EARLIER sessions (and survives a window clear).
    private static string? _digest;
    private static bool _digestBuilt;

    public static string? Digest()
    {
        if (_digestBuilt) return _digest;
        _digestBuilt = true;
        try { _digest = BuildDigest(); } catch { _digest = null; }
        return _digest;
    }

    // A "pattern" = one recurring kind of script, keyed by (tool, engine, the set of
    // element categories it touched). Scripts that create/modify the same categories are
    // almost always the same operation re-run with different numbers — exactly the thing
    // worth turning into a dedicated tool.
    private sealed class Pattern
    {
        public string Tool = "";
        public string? Engine;
        public List<string> Categories = new();
        public int Runs;
        public int Ok;
        public string? SampleCode;      // a representative SUCCESSFUL snippet
        public string? SampleResult;
        public string LastTsUtc = "";
    }

    private static List<Pattern> ExtractPatterns()
    {
        // The journal is trimmed to a couple hundred entries, so one generous read covers it.
        var entries = ScriptJournal.ReadRecent(500);
        var byKey = new Dictionary<string, Pattern>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            try
            {
                var tool = GetString(e, "tool") ?? "(script)";
                var engine = GetString(e, "engine");
                var ok = e.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                var cats = ChangedCategories(e);
                var key = tool + "|" + (engine ?? "") + "|" + string.Join(",", cats);
                if (!byKey.TryGetValue(key, out var p))
                {
                    p = new Pattern { Tool = tool, Engine = engine, Categories = cats };
                    byKey[key] = p;
                }
                p.Runs++;
                if (ok) p.Ok++;

                // Entries arrive newest-first, so the first success we see per pattern is
                // its most-recent working sample — the best one to show and to reuse.
                if (ok && p.SampleCode == null)
                {
                    p.SampleCode = GetString(e, "code");
                    p.SampleResult = GetString(e, "result");
                }
                var ts = GetString(e, "ts") ?? "";
                if (string.CompareOrdinal(ts, p.LastTsUtc) > 0) p.LastTsUtc = ts;
            }
            catch { /* skip a malformed line */ }
        }
        return byKey.Values
            .OrderByDescending(p => p.Runs)
            .ThenByDescending(p => p.Ok)
            .ThenByDescending(p => p.LastTsUtc, StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> ChangedCategories(JsonElement e)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        if (e.TryGetProperty("changes", out var ch) && ch.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "added_by_category", "modified_by_category" })
                if (ch.TryGetProperty(name, out var obj) && obj.ValueKind == JsonValueKind.Object)
                    foreach (var prop in obj.EnumerateObject())
                        set.Add(prop.Name);
        }
        return set.ToList();
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Compact digest for the system prompt — only patterns that have SUCCEEDED at least
    // once, capped in count and characters so it can't bloat the cached prefix.
    private static string? BuildDigest(int maxPatterns = 8, int maxChars = 4000)
    {
        var patterns = ExtractPatterns().Where(p => p.Ok > 0).ToList();
        if (patterns.Count == 0) return null;

        var sb = new StringBuilder();
        sb.Append("PROVEN SCRIPTS IN THIS ENVIRONMENT (learned from earlier sessions and kept ");
        sb.Append("even after the chat is cleared — the model delta each produced is real and ");
        sb.Append("reproducible here). Reuse these patterns instead of re-deriving them; adapt ");
        sb.Append("the numbers to the request:\n");

        int shown = 0;
        foreach (var p in patterns)
        {
            if (shown >= maxPatterns) break;
            var cats = p.Categories.Count > 0 ? string.Join(", ", p.Categories) : "no element change";
            sb.Append("\n• ").Append(p.Tool);
            if (!string.IsNullOrEmpty(p.Engine)) sb.Append(" (").Append(p.Engine).Append(')');
            sb.Append(" — touches: ").Append(cats)
              .Append("; succeeded ").Append(p.Ok).Append('/').Append(p.Runs).Append("×.");
            var snippet = TextUtil.TruncateOrNull(p.SampleCode?.Trim(), 320);
            if (!string.IsNullOrEmpty(snippet))
                sb.Append("\n  proven code:\n").Append(Indent(snippet, "    "));
            shown++;
            if (sb.Length > maxChars) break;
        }

        var text = sb.ToString();
        return text.Length > maxChars ? text[..maxChars] : text;
    }

    // Fuller, developer-facing report. Markdown so it reads well when loaded back into the
    // ClaudeRevit dev conversation. Each pattern is framed as a tool-promotion candidate.
    public static string BuildDiagnosticReport()
    {
        var patterns = ExtractPatterns();
        var sb = new StringBuilder();
        sb.AppendLine("# ClaudeRevit — Diagnostic & Learning Report");
        sb.AppendLine();
        sb.AppendLine("Generated automatically when Revit last closed. Load this file back into ");
        sb.AppendLine("the ClaudeRevit developer conversation: each recurring pattern below is a ");
        sb.AppendLine("candidate to promote from an ad-hoc Dynamo/C# script into a dedicated ");
        sb.AppendLine("native tool. The recorded model delta (categories added/modified) is ");
        sb.AppendLine("exactly what such a tool must reproduce without running arbitrary code.");
        sb.AppendLine();

        if (patterns.Count == 0)
        {
            sb.AppendLine("_No script executions were journaled yet — nothing to promote._");
            return sb.ToString();
        }

        var total = patterns.Sum(p => p.Runs);
        var succeeded = patterns.Sum(p => p.Ok);
        sb.AppendLine($"- Total journaled script runs: **{total}** (succeeded {succeeded})");
        sb.AppendLine($"- Distinct patterns: **{patterns.Count}**");
        sb.AppendLine();
        sb.AppendLine("## Candidate tools (most-used first)");
        sb.AppendLine();

        int i = 0;
        foreach (var p in patterns)
        {
            i++;
            var cats = p.Categories.Count > 0
                ? string.Join(", ", p.Categories)
                : "(no element change recorded — may be a query/inspection script)";
            sb.AppendLine($"### {i}. {SuggestToolName(p)}");
            sb.AppendLine();
            sb.Append($"- Source: `{p.Tool}`");
            if (!string.IsNullOrEmpty(p.Engine)) sb.Append($" · engine `{p.Engine}`");
            sb.AppendLine();
            sb.AppendLine($"- Runs: **{p.Runs}**, succeeded {p.Ok}/{p.Runs}");
            sb.AppendLine($"- Model delta (categories touched): {cats}");
            if (!string.IsNullOrEmpty(p.LastTsUtc))
                sb.AppendLine($"- Last seen (UTC): {p.LastTsUtc}");
            if (!string.IsNullOrWhiteSpace(p.SampleResult))
                sb.AppendLine($"- Sample result: {TextUtil.Truncate(p.SampleResult!.Trim(), 300)}");
            if (!string.IsNullOrWhiteSpace(p.SampleCode))
            {
                sb.AppendLine();
                sb.AppendLine("Representative proven code:");
                sb.AppendLine();
                sb.AppendLine("```csharp");
                sb.AppendLine(p.SampleCode!.Trim());
                sb.AppendLine("```");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // Best-effort human-readable name for the tool this pattern should become.
    private static string SuggestToolName(Pattern p)
    {
        if (p.Categories.Count == 0)
            return $"Native tool for the repeated `{p.Tool}` script";
        return "Native tool: " + string.Join(" + ", p.Categories.Take(3));
    }

    // Written on shutdown — best-effort, never throws into Revit's close path.
    public static void WriteDiagnosticReport()
    {
        try
        {
            var report = BuildDiagnosticReport();
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
            File.WriteAllText(ReportPath, report);
            Log.Info($"Diagnostic report written to {ReportPath}.");
        }
        catch (Exception ex) { Log.Error("ExperienceStore.WriteDiagnosticReport failed", ex); }
    }

    private static string Indent(string text, string prefix) =>
        string.Join("\n", text.Split('\n').Select(l => prefix + l));
}
