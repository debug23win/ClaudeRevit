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
        var values = new Dictionary<string, object?>();
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array) { values[property.Name] = Compact(property.Value); continue; }
            var items = property.Value.EnumerateArray().ToArray();
            values[property.Name] = new { count = items.Length, sample = items.Take(200).Select(Compact).ToArray(), sample_truncated = items.Length > 200,
                total = items.All(e => e.ValueKind == JsonValueKind.Number) ? (double?)items.Sum(e => e.GetDouble()) : null };
        }
        return JsonSerializer.Serialize(values);
    }

    public static (string Before, string After) SummarizePair(string before, string after)
    {
        using var b = JsonDocument.Parse(before); using var a = JsonDocument.Parse(after);
        var bv = b.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone());
        var av = a.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone());
        foreach (var property in a.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array || !b.RootElement.TryGetProperty(property.Name, out var prior) || prior.ValueKind != JsonValueKind.Array) continue;
            var old = prior.EnumerateArray().ToArray(); var current = property.Value.EnumerateArray().ToArray();
            // Scalar length/elevation arrays retain totals. Identity-bearing element snapshots
            // retain additions, edits AND deletions, so a late addition is not hidden by sampling.
            bool HasId(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out _);
            if (!old.Concat(current).Any() || !old.Concat(current).All(HasId)) continue;
            var oldIds = old.ToDictionary(e => e.GetProperty("id").GetInt64());
            var newIds = current.ToDictionary(e => e.GetProperty("id").GetInt64());
            bool Different(JsonElement e, Dictionary<long, JsonElement> other) => !other.TryGetValue(e.GetProperty("id").GetInt64(), out var match) || e.GetRawText() != match.GetRawText();
            bv[property.Name] = old.Where(e => Different(e, newIds)).Select(e => e.Clone()).ToArray();
            av[property.Name] = current.Where(e => Different(e, oldIds)).Select(e => e.Clone()).ToArray();
            bv[property.Name + "_unchanged_omitted"] = old.Count(e => !Different(e, newIds));
            av[property.Name + "_unchanged_omitted"] = current.Count(e => !Different(e, oldIds));
        }
        return (SummarizeProbe(JsonSerializer.Serialize(bv)), SummarizeProbe(JsonSerializer.Serialize(av)));
    }

    private static object? Compact(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => Compact(p.Value)),
        JsonValueKind.Array => new { count = value.GetArrayLength(), sample = value.EnumerateArray().Take(200).Select(Compact).ToArray(), sample_truncated = value.GetArrayLength() > 200 },
        _ => value.Clone()
    };
}
