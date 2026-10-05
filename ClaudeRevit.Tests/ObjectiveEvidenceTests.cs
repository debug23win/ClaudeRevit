using System.Text.Json;
using ClaudeRevit.Services;
using Xunit;
namespace ClaudeRevit.Tests;

public class ObjectiveEvidenceTests
{
    [Fact] public void ConfidentJudgeCannotPassWrongUnits()
    {
        var report = BenchmarkObjective.Evaluate("B0", "{\"levels\":0,\"level_elements\":[]}", "{\"levels\":1,\"level_elements\":[{\"id\":1,\"name\":\"Bench B0\",\"elevation_m\":3500}]}");
        var verdict = BenchmarkObjective.Apply(new(true, 100, "perfect", true), report);
        Assert.False(verdict.Pass); Assert.True(verdict.Score < 100); Assert.Equal(1, report.Failed);
    }
    [Fact] public void MissingAndTruncatedEvidenceCannotPass()
    {
        foreach (var after in new[] { "{}", "{\"levels\":1,\"level_elements_truncated\":true,\"level_elements\":[{\"id\":1,\"name\":\"Bench B0\",\"elevation_m\":3.5}]}" })
        { var r = BenchmarkObjective.Evaluate("B0", "{\"levels\":0,\"level_elements\":[]}", after); Assert.False(r.Passed); Assert.True(r.Incomplete > 0); }
    }
    [Fact] public void CsvHandlesQuotesNewlinesAndDoesNotAcceptWrongRows()
    {
        var actual = CsvEvidence.Parse("name,value\r\n\"beam, A\",\"line1\nline2\"\r\n\"a\"\"b\",7\r\n");
        Assert.Equal(3, actual.Count); Assert.Equal("a\"b", actual[2][0]);
        Assert.True(CsvEvidence.ContainsRows(actual, new[] { new[] { "beam, A", "line1\nline2" }, new[] { "a\"b", "7" } }));
        Assert.False(CsvEvidence.ContainsRows(actual, new[] { new[] { "a\"b", "8" } }));
        Assert.Throws<FormatException>(() => CsvEvidence.Parse("\"broken"));
    }
    [Fact] public void CalibrationKeepsSeedEnvironmentAndFailuresSeparate()
    {
        BenchmarkResult Row(double seconds, string verdict = "✓", string seed = "seed") => new() { TaskId = "B0", Model = "A", SeedFingerprint = seed, EnvironmentKey = "machine", ComparisonKey = "config", Seconds = seconds, Score = 95, Quality = 95, Verdict = verdict, Objective = new("objective-v1", new[] { new ObjectiveCheck("size", "passed", "") }) };
        var rows = new[] { Row(10), Row(20), Row(30), Row(40), Row(50) };
        var reference = Assert.Single(BenchmarkCalibration.Create(rows)); Assert.Equal(30, reference.MedianSeconds); Assert.Equal(48, reference.P95Seconds);
        Assert.Empty(BenchmarkCalibration.Create(rows.Append(Row(60, "✗"))));
        Assert.Empty(BenchmarkCalibration.Create(rows.Take(4).Append(Row(30, seed: "different"))));
        var ungraded=new BenchmarkResult { TaskId="B0",Model="A",SeedFingerprint="seed",EnvironmentKey="machine",ComparisonKey="config",Seconds=60,Verdict="?" };
        Assert.Empty(BenchmarkCalibration.Create(rows.Append(ungraded)));
    }
}
