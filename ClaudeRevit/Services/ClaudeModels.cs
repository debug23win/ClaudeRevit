using System;

namespace ClaudeRevit.Services;

// Per-model rules for the Anthropic path, keyed by our internal model tag ("opus-5-5", ...).
// Kept free of SDK and Revit types so the rules are unit-tested; ChatService, UsageTracker and
// the Claude Code backend all read them from here instead of keeping their own lists.
public static class ClaudeModels
{
    public static string ApiId(string tag) => tag switch
    {
        "opus-5-5" => "claude-opus-5-5",
        "sonnet-5-5" => "claude-sonnet-5-5",
        "haiku-5-5" => "claude-haiku-5-5",
        "opus-5" => "claude-opus-5",
        "fable-5-1" => "claude-fable-5-1",
        "opus-4-8" => "claude-opus-4-8",
        "fable-5" => "claude-fable-5",
        "haiku-4-5" => "claude-haiku-4-5",
        "sonnet-4-6" => "claude-sonnet-4-6",
        "opus-4-7" => "claude-opus-4-7",
        _ => "claude-sonnet-5"
    };

    // Normalise a served model id (either "claude-opus-4-8" or an enum form like "ClaudeOpus4_8")
    // to our rate tag. Most specific first: "opus-5-5" must not fall into "opus-5", and
    // "fable-5-1" must not collapse into "fable-5". Unknown ids default to opus-4-8, the advisor.
    public static string TagFromModel(string id)
    {
        var s = id.ToLowerInvariant().Replace("_", "-");
        if (s.Contains("opus-5-5") || s.Contains("opus5-5")) return "opus-5-5";
        if (s.Contains("sonnet-5-5") || s.Contains("sonnet5-5")) return "sonnet-5-5";
        if (s.Contains("haiku-5-5") || s.Contains("haiku5-5")) return "haiku-5-5";
        if (s.Contains("opus-5") || s.Contains("opus5")) return "opus-5";
        if (s.Contains("fable-5-1") || s.Contains("fable5-1")) return "fable-5-1";
        if (s.Contains("opus") && s.Contains("4-7")) return "opus-4-7";
        if (s.Contains("opus")) return "opus-4-8";
        if (s.Contains("sonnet") && s.Contains("4-6")) return "sonnet-4-6";
        if (s.Contains("sonnet")) return "sonnet-5";
        if (s.Contains("haiku")) return "haiku-4-5";
        if (s.Contains("fable")) return "fable-5";
        return "opus-4-8";
    }

    // Preserved thinking: these models bind each thinking block to the exact conversation before
    // it, and for accounts created from 31 August 2026 the API answers an edited history with a
    // 400. For them the history must be append-only: no tool-result aging, no client-side
    // compaction, no per-request text removed again, and system/tools kept stable.
    public static bool AppendOnlyHistory(string tag) => tag is "fable-5-1" or "opus-5-5" or "sonnet-5-5" or "haiku-5-5";

    // Models whose safety classifier can decline a request and that accept the server-side
    // fallback ("fallbacks": "default"). Haiku 5.5 declines too but has no server-side fallback.
    public static bool SupportsServerFallback(string tag) => tag is "fable-5" or "fable-5-1" or "opus-5" or "opus-5-5" or "sonnet-5-5";

    // Haiku 4.5 predates adaptive thinking and rejects the effort parameter; every other
    // current model takes both.
    public static bool SupportsAdaptiveThinking(string tag) => tag != "haiku-4-5";

    // List prices per million tokens (USD) and the cache-read price as a fraction of base input.
    // Cache writes are always 1-hour here (2x base input). Haiku 5.5 is priced by prompt size:
    // above 100K prompt tokens a request costs $0.50 / $2.50 instead of $0.10 / $0.50.
    public readonly record struct Price(decimal Input, decimal Output, decimal CacheRead);

    public const decimal CacheWriteMultiplier = 2.0m;

    public static bool TryPrice(string tag, long promptTokens, out Price price)
    {
        price = tag switch
        {
            "opus-5-5" => new(4.0m, 20.0m, 0.05m),
            "sonnet-5-5" => new(2.0m, 10.0m, 0.1m),
            "haiku-5-5" => promptTokens > 100_000 ? new(0.5m, 2.5m, 0.1m) : new(0.1m, 0.5m, 0.1m),
            "fable-5-1" => new(10.0m, 50.0m, 0.025m),
            "fable-5" => new(10.0m, 50.0m, 0.1m),
            "opus-5" => new(5.0m, 25.0m, 0.1m),
            "sonnet-5" => new(2.0m, 10.0m, 0.1m),
            "opus-4-8" or "opus-4-7" => new(5.0m, 25.0m, 0.1m),
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
