using System.IO;
using System.Text.Json;

namespace ClaudeRevit.Services;

public sealed record CodexUsage(long Input, long Cached, long Output, long Reasoning, string Scope)
{
    // Read accounting records only. Message and reasoning content is never loaded/exported.
    public static CodexUsage? ReadTurn(string sessionId, DateTime startedUtc, string? home = null)
    {
        if (!Guid.TryParse(sessionId, out _)) return null;
        home ??= Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var sessions = Path.Combine(home, "sessions");
        try
        {
            // A resumed session's file lives on its creation date, encoded in UUIDv7.
            var hex = sessionId.Replace("-", "");
            var created = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(hex[..12],16)).UtcDateTime;
            // Which calendar day names the folder is Codex's choice (local time, most likely), and
            // near midnight the UTC and local dates differ — looking only in the UTC day silently
            // lost the usage for those turns. Try the plausible days, nearest first.
            var days = new[] { created.ToLocalTime().Date, created.Date, created.Date.AddDays(-1), created.Date.AddDays(1) }.Distinct();
            string? path = null;
            foreach (var day in days)
            {
                var folder = Path.Combine(sessions, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
                if (!Directory.Exists(folder)) continue;
                path = Directory.EnumerateFiles(folder, "*" + sessionId + ".jsonl").FirstOrDefault();
                if (path != null) break;
            }
            if (path == null) return null;
            CodexUsage? last = null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Current turn accounting is at the tail, even after compaction. Bound IO.
            if (stream.Length > 8_000_000) stream.Seek(-8_000_000, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("\"token_usage_record\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line); var r = document.RootElement;
                    if (r.GetProperty("type").GetString() != "token_usage_record" || r.GetProperty("timestamp").GetDateTime().ToUniversalTime() < startedUtc) continue;
                    var p = r.GetProperty("payload");
                    if (!p.TryGetProperty("turn_token_usage", out var u)) continue;
                    long Number(string key) => u.TryGetProperty(key, out var v) ? v.GetInt64() : 0;
                    last = new(Number("input_tokens"),Number("cached_input_tokens"),Number("output_tokens"),Number("reasoning_output_tokens"),"turn");
                }
                catch (JsonException) { }
            }
            return last;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return null; }
    }
}
