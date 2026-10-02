using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class AdvancedBimTests
{
    [Fact]
    public void BenchmarkDefaultsToSubscriptionAndKeepsIndependentModelsAndEfforts()
    {
        var defaults = new BenchmarkExecution();
        Assert.Equal("codex", defaults.Backend);
        Assert.Equal("codex", defaults.Tag);
        Assert.Equal("codex", defaults.Agent!.Agent);
        var task = new BenchmarkExecution("claudecode", "sonnet", "medium");
        var judge = new BenchmarkExecution("codex", "gpt-test", "high");
        Assert.Equal(new McpAgentSelection("claudecode", "sonnet", "medium"), task.Agent);
        Assert.Equal("codex:gpt-test", judge.Tag);
        Assert.Equal("high", judge.Agent!.Effort);
        Assert.Null(new BenchmarkExecution("api", "sonnet-5").Agent);
    }
    [Theory]
    [InlineData("codex")]
    [InlineData("codex:gpt-test")]
    [InlineData("ClaudeCode")]
    public void ExplicitApiCannotSilentlyRouteToSubscription(string model) =>
        Assert.Throws<ArgumentException>(() => new BenchmarkExecution("api", model).Tag);
    [Fact]
    public void CleanupProtectsExistingElementsFromDeletionCascade()
    {
        var baseline = new HashSet<long> { 1, 2, 3 };
        BenchmarkCleanup.ValidateCascade(baseline, new long[] { 4, 5 }, new long[] { 4, 5, 6 });
        Assert.Throws<InvalidOperationException>(() => BenchmarkCleanup.ValidateCascade(baseline, new long[] { 4 }, new long[] { 4, 2 }));
        Assert.Throws<InvalidOperationException>(() => BenchmarkCleanup.ValidateCascade(baseline, new long[] { 4, 5 }, new long[] { 4 }));
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"pass\":\"true\",\"score\":100,\"reason\":\"claim\"}")]
    [InlineData("Rate limit reached")]
    public void InvalidJudgeRepliesAreUngraded(string raw) => Assert.False(BenchmarkGrading.Parse(raw).Graded);
    [Fact]
    public void ProbeSummaryRetainsTotalsBeyondSampleAndFieldsAtTheEnd()
    {
        var raw = System.Text.Json.JsonSerializer.Serialize(new { walls = 300, wall_lengths_m = Enumerable.Repeat(10.0, 300), rebar = 42 });
        using var doc = System.Text.Json.JsonDocument.Parse(BenchmarkGrading.SummarizeProbe(raw));
        Assert.Equal(42, doc.RootElement.GetProperty("rebar").GetInt32());
        Assert.Equal(3000, doc.RootElement.GetProperty("wall_lengths_m").GetProperty("total").GetDouble());
        Assert.True(doc.RootElement.GetProperty("wall_lengths_m").GetProperty("sample_truncated").GetBoolean());
        Assert.Throws<InvalidOperationException>(() => BenchmarkGrading.SummarizeProbe("{\"probe_error\":\"offline\"}"));
    }
    [Fact]
    public void GradientHandlesConstantAndClippedRangesAndRejectsInvalidData()
    {
        Assert.Equal(.5, ParameterColors.Fraction(10, 10, 10));
        Assert.Equal(0, ParameterColors.Fraction(-1, 0, 100));
        Assert.Equal(1, ParameterColors.Fraction(101, 0, 100));
        Assert.Throws<ArgumentException>(() => ParameterColors.Fraction(double.NaN, 0, 1));
        Assert.Throws<ArgumentException>(() => ParameterColors.Fraction(2, 5, 1));
        Assert.Equal("#808080", Rgb.Interpolate(Rgb.Parse("#000000"), Rgb.Parse("#ffffff"), .5).Hex);
        Assert.Throws<ArgumentException>(() => Rgb.Parse("#FF00"));
    }
    [Fact]
    public void ReferencesExplicitlyDistinguishPartialPluginPortsAndMissingEirInputs()
    {
        var commands = BimStarterKnowledge.Commands;
        Assert.Equal(63, commands.Count);
        Assert.Equal(63, commands.Select(c => c.GetProperty("id").GetString()).Distinct().Count());
        Assert.Contains(commands, c => c.GetProperty("coverage").GetString() == "partial_workflow");
        Assert.Contains(commands, c => c.GetProperty("coverage").GetString() == "interactive_plugin");
        var data = StandardWorkflows.Data;
        Assert.Contains(data.GetProperty("limitations").EnumerateArray(), e => e.GetString()!.Contains("Samolet FOP"));
        Assert.Contains(data.GetProperty("rules").EnumerateArray(), r => r.GetProperty("profile").GetString() == "Samolet" && r.GetProperty("rule").GetString() == "9.1.10–9.1.13");
    }
}
