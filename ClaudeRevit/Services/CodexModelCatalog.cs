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
    // Discovery starts no model turn and consumes no inference tokens. Use the user's ordinary
    // Codex home/login. Keep it separate from the Revit MCP server and its bearer token.
    public static async Task<IReadOnlyList<CodexModel>> ReadAsync(string exe, string workDir, CancellationToken ct)
    {
        var resolved = ClaudeCodeBackend.Resolve(string.IsNullOrWhiteSpace(exe) ? "codex" : exe);
        if (resolved == null || !CodexCli.LooksLikeCodexBinary(resolved))
            throw new IOException("Codex CLI not found. Set its path in Settings → MCP.");
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
                if (!reply.TryGetProperty("id", out var id) || !id.TryGetInt32(out var number) || reply.TryGetProperty("method", out _)) continue;
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
