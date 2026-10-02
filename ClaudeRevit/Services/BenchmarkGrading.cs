using System.Text.Json;

namespace ClaudeRevit.Services;

public sealed record BenchmarkVerdict(bool Pass, int Score, string Reason, bool Graded);
public static class BenchmarkGrading
{
    public static BenchmarkVerdict Parse(string raw)
    {
        var start = raw.IndexOf('{'); var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return new(false, 0, "Judge returned no JSON.", false);
        try
        {
            using var doc = JsonDocument.Parse(raw.Substring(start, end - start + 1)); var r = doc.RootElement;
            if (!r.TryGetProperty("pass", out var p) || p.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !r.TryGetProperty("score", out var s) || !s.TryGetInt32(out var score) ||
                !r.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String)
                return new(false, 0, "Judge JSON lacks a boolean pass, integer score or string reason.", false);
            return new(p.GetBoolean(), Math.Clamp(score, 0, 100), reason.GetString() ?? "", true);
        }
        catch { return new(false, 0, "Judge JSON parse failed.", false); }
    }
    public static string SummarizeProbe(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("probe_error", out var error)) throw new InvalidOperationException("Objective probe unavailable: " + error);
        if (doc.RootElement.TryGetProperty("no_document", out _)) throw new InvalidOperationException("Objective probe has no document.");
        var values = new Dictionary<string, object>();
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array) { values[property.Name] = property.Value.Clone(); continue; }
            var items = property.Value.EnumerateArray().ToArray();
            values[property.Name] = new { count = items.Length, sample = items.Take(30).Select(e => e.Clone()).ToArray(), sample_truncated = items.Length > 30,
                total = items.All(e => e.ValueKind == JsonValueKind.Number) ? (double?)items.Sum(e => e.GetDouble()) : null };
        }
        return JsonSerializer.Serialize(values);
    }
}
