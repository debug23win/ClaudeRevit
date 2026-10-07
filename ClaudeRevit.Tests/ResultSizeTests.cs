using System.Text.Json;
using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class ResultSizeTests
{
    [Theory]
    [InlineData(32.808398950131235, 32.8084)]
    [InlineData(0.000012345678, 0.00001234568)]
    [InlineData(123456789.123, 123456789)]
    [InlineData(-3.14159265358979, -3.141593)]
    [InlineData(2.5, 2.5)]
    [InlineData(0, 0)]
    public void FractionsKeepSevenSignificantDigits(double input, double expected)
        => Assert.Equal(expected, ToolResult.Round(input), 12);

    [Fact]
    public void CompleteRoundsNestedNumbersButNeverIntegers()
    {
        var json = ToolResult.Complete("""{"id":123456789012,"count":3,"pt":{"x":32.808398950131235,"y":[1.23456789012,7]}}""");
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        Assert.Equal(123456789012, r.GetProperty("id").GetInt64());
        Assert.Equal(3, r.GetProperty("count").GetInt32());
        Assert.Equal(32.8084, r.GetProperty("pt").GetProperty("x").GetDouble(), 10);
        Assert.Equal(1.234568, r.GetProperty("pt").GetProperty("y")[0].GetDouble(), 10);
        Assert.Equal(7, r.GetProperty("pt").GetProperty("y")[1].GetInt32());
    }

    [Fact]
    public void SmallResultsPassThroughUnchanged()
    {
        const string small = """{"ok":true,"value":1}""";
        Assert.Same(small, ToolResultCap.Apply(small, "t"));
    }

    [Fact]
    public void OversizedResultsAreArchivedAndSummarised()
    {
        var big = "{\"ok\":true,\"items\":\"" + new string('x', ToolResultCap.MaxChars + 5000) + "\"}";
        using var d = JsonDocument.Parse(ToolResultCap.Apply(big, "list_everything"));
        var r = d.RootElement;
        Assert.True(r.GetProperty("truncated").GetBoolean());
        Assert.Equal(big.Length, r.GetProperty("total_chars").GetInt32());
        var id = r.GetProperty("full_result_id").GetString()!;
        Assert.Equal(big, ToolResultArchive.Lookup(id));
        Assert.Contains("list_everything", r.GetProperty("note").GetString());
    }
}
