using System;
using System.Collections.Generic;

namespace ClaudeRevit.Services;

public static class UsageTracker
{
    // Per-model accumulators
    private static readonly Dictionary<string, ModelUsage> _byModel = new();

    private static readonly HashSet<string> _unknownTagsLogged = new();

    // Price of one request. Rates, cache multipliers and Haiku 5.5's prompt-size tier live in
    // ClaudeModels. Returns false only for genuinely unknown ANTHROPIC tags (caller may log).
    private static bool CostOf(string modelTag, long input, long output, long cacheCreation, long cacheRead, out decimal cost)
    {
        if (modelTag.StartsWith("alt:", StringComparison.Ordinal))
        {
            cost = 0m;
            return true;
        }
        var known = ClaudeModels.TryPrice(modelTag, input + cacheCreation + cacheRead, out var price);
        cost = ClaudeModels.Cost(price, input, output, cacheCreation, cacheRead);
        return known;
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

        // Persist the estimated spend so the balance countdown survives restarts. An
        // unknown model tag must NOT silently accrue $0 — the countdown exists for spend
        // safety, so overestimate with the highest known rate and log once. Alternative
        // providers ("alt:" prefix) are the deliberate exception: their pricing is
        // unknown to us (often free / near-free) and the balance countdown tracks the
        // Anthropic account, so they contribute tokens but no dollars.
        if (!CostOf(modelTag, inputTokens, outputTokens, cacheCreationTokens, cacheReadTokens, out var cost) && _unknownTagsLogged.Add(modelTag))
            Log.Info($"UsageTracker: no price for model '{modelTag}' — using the highest known rate.");

        cur.Input += inputTokens;
        cur.Output += outputTokens;
        cur.CacheCreation += cacheCreationTokens;
        cur.CacheRead += cacheReadTokens;
        // Priced per request: Haiku 5.5's rate depends on each request's prompt size.
        cur.Cost += cost;
        _byModel[modelTag] = cur;

        SettingsStore.AddSpend(cost);

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
                cost += kv.Value.Cost;
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
        public decimal Cost;
    }
}
