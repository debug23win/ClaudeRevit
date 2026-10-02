using System.Text.Json;
using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class BenchmarkTests
{
    [Fact]
    public void SpeedRewardsQualityWithoutIndependentPointsForIncorrectResults()
    {
        Assert.Equal(90, BenchmarkScoring.Calculate(new(true, 90, "", true), 60, 120)!.Total);
        Assert.Equal(81, BenchmarkScoring.Calculate(new(true, 90, "", true), 240, 120)!.Total);
        Assert.Equal(0, BenchmarkScoring.Calculate(new(false, 0, "", true), .1, 120)!.Total);
        Assert.True(BenchmarkScoring.Calculate(new(true, 100, "", true), 10000, 120)!.Total >
            BenchmarkScoring.Calculate(new(false, 20, "", true), 1, 120)!.Total);
        Assert.Null(BenchmarkScoring.Calculate(new(false, 0, "offline", false), 30, 120));
    }
    [Theory]
    [InlineData(double.NaN, 120)] [InlineData(-1, 120)] [InlineData(1, 0)] [InlineData(1, double.PositiveInfinity)]
    public void InvalidTimingCannotProducePoints(double seconds, double reference) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkScoring.Calculate(new(true, 100, "", true), seconds, reference));
    [Fact]
    public void FasterRunsHaveMonotonicallyHigherScoreAndNeverExceedQuality()
    {
        var scores = new[] { 0, 10, 120, 180, 500, 10000 }.Select(s => BenchmarkScoring.Calculate(new(true, 83, "", true), s, 120)!).ToArray();
        Assert.All(scores, s => Assert.InRange(s.Total, 66.4, 83));
        for (var i = 1; i < scores.Length; i++) Assert.True(scores[i].Total <= scores[i - 1].Total);
    }
    [Fact]
    public void DocumentAndNestedPrerequisitesAreSkippedBeforeModelling()
    {
        var f2 = BenchmarkTasks.All.Single(t => t.Id == "F2");
        Assert.NotNull(BenchmarkEligibility.SkipReason(f2, "{\"is_family_document\":false}"));
        Assert.NotNull(BenchmarkEligibility.SkipReason(f2, "{\"is_family_document\":true,\"nested_seed_types\":[]}"));
        Assert.Null(BenchmarkEligibility.SkipReason(f2, "{\"is_family_document\":true,\"nested_seed_types\":[{}]}"));
        Assert.NotNull(BenchmarkEligibility.SkipReason(BenchmarkTasks.All[0], "{\"is_family_document\":true}"));
        var f3 = BenchmarkTasks.All.Single(t => t.Id == "F3");
        Assert.NotNull(BenchmarkEligibility.SkipReason(f3, "{\"is_family_document\":true,\"nested_seed_types\":[{}],\"family_structure\":{\"nodes\":[{\"depth\":1}]}}"));
        Assert.Null(BenchmarkEligibility.SkipReason(f3, "{\"is_family_document\":true,\"nested_seed_types\":[{}],\"family_structure\":{\"nodes\":[{\"depth\":2}]}}"));
        Assert.Throws<InvalidOperationException>(() => BenchmarkEligibility.SkipReason(f2, "{\"probe_error\":\"unavailable\"}"));
    }
    [Fact]
    public void ChangedAndNewElementsBeyondFirstSampleRemainVisibleToJudge()
    {
        var before = Enumerable.Range(1, 400).Select(id => new { id, size = 100 }).ToArray();
        var after = before.Where(e => e.id != 2).Select(e => e.id == 350 ? new { e.id, size = 200 } : e).Append(new { id = 401, size = 300 });
        var pair = BenchmarkGrading.SummarizePair(JsonSerializer.Serialize(new { elements = before }), JsonSerializer.Serialize(new { elements = after }));
        using var b = JsonDocument.Parse(pair.Before); using var a = JsonDocument.Parse(pair.After);
        Assert.Equal(new[] { 350, 401 }, a.RootElement.GetProperty("elements").GetProperty("sample").EnumerateArray().Select(e => e.GetProperty("id").GetInt32()));
        Assert.Equal(new[] { 2, 350 }, b.RootElement.GetProperty("elements").GetProperty("sample").EnumerateArray().Select(e => e.GetProperty("id").GetInt32()));
        Assert.Equal(398, a.RootElement.GetProperty("elements_unchanged_omitted").GetInt32());
    }
    [Fact]
    public void TaskReferencesAreStableAndFamilyFlexScenariosAreIndependent()
    {
        Assert.Equal(29, BenchmarkTasks.All.Count);
        Assert.Equal(29, BenchmarkTasks.All.Select(t => t.Id).Distinct().Count());
        Assert.All(BenchmarkTasks.All, t => Assert.True(t.ReferenceSeconds > 0));
        var families = BenchmarkTasks.All.Where(t => t.FamilyDocument).ToArray();
        Assert.Equal(5, families.Length);
        Assert.All(families, t => { Assert.NotNull(t.FlexScenarios); using var doc = JsonDocument.Parse(t.FlexScenarios!); Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind); });
    }
    [Fact]
    public void AutoCliSelectionPrefersActualVersionOverPathOrder()
    {
        Assert.Equal("desktop-new", CodexBackend.SelectExecutable(new[] { ("path-old", (Version?)new Version(0,154,0)), ("desktop-new", new Version(0,159,2)), ("unknown", (Version?)null) }));
        Assert.Equal("path-first", CodexBackend.SelectExecutable(new[] { ("path-first", (Version?)new Version(0,159,2)), ("same", new Version(0,159,2)) }));
        Assert.Null(CodexBackend.SelectExecutable(Array.Empty<(string, Version?)>()));
    }
}
