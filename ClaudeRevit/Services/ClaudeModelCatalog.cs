using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Models;

namespace ClaudeRevit.Services;

// The Claude models this account can use, read from the Models API (GET /v1/models) — the
// counterpart of CodexModelCatalog, so the pickers follow what Anthropic serves instead of a
// hand-kept list that goes stale with each release. Listing models is free: no inference runs.
// Without an API key (Claude Code on a subscription) the built-in current generation is used.
public static class ClaudeModelCatalog
{
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static string? _cacheKey;
    private static IReadOnlyList<ClaudeModelInfo>? _cached;
    private static DateTime _expires;

    // True when the last answer came from the Models API rather than the built-in list.
    public static bool LastWasLive { get; private set; }

    public static async Task<IReadOnlyList<ClaudeModelInfo>> ReadAsync(CancellationToken ct, bool forceRefresh = false)
    {
        var apiKey = ApiKeyStore.Load();
        if (string.IsNullOrWhiteSpace(apiKey)) { LastWasLive = false; return ClaudeModels.Current; }
        // The key itself is never kept; its hash only tells a changed key from the cached one.
        var key = Convert.ToBase64String(System.Security.Cryptography.SHA256.Create()
            .ComputeHash(System.Text.Encoding.UTF8.GetBytes(apiKey)));
        await CacheGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!forceRefresh && _cached != null && _cacheKey == key && DateTime.UtcNow < _expires)
            { LastWasLive = true; return _cached; }
            // A failed explicit refresh must not leave stale entries available to the next run.
            _cached = null;
            var models = await DiscoverAsync(apiKey!, ct);
            if (models.Count == 0) throw new InvalidOperationException("The Models API returned no Claude models.");
            _cacheKey = key; _cached = models; _expires = DateTime.UtcNow.AddMinutes(5);
            LastWasLive = true;
            return models;
        }
        finally { CacheGate.Release(); }
    }

    private static async Task<IReadOnlyList<ClaudeModelInfo>> DiscoverAsync(string apiKey, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var client = new AnthropicClient(new ClientOptions { ApiKey = apiKey });
        var served = new List<(string, string?)>();
        // Newest first, as the API orders them; bounded so a misbehaving cursor cannot loop.
        IPage<ModelInfo> page = await client.Models.List(new ModelListParams { Limit = 100 }, timeout.Token);
        for (var pages = 0; ; pages++)
        {
            foreach (var model in page.Items) served.Add((model.ID, model.DisplayName));
            if (pages >= 20 || !page.HasNext()) break;
            page = await page.Next(timeout.Token);
        }
        return ClaudeModels.FromServed(served);
    }
}
