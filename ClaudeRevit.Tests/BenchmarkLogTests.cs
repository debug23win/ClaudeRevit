using System.Text.Json;
using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public sealed class BenchmarkLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "benchmark-log-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    [Fact]
    public void LogIsReadableBeforeCloseAndPagingPreservesEvidence()
    {
        using var log = new BenchmarkLog(_root) { TaskId = "B0" };
        log.Write("before_probe", new { levels = 2 });
        log.Write("failure", new { error = "first line\nsecond line", stack = "stack evidence" });
        using var page = JsonDocument.Parse(JsonSerializer.Serialize(BenchmarkLog.Read(log.RunId, 0, 1, _root)));
        Assert.True(page.RootElement.GetProperty("has_more").GetBoolean());
        var first = page.RootElement.GetProperty("events")[0];
        Assert.Equal("B0", first.GetProperty("task").GetString());
        Assert.Equal(1, first.GetProperty("sequence").GetInt64());
        using var next = JsonDocument.Parse(JsonSerializer.Serialize(BenchmarkLog.Read(log.RunId, 1, 1, _root)));
        Assert.False(next.RootElement.GetProperty("has_more").GetBoolean());
        Assert.Equal("first line\nsecond line", next.RootElement.GetProperty("events")[0].GetProperty("data").GetProperty("error").GetString());
        Assert.Throws<ArgumentException>(() => BenchmarkLog.Read("../settings", 0, 1, _root));
        Assert.Throws<ArgumentException>(() => BenchmarkLog.Read(log.RunId, -1, 1, _root));
    }
    [Fact]
    public async Task JournalRoutesRevitThreadEventsByTaskIdAndRemovesCompletedTasks()
    {
        var captured = new List<string>();
        string id;
        using (TaskJournal.Capture(line => captured.Add(line)))
        using (TaskJournal.Start("benchmark-test"))
        {
            id = TaskJournal.CurrentId!;
            // Simulate the Revit API thread, which doesn't inherit AsyncLocal.
            Task worker;
            using (ExecutionContext.SuppressFlow()) worker = Task.Run(() => TaskJournal.Append(new { kind = "tool", task_id = id, error = "tool failed" }));
            await worker;
            Assert.True(TaskJournal.IsCaptured(id));
        }
        Assert.False(TaskJournal.IsCaptured(id));
        TaskJournal.Append(new { kind = "unrelated", task_id = id });
        Assert.Equal(2, captured.Count);
        Assert.Contains("tool failed", captured[1]);
    }
    [Fact]
    public void ReadingDuringPartialAppendDoesNotAdvancePastIncompleteEvent()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "partial.jsonl");
        File.WriteAllText(path, "{\"kind\":");
        using var partial = JsonDocument.Parse(JsonSerializer.Serialize(BenchmarkLog.Read("partial", 0, 20, _root)));
        Assert.Equal(0, partial.RootElement.GetProperty("next_offset").GetInt32());
        Assert.True(partial.RootElement.GetProperty("has_more").GetBoolean());
        File.AppendAllText(path, "\"tool_completed\"}\n");
        using var completed = JsonDocument.Parse(JsonSerializer.Serialize(BenchmarkLog.Read("partial", 0, 20, _root)));
        Assert.Equal(1, completed.RootElement.GetProperty("next_offset").GetInt32());
        Assert.False(completed.RootElement.GetProperty("has_more").GetBoolean());
    }

}
