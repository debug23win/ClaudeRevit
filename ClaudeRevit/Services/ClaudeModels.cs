using System;
using System.Collections.Generic;

namespace ClaudeRevit.Services;

// Per-model rules for the Anthropic path, keyed by our internal model tag ("opus-5-5", ...).
// Kept free of SDK and Revit types so the rules are unit-tested; ChatService, UsageTracker and
// the Claude Code backend all read them from here instead of keeping their own lists.
public static class ClaudeModels
{
    // A tag is the API id without its "claude-" prefix ("opus-5-5" <-> "claude-opus-5-5"), so
    // any model the Models API returns maps to a tag without a hand-kept table. A full id is
    // accepted as-is; "auto" (and an empty tag) resolve to the current default executor.
    public const string DefaultExecutor = "sonnet-5-5";
    public const string DefaultAdvisor = "opus-5-5";

    public static string ApiId(string tag)
    {
        var t = (tag ?? "").Trim();
        if (t.Length == 0 || t == "auto") return "claude-" + DefaultExecutor;
        return t.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) ? t : "claude-" + t;
    }

    public static string TagOf(string apiId)
    {
        var id = (apiId ?? "").Trim();
        return id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) ? id.Substring(7) : id;
    }

    // Normalise a served model id (either "claude-opus-4-8", a dated "claude-opus-4-5-20251101"
    // or an enum form like "ClaudeOpus4_8") to our rate tag: family, major, optional one-digit
    // minor. A date suffix is not a minor version. Unknown ids default to the advisor.
    public static string TagFromModel(string id) => Normalize(id) ?? DefaultAdvisor;

    // Family-major[-minor] of any Claude id or tag, or null when it names no Claude model.
    private static string? Normalize(string? id)
    {
        var s = (id ?? "").ToLowerInvariant().Replace("_", "-");
        var m = System.Text.RegularExpressions.Regex.Match(s, @"(fable|mythos|opus|sonnet|haiku)-?(\d+)(?:-(\d)(?!\d))?");
        if (!m.Success) return null;
        return m.Groups[1].Value + "-" + m.Groups[2].Value + (m.Groups[3].Success ? "-" + m.Groups[3].Value : "");
    }

    // Rules below are keyed by the normalised tag, so a dated id from the Models API
    // ("opus-4-5-20251101") or a full id ("claude-opus-5-5") gets its family's rules.
    private static string Key(string tag) => Normalize(tag) ?? tag;

    // Display name for a tag the Models API did not name: "opus-5-5" -> "Claude Opus 5.5".
    public static string DisplayName(string tag)
    {
        var parts = TagOf(ApiId(tag)).Split('-');
        if (parts.Length < 2 || parts[0].Length == 0 || !char.IsDigit(parts[1][0])) return TagOf(ApiId(tag));
        var version = parts[1] + (parts.Length > 2 && parts[2].Length == 1 ? "." + parts[2] : "");
        return "Claude " + char.ToUpperInvariant(parts[0][0]) + parts[0].Substring(1) + " " + version;
    }

    // Shown until the Models API answers (no API key, offline): the current generation only.
    // Same order as the API: most capable/newest first.
    public static readonly IReadOnlyList<ClaudeModelInfo> Current = new[]
    {
        new ClaudeModelInfo("fable-5-2", "Claude Fable 5.2"),
        new ClaudeModelInfo("opus-5-5", "Claude Opus 5.5"),
        new ClaudeModelInfo("sonnet-5-5", "Claude Sonnet 5.5"),
        new ClaudeModelInfo("haiku-5-5", "Claude Haiku 5.5"),
    };

    // The Models API answer as picker entries: Claude ids only, de-duplicated, order kept.
    public static IReadOnlyList<ClaudeModelInfo> FromServed(IEnumerable<(string Id, string? Name)> served)
    {
        var list = new List<ClaudeModelInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in served)
        {
            if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) continue;
            var tag = TagOf(id);
            if (!seen.Add(tag)) continue;
            list.Add(new ClaudeModelInfo(tag, string.IsNullOrWhiteSpace(name) ? DisplayName(tag) : name!.Trim()));
        }
        return list;
    }

    // Preserved thinking: these models bind each thinking block to the exact conversation before
    // it, and for accounts created from 31 August 2026 the API answers an edited history with a
    // 400. For them the history must be append-only: no tool-result aging, no client-side
    // compaction, no per-request text removed again, and system/tools kept stable.
    public static bool AppendOnlyHistory(string tag) => Key(tag) is "fable-5-2" or "mythos-5-2" or "fable-5-1" or "opus-5-5" or "sonnet-5-5" or "haiku-5-5";

    // Models whose safety classifier can decline a request and that accept the server-side
    // fallback ("fallbacks": "default"). Haiku 5.5 declines too but has no server-side fallback.
    public static bool SupportsServerFallback(string tag) => Key(tag) is "fable-5-2" or "mythos-5-2" or "fable-5" or "fable-5-1" or "opus-5" or "opus-5-5" or "sonnet-5-5";

    // Adaptive thinking and effort arrived with the 4.6 generation; Haiku 4.5 and anything older
    // (which the Models API may still list) reject both. Fable and Mythos always take them.
    public static bool SupportsAdaptiveThinking(string tag)
    {
        var t = Key(tag);
        if (t.StartsWith("fable-") || t.StartsWith("mythos-")) return true;
        var parts = t.Split('-');
        if (parts.Length < 2 || !int.TryParse(parts[1], out var major)) return true;
        var minor = parts.Length > 2 && int.TryParse(parts[2], out var n) ? n : 0;
        return major > 4 || (major == 4 && minor >= 6);
    }

    // List prices per million tokens (USD) and the cache-read price as a fraction of base input.
    // Cache writes are always 1-hour here (2x base input). Haiku 5.5 is priced by prompt size:
    // above 100K prompt tokens a request costs $0.50 / $2.50 instead of $0.10 / $0.50.
    public readonly record struct Price(decimal Input, decimal Output, decimal CacheRead);

    public const decimal CacheWriteMultiplier = 2.0m;

    public static bool TryPrice(string tag, long promptTokens, out Price price)
    {
        price = Key(tag) switch
        {
            "opus-5-5" => new(4.0m, 20.0m, 0.05m),
            "sonnet-5-5" => new(2.0m, 10.0m, 0.1m),
            "haiku-5-5" => promptTokens > 100_000 ? new(0.5m, 2.5m, 0.1m) : new(0.1m, 0.5m, 0.1m),
            "fable-5-2" or "mythos-5-2" => new(8.0m, 40.0m, 0.025m),
            "fable-5-1" => new(10.0m, 50.0m, 0.025m),
            "fable-5" => new(10.0m, 50.0m, 0.1m),
            "opus-5" => new(5.0m, 25.0m, 0.1m),
            "sonnet-5" => new(2.0m, 10.0m, 0.1m),
            "opus-4-8" or "opus-4-7" or "opus-4-6" => new(5.0m, 25.0m, 0.1m),
            "sonnet-4-6" => new(3.0m, 15.0m, 0.1m),
            "haiku-4-5" => new(1.0m, 5.0m, 0.1m),
            _ => default
        };
        if (price != default) return true;
        // Unknown tag: overestimate with the highest rate rather than accrue nothing.
        price = new(10.0m, 50.0m, 0.1m);
        return false;
    }

    public static decimal Cost(Price price, long input, long output, long cacheCreation, long cacheRead) =>
        input / 1_000_000m * price.Input
        + output / 1_000_000m * price.Output
        + cacheCreation / 1_000_000m * price.Input * CacheWriteMultiplier
        + cacheRead / 1_000_000m * price.Input * price.CacheRead;

    // Text appended to the transcript when a reply did not end normally. Null for a normal end.
    // Refusal: the partial output is not an answer and is not kept in history.
    public static string? StopNote(string? stopReason, string? refusalCategory) => stopReason switch
    {
        "refusal" => "[The model declined this request" +
                     (string.IsNullOrEmpty(refusalCategory) ? "" : $" (classifier category: {refusalCategory})") +
                     ". Any partial text above is not an answer and was not kept in the conversation. " +
                     "Rephrase the request or choose another model.]",
        "max_tokens" => "[The reply reached the output limit and is cut off. Say \"continue\" for the rest.]",
        "model_context_window_exceeded" => "[The conversation no longer fits the model's context window. " +
                                           "Clear the chat or start a new one.]",
        _ => null
    };
}

// One entry of the Claude model picker: our tag ("opus-5-5") and the name to show.
public sealed record ClaudeModelInfo(string Tag, string Name);
