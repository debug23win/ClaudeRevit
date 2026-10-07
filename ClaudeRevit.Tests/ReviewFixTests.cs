using System.IO;
using ClaudeRevit.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ClaudeRevit.Tests;

// Regression tests for the review fixes that do not need Revit to run.
public class ReviewFixTests
{
    [Fact]
    public void ToolResultsComeBeforeImagesInAUserTurn()
    {
        // Two calls in one round, the first an export: [result, image, result] was a 400.
        var blocks = new List<ChatBlock>
        {
            new ChatToolResultBlock("a", "{}", false),
            new ChatImageBlock("image/png", "AAAA"),
            new ChatToolResultBlock("b", "{}", false),
            new ChatTextBlock("[note]"),
        };
        var ordered = ChatBlockOrder.ToolResultsFirst(blocks);
        Assert.IsType<ChatToolResultBlock>(ordered[0]);
        Assert.IsType<ChatToolResultBlock>(ordered[1]);
        Assert.Equal("a", ((ChatToolResultBlock)ordered[0]).ToolUseId);
        Assert.Equal("b", ((ChatToolResultBlock)ordered[1]).ToolUseId);
        Assert.IsType<ChatImageBlock>(ordered[2]);
        Assert.IsType<ChatTextBlock>(ordered[3]);
    }

    [Fact]
    public void ATurnWithoutToolResultsKeepsItsOrder()
    {
        var blocks = new List<ChatBlock> { new ChatTextBlock("q"), new ChatImageBlock("image/png", "AA") };
        Assert.Equal(blocks, ChatBlockOrder.ToolResultsFirst(blocks));
    }

    [Theory]
    [InlineData(new[] { "get_ful", "l", "_result" }, "get_full_result")]   // fragments, one a suffix of the name so far
    [InlineData(new[] { "create_wall", "create_wall" }, "create_wall")]     // repeated whole
    [InlineData(new[] { "crea", "create_wall" }, "create_wall")]            // cumulative
    [InlineData(new[] { "set_", "param", "eter" }, "set_parameter")]
    [InlineData(new[] { "", "tag", "" }, "tag")]
    public void StreamedToolNamesAreReassembled(string[] pieces, string expected)
    {
        var name = "";
        foreach (var piece in pieces) name = ToolCallName.Merge(name, piece);
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("opus", "claude-opus-4-8", true)]
    [InlineData("opus-5", "claude-opus-5", true)]
    [InlineData("opus-5", "claude-opus-4-8", false)]
    [InlineData("claude-opus-5", "claude-opus-5", true)]
    [InlineData("gpt-5.6-sol", "gpt-5.6-sol", true)]
    [InlineData("sonnet", "claude-opus-5", false)]
    [InlineData("op", "claude-opus-5", false)]   // whole parts only, not substrings
    public void RequestedModelMatchesByWholeParts(string requested, string reported, bool expected)
        => Assert.Equal(expected, McpClientState.ModelMatches(requested, reported));

    [Fact]
    public void AnAliasRequestIsSatisfiedAndTheDirectiveStops()
    {
        var state = new McpClientState("claude-code", "1.0");
        state.RequestModel("opus");
        Assert.NotNull(state.TakeDirective());
        state.ReportModel("claude-opus-4-8");
        Assert.Null(state.RequestedModel);
        Assert.Null(state.TakeDirective());
    }

    [Fact]
    public void AnIgnoredModelRequestIsNotRepeatedForever()
    {
        var state = new McpClientState("codex", "1.0");
        state.RequestModel("gpt-6-astra");
        var sent = 0;
        for (var i = 0; i < 20; i++) if (state.TakeDirective() != null) sent++;
        Assert.Equal(McpClientState.MaxModelDirectives, sent);
        Assert.Equal("gpt-6-astra", state.RequestedModel); // still visible in Settings
    }

    [Fact]
    public void OldAttachmentCopiesArePrunedButRecentAndForeignFoldersAreKept()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClaudeRevit-prune-" + Guid.NewGuid().ToString("N"));
        try
        {
            string Make(string name, DateTime written)
            {
                var dir = Path.Combine(root, name); Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "a.pdf"); File.WriteAllText(file, "x");
                File.SetLastWriteTimeUtc(file, written); return dir;
            }
            var old = Make(Guid.NewGuid().ToString("N"), DateTime.UtcNow.AddDays(-40));
            var recent = Make(Guid.NewGuid().ToString("N"), DateTime.UtcNow.AddDays(-2));
            var foreign = Make("not-a-guid", DateTime.UtcNow.AddDays(-400));

            Assert.Equal(1, AttachmentStore.PruneOlderThan(TimeSpan.FromDays(30), root));
            Assert.False(Directory.Exists(old));
            Assert.True(Directory.Exists(recent));
            Assert.True(Directory.Exists(foreign));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ARecoveredCodexErrorDoesNotFailACompletedTurn()
    {
        var result = new CodexBackend.Result();
        CodexBackend.ParseLine("""{"type":"error","message":"stream disconnected - retrying"}""", result, _ => { }, _ => { });
        CodexBackend.ParseLine("""{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}""", result, _ => { }, _ => { });
        Assert.True(result.Completed);
        Assert.Null(result.Error);
        Assert.Equal("stream disconnected - retrying", result.LastErrorEvent);
    }

    [Fact]
    public void CodexUsageIsFoundUnderTheLocalDateFolder()
    {
        // UUIDv7 creation time 2026-10-04 ~21:30 UTC is already the next day in UTC+3.
        const string id = "0199b13f-6c00-7f32-af26-ad32c1af1dac";
        var created = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(id.Replace("-", "")[..12], 16)).UtcDateTime;
        var folderDay = created.AddDays(1); // simulate a folder named by a date other than the UTC one
        var home = Path.Combine(Path.GetTempPath(), "ClaudeRevit-usage-local-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = Path.Combine(home, "sessions", folderDay.ToString("yyyy"), folderDay.ToString("MM"), folderDay.ToString("dd"));
            Directory.CreateDirectory(session);
            File.WriteAllLines(Path.Combine(session, "rollout-" + id + ".jsonl"), [
                "{\"type\":\"token_usage_record\",\"timestamp\":\"" + created.AddMinutes(5).ToString("o") + "\",\"payload\":{\"turn_token_usage\":{\"input_tokens\":7,\"output_tokens\":3}}}"]);
            var usage = CodexUsage.ReadTurn(id, created, home);
            Assert.NotNull(usage);
            Assert.Equal(7, usage!.Input);
        }
        finally { if (Directory.Exists(home)) Directory.Delete(home, true); }
    }

    [Fact]
    public void TheStdioBridgeCompilesUnderWindowsPowerShellsCSharp5()
    {
        // Add-Type in Windows PowerShell 5.1 compiles with the C# 5 compiler, so newer syntax in
        // the bridge only fails on the user's machine. Parse it as C# 5 here.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "scripts", "revit-mcp-bridge.cs");
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path),
            new CSharpParseOptions(LanguageVersion.CSharp5));
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }
}
