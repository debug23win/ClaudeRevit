using System.Text.Json;
using ClaudeRevit.Tools;
using Xunit;

namespace ClaudeRevit.Tests;

// The required-input readers every tool now uses instead of input["x"].GetDouble() & co.
// What matters is the message: it is what the model reads to fix its next call.
public class ToolInputTests
{
    private static Dictionary<string, JsonElement> In(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void AMissingParameterIsNamed()
    {
        var ex = Assert.Throws<ToolInputException>(() => ToolInput.RequiredDouble(In("{}"), "start_x"));
        Assert.Contains("'start_x'", ex.Message);
        Assert.Contains("Missing", ex.Message);
    }

    [Fact]
    public void AWrongTypeSaysWhatArrived()
    {
        var ex = Assert.Throws<ToolInputException>(() => ToolInput.RequiredDouble(In("""{"height_mm":"tall"}"""), "height_mm"));
        Assert.Contains("'height_mm'", ex.Message);
        Assert.Contains("\"tall\"", ex.Message);
    }

    [Fact]
    public void NumbersSentAsStringsAreAccepted()
    {
        Assert.Equal(3000, ToolInput.RequiredDouble(In("""{"h":"3000"}"""), "h"));
        Assert.Equal(12345678901L, ToolInput.RequiredLong(In("""{"id":"12345678901"}"""), "id"));
        Assert.Equal(4, ToolInput.RequiredInt(In("""{"n":"4"}"""), "n"));
    }

    [Fact]
    public void WholeNumbersWrittenWithADecimalPointAreIntegers()
    {
        Assert.Equal(3, ToolInput.RequiredInt(In("""{"n":3.0}"""), "n"));
        Assert.Equal(42L, ToolInput.RequiredLong(In("""{"id":42.0}"""), "id"));
    }

    [Fact]
    public void AFractionIsNotSilentlyRoundedIntoACount()
        => Assert.Throws<ToolInputException>(() => ToolInput.RequiredInt(In("""{"n":2.5}"""), "n"));

    [Fact]
    public void RequiredTextKeepsGetStringSemanticsForNull()
    {
        // Call sites written for input[x].GetString() treat null as "not given"; that must hold.
        Assert.Null(ToolInput.RequiredText(In("""{"name":null}"""), "name"));
        Assert.Equal("", ToolInput.RequiredText(In("""{"name":""}"""), "name"));
        Assert.Equal("Бетон", ToolInput.RequiredText(In("""{"name":"Бетон"}"""), "name"));
        Assert.Throws<ToolInputException>(() => ToolInput.RequiredText(In("""{"name":5}"""), "name"));
        Assert.Throws<ToolInputException>(() => ToolInput.RequiredText(In("{}"), "name"));
    }

    [Fact]
    public void ASingleValueWhereAListIsExpectedIsNamed()
    {
        var ex = Assert.Throws<ToolInputException>(() => ToolInput.RequiredArray(In("""{"element_ids":123}"""), "element_ids"));
        Assert.Contains("'element_ids'", ex.Message);
        Assert.Contains("array", ex.Message);
        Assert.Equal(2, ToolInput.RequiredArray(In("""{"element_ids":[1,2]}"""), "element_ids").GetArrayLength());
    }
}
