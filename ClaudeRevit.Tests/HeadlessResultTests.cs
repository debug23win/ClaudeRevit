using System.Text.Json;
using ModPlus.Headless;
using Xunit;

namespace ClaudeRevit.Tests;

public class HeadlessResultTests
{
    [Fact]
    public void UnicodeEscapingCannotOverflowTheEnvelope()
    {
        var raw = "{\"preview\":true,\"committed\":false,\"results\":\"" + new string('Ж', 60_000) + "\"}";
        var output = HeadlessResultLimits.Compact(raw);
        Assert.True(output.Length <= 30_000);
        using var json = JsonDocument.Parse(output);
        Assert.False(json.RootElement.GetProperty("committed").GetBoolean());
        Assert.NotEmpty(json.RootElement.GetProperty("head").GetString()!);
    }
    [Fact]
    public void LargePreviewKeepsRollbackStatusAndStaysBelowTheHostCap()
    {
        var raw=JsonSerializer.Serialize(new { preview=true,committed=false,results=new string('\\',60000) });
        var output=HeadlessResultLimits.Compact(raw);
        Assert.True(output.Length<30000);
        using var json=JsonDocument.Parse(output);
        Assert.True(json.RootElement.GetProperty("preview").GetBoolean());
        Assert.False(json.RootElement.GetProperty("committed").GetBoolean());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
    }
    [Fact]
    public void OversizedErrorCannotBecomeSuccess()
    {
        var output=HeadlessResultLimits.Compact(JsonSerializer.Serialize(new {ok=false,error=new string('x',100000)}));
        Assert.True(output.Length<30000);
        using var json=JsonDocument.Parse(output);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(json.RootElement.GetProperty("error").GetString()!.Length<=2001);
    }
    [Fact]
    public void SmallResultsAreUnchanged()
    {
        const string raw="{\"preview\":true,\"committed\":false}";
        Assert.Equal(raw,HeadlessResultLimits.Compact(raw));
    }
}
