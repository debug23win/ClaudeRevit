using System;
using System.Collections.Generic;

namespace ClaudeRevit.Services;

public static class UsageTracker
{
    // Per-model accumulators
    private static readonly Dictionary<string, ModelUsage> _byModel = new();

    // Base (list) rates per million tokens, USD. Time-limited launch discounts are
    // applied in RateFor, not baked in here, so they revert automatically when they end.
    private static readonly Dictionary<string, (decimal Input, decimal Output)> _rates = new()
    {
        ["sonnet-5"] = (3.0m, 15.0m),
        ["opus-4-8"] = (5.0m, 25.0m),
        ["fable-5"] = (10.0m, 50.0m),
        ["haiku-4-5"] = (1.0m, 5.0m),
        ["sonnet-4-6"] = (3.0m, 15.0m),
        ["opus-4-7"] = (5.0m, 25.0m)
    };

    // Sonnet 5 launched with introductory pricing of $2/$10 per MTok through
    // 2026-08-31; it reverts to the $3/$15 list rate above afterwards. Charging the
    // list rate during the intro window overstated spend by ~50% on the default model.
    private static readonly DateTime Sonnet5IntroEndUtc = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    // 1-hour cache: write = 2x base input rate; read = 0.1x base input rate
    private const decimal CacheWriteMultiplier = 2.0m;
    private const decimal CacheReadMultiplier = 0.1m;

    private static readonly HashSet<string> _unknownTagsLogged = new();

    // Returns false only for genuinely unknown ANTHROPIC tags (caller may want to log).
    private static bool RateFor(string modelTag, out (decimal Input, decimal Output) rate)
    {
        if (modelTag.StartsWith("alt:", StringComparison.Ordinal))
        {
            rate = (0m, 0m);
            return true;
        }
        if (modelTag == "sonnet-5" && DateTime.UtcNow < Sonnet5IntroEndUtc)
        {
            rate = (2.0m, 10.0m); // introductory pricing (reverts to list on 2026-09-01)
            return true;
        }
        if (_rates.TryGetValue(modelTag, out rate)) return true;
        rate = (10.0m, 50.0m);
        return false;
    }

    public static event Action? Updated;

    public static void Add(
        string modelTag,
        long inputTokens,
        long outputTokens,
        long cacheCreationTokens,
        long cacheReadTokens)
    {
        if (!_byModel.TryGetValue(modelTag, out var cur))
            cur = new ModelUsage();

        cur.Input += inputTokens;
        cur.Output += outputTokens;
        cur.CacheCreation += cacheCreationTokens;
        cur.CacheRead += cacheReadTokens;
        _byModel[modelTag] = cur;

        // Persist the estimated spend so the balance countdown survives restarts. An
        // unknown model tag must NOT silently accrue $0 — the countdown exists for spend
        // safety, so overestimate with the highest known rate and log once. Alternative
        // providers ("alt:" prefix) are the deliberate exception: their pricing is
        // unknown to us (often free / near-free) and the balance countdown tracks the
        // Anthropic account, so they contribute tokens but no dollars.
        if (!RateFor(modelTag, out var rate) && _unknownTagsLogged.Add(modelTag))
            Log.Info($"UsageTracker: no price for model '{modelTag}' — using the highest known rate.");
        SettingsStore.AddSpend(
            inputTokens / 1_000_000m * rate.Input
            + outputTokens / 1_000_000m * rate.Output
            + cacheCreationTokens / 1_000_000m * rate.Input * CacheWriteMultiplier
            + cacheReadTokens / 1_000_000m * rate.Input * CacheReadMultiplier);

        Updated?.Invoke();
    }

    public static (long Input, long Output, long CacheCreation, long CacheRead, decimal Cost) Totals
    {
        get
        {
            long ti = 0, to = 0, tcc = 0, tcr = 0;
            decimal cost = 0;
            foreach (var kv in _byModel)
            {
                ti += kv.Value.Input;
                to += kv.Value.Output;
                tcc += kv.Value.CacheCreation;
                tcr += kv.Value.CacheRead;
                // Same fallback as Add(): unknown models show the conservative estimate
                // instead of silently displaying $0; alt providers really are $0 here.
                RateFor(kv.Key, out var rate);
                cost += kv.Value.Input / 1_000_000m * rate.Input
                      + kv.Value.Output / 1_000_000m * rate.Output
                      + kv.Value.CacheCreation / 1_000_000m * rate.Input * CacheWriteMultiplier
                      + kv.Value.CacheRead / 1_000_000m * rate.Input * CacheReadMultiplier;
            }
            return (ti, to, tcc, tcr, cost);
        }
    }

    public static void Reset()
    {
        _byModel.Clear();
        Updated?.Invoke();
    }

    public static string Format()
    {
        var (i, o, cc, cr, cost) = Totals;
        // The balance countdown is meaningful even before this session spends anything.
        var balance = SettingsStore.BalanceUsd;
        var balanceLabel = balance > 0
            ? $" · balance ≈ ${Math.Max(0, balance - SettingsStore.SpentUsd):F2}"
            : "";

        if (i == 0 && o == 0 && cc == 0 && cr == 0)
            return balanceLabel.Length > 0 ? balanceLabel[3..] : "";

        var totalInput = i + cc + cr;
        var cacheLabel = (cc + cr) > 0
            ? $" ({(int)(cr * 100.0 / Math.Max(1, totalInput))}% cached)"
            : "";

        return $"{FormatTokens(totalInput)} in{cacheLabel} · {FormatTokens(o)} out · ${cost:F3}{balanceLabel}";
    }

    private static string FormatTokens(long n) => n switch
    {
        < 1_000 => n.ToString(),
        < 1_000_000 => $"{n / 1000.0:F1}K",
        _ => $"{n / 1_000_000.0:F2}M"
    };

    private sealed class ModelUsage
    {
        public long Input;
        public long Output;
        public long CacheCreation;
        public long CacheRead;
    }
}
