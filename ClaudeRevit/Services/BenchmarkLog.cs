using System.IO;
using System.Text.Json;

namespace ClaudeRevit.Services;

// One append-only file per run. Flush each event so a crash leaves useful evidence.
public sealed class BenchmarkLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private long _sequence;
    public static string DirectoryName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeRevit", "benchmarks");
    public string RunId { get; } = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
    public string PathName { get; }
    public string? TaskId { get; set; }
    public BenchmarkLog(string? directory = null)
    {
        directory ??= DirectoryName;
        Directory.CreateDirectory(directory);
        PathName = Path.Combine(directory, RunId + ".jsonl");
        _writer = new StreamWriter(new FileStream(PathName, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }
    public void Write(string kind, object? data)
    {
        lock (_gate)
            _writer.WriteLine(JsonSerializer.Serialize(new { schema_version = 1, run_id = RunId, sequence = ++_sequence, utc = DateTime.UtcNow, task = TaskId, kind, data }));
    }
    public void Journal(string line) { using var json = JsonDocument.Parse(line); Write("task_journal", json.RootElement); }
    public void Dispose() { lock (_gate) _writer.Dispose(); }

    public static object Read(string runId, int offset, int limit, string? directory = null)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Invalid run_id.");
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentException("Use offset >= 0 and limit 1..100.");
        using var stream = new FileStream(Path.Combine(directory ?? DirectoryName, runId + ".jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        for (var i = 0; i < offset; i++) if (reader.ReadLine() == null) return new { events = Array.Empty<JsonElement>(), next_offset = offset, has_more = false };
        var events = new List<JsonElement>();
        // Bound response size as well as event count; one complete large event is retained.
        var chars = 0;
        var incomplete = false;
        while (events.Count < limit && chars < 200_000 && reader.ReadLine() is { } line)
        {
            try { events.Add(JsonSerializer.Deserialize<JsonElement>(line)); chars += line.Length; }
            catch (JsonException) when (reader.EndOfStream) { incomplete = true; break; }
        }
        return new { events, next_offset = offset + events.Count, has_more = incomplete || reader.Peek() >= 0 };
    }
}
