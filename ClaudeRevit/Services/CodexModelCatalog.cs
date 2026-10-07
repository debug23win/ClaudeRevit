using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeRevit.Services;

public static class CodexModelCatalog
{
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static string? _cacheKey;
    private static IReadOnlyList<CodexModel>? _cached;
    private static DateTime _expires;

    // Share discovery across modeller and judge tasks, while explicit Refresh always re-queries.
    // File metadata invalidates on CLI/config/account changes; credentials are never read here.
    public static async Task<IReadOnlyList<CodexModel>> ReadAsync(string exe, string workDir, CancellationToken ct, bool forceRefresh = false)
    {
        // The search walks PATH and several install folders, and this is awaited from the pane's
        // UI thread — so it runs on the pool, and only once (DiscoverAsync used to repeat it).
        var resolved = await Task.Run(() => CodexBackend.ResolveExecutable(exe), ct);
        if (resolved == null || !CodexCli.LooksLikeCodexBinary(resolved))
            throw new IOException("Codex CLI not found. Set its path in Settings → MCP.");
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome)) codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        static string Stamp(string path)
        {
            try { var file = new FileInfo(path); return file.Exists ? file.FullName + ":" + file.Length + ":" + file.LastWriteTimeUtc.Ticks : path + ":absent"; }
            catch { return path + ":unavailable"; }
        }
        var key = Stamp(resolved) + "|" + Stamp(Path.Combine(codexHome, "auth.json")) + "|" + Stamp(Path.Combine(codexHome, "config.toml"));
        await CacheGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!forceRefresh && _cached != null && _cacheKey == key && DateTime.UtcNow < _expires) return _cached;
            // A failed explicit refresh must not leave stale entries available to the next run.
            _cached = null;
            var models = await DiscoverAsync(resolved, workDir, ct);
            _cacheKey = key; _cached = models; _expires = DateTime.UtcNow.AddMinutes(5);
            return models;
        }
        finally { CacheGate.Release(); }
    }
    // Discovery starts no model turn and consumes no inference tokens. Use the user's ordinary
    // Codex home/login. Keep it separate from the Revit MCP server and its bearer token.
    private static async Task<IReadOnlyList<CodexModel>> DiscoverAsync(string resolved, string workDir, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var shim = resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(shim ? "cmd.exe" : resolved)
        {
            WorkingDirectory = workDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment.Remove("OPENAI_API_KEY");
        start.Environment.Remove("CODEX_API_KEY");
        if (shim) { start.ArgumentList.Add("/c"); start.ArgumentList.Add(resolved); }
        start.ArgumentList.Add("app-server");
        using var process = Process.Start(start) ?? throw new IOException("Cannot start Codex.");
        void Stop() { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }
        using var cancellation = token.Register(Stop);
        var errors = process.StandardError.ReadToEndAsync();
        var entries = new List<JsonElement>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        async Task Send(object message)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
        }
        try
        {
            await Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "clauderevit", title = "ClaudeRevit", version = "1.0" } } });
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var reply = document.RootElement;
                if (!reply.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) || reply.TryGetProperty("method", out _)) continue;
                if (reply.TryGetProperty("error", out var error)) throw new IOException("Codex: " + error);
                if (number == 1)
                {
                    await Send(new { method = "initialized", @params = new { } });
                    await Send(new { id = 2, method = "model/list", @params = new { limit = 100, includeHidden = false } });
                }
                else if (number == 2)
                {
                    var page = reply.GetProperty("result");
                    foreach (var entry in page.GetProperty("data").EnumerateArray()) entries.Add(entry.Clone());
                    if (entries.Count > 1000) throw new IOException("Codex model catalog exceeds 1000 entries.");
                    var cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
                    if (string.IsNullOrEmpty(cursor)) return CodexModels.Parse(entries);
                    if (!cursors.Add(cursor)) throw new IOException("Codex repeated a model catalog page.");
                    await Send(new { id = 2, method = "model/list", @params = new { limit = 100, includeHidden = false, cursor } });
                }
            }
            token.ThrowIfCancellationRequested();
            throw new IOException("Codex exited before returning models. " + await errors);
        }
        finally { Stop(); await errors; }
    }
}
