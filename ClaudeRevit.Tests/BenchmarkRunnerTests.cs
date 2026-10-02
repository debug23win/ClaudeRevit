using System.Text.Json;
using ClaudeRevit.Services;
using ClaudeRevit.Tools;
using Xunit;

namespace ClaudeRevit.Tests;

[Collection("BenchmarkRunner")]
public class BenchmarkRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClaudeRevit-benchmark-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ToolDispatcher _dispatcher = ToolDispatcher.Instance;
    public BenchmarkRunnerTests()
    {
        _dispatcher.Starts = _dispatcher.Ends = 0; _dispatcher.ScopeActive = _dispatcher.Family = false;
        _dispatcher.Tools.Clear(); ToolDispatcher.ForceSuppress = false; DocumentSessions.CurrentDocumentKey = "test-document";
        ChatService.Send = _ => Task.CompletedTask; ChatService.Grade = "{\"pass\":true,\"score\":90,\"reason\":\"objective test\"}";
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private Task Run(IReadOnlyList<BenchmarkTask> tasks, List<BenchmarkResult> rows, CancellationToken ct = default) =>
        BenchmarkRunner.RunAsync(new("api", "test"), tasks, new("api", "judge"), "run", true, 60, 0, _ => { }, rows.Add, ct, Path.Combine(_root, "results.jsonl"));
    [Fact]
    public async Task SkipsWrongDocumentAndWritesQualitySpeedAndFormulaWithoutInventedPoints()
    {
        var rows = new List<BenchmarkResult>();
        await Run(new[] { BenchmarkTasks.All[0], BenchmarkTasks.All.Single(t => t.Id == "F1") }, rows);
        Assert.Equal(1, _dispatcher.Starts); Assert.Equal(1, _dispatcher.Ends); Assert.False(_dispatcher.ScopeActive);
        Assert.Equal(90, rows[0].Quality); Assert.Equal(90, rows[0].Score);
        Assert.Equal("—", rows[1].Verdict); Assert.Null(rows[1].Score);
        var lines = File.ReadAllLines(Path.Combine(_root, "results.jsonl"));
        using var first = JsonDocument.Parse(lines[0]); using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal(BenchmarkScoring.Version, first.RootElement.GetProperty("scoring_version").GetString());
        Assert.Equal(30, first.RootElement.GetProperty("reference_seconds").GetInt32());
        Assert.True(second.RootElement.GetProperty("skipped").GetBoolean());
        Assert.False(second.RootElement.GetProperty("graded").GetBoolean());
    }
    [Fact]
    public async Task CancellationRollsBackScopeAndRestoresSuppression()
    {
        using var cancellation = new CancellationTokenSource();
        ChatService.Send = _ => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(new[] { BenchmarkTasks.All[0] }, new(), cancellation.Token));
        Assert.Equal(1, _dispatcher.Ends); Assert.False(_dispatcher.ScopeActive); Assert.False(ToolDispatcher.ForceSuppress);
    }
    [Fact]
    public async Task DocumentSwitchStopsGradingAndRollsBackOriginalScope()
    {
        ChatService.Send = _ => { DocumentSessions.CurrentDocumentKey = "other-document"; return Task.CompletedTask; };
        var rows = new List<BenchmarkResult>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new[] { BenchmarkTasks.All[0] }, rows));
        Assert.Empty(rows); Assert.Equal(1, _dispatcher.Ends); Assert.False(ToolDispatcher.ForceSuppress);
    }
    [Fact]
    public async Task FamilyTaskGetsIndependentAnalysisAndFlexAndUnavailableJudgeHasNoPoints()
    {
        _dispatcher.Family = true; ChatService.Grade = "unavailable";
        var rows = new List<BenchmarkResult>();
        await Run(new[] { BenchmarkTasks.All.Single(t => t.Id == "F1") }, rows);
        Assert.Equal(2, _dispatcher.Tools.Count(t => t == "analyze_family_structure"));
        Assert.Single(_dispatcher.Tools, t => t == "flex_family");
        Assert.Equal("?", rows[0].Verdict); Assert.Null(rows[0].Score); Assert.Equal(1, _dispatcher.Ends);
    }
}
