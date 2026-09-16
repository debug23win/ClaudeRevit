using System.Linq;
using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class CodexCliTests
{
    private const string Path = @"C:\Temp\last.txt";

    [Fact]
    public void ExecOptionsComeBeforeTheResumeSubcommand()
    {
        // The bug this guards: `codex exec resume <id> --json` is rejected by the CLI with
        // "unexpected argument '--json' found", because the options belong to `exec`.
        var args = CodexCli.BuildArgs("do the thing", null, "sess-42", Path, minimal: false);

        var json = args.IndexOf("--json");
        var resume = args.IndexOf("resume");

        Assert.True(json >= 0 && resume > json, string.Join(" ", args));
        Assert.Equal("sess-42", args[resume + 1]);
    }

    [Fact]
    public void PromptIsAlwaysLast()
    {
        var withResume = CodexCli.BuildArgs("prompt text", "gpt-6-astra", "sess-1", Path, minimal: false);
        var fresh = CodexCli.BuildArgs("prompt text", null, null, Path, minimal: false);

        Assert.Equal("prompt text", withResume.Last());
        Assert.Equal("prompt text", fresh.Last());
    }

    [Fact]
    public void FreshRunHasNoResume()
    {
        var args = CodexCli.BuildArgs("hi", null, null, Path, minimal: false);
        Assert.DoesNotContain("resume", args);
    }

    [Fact]
    public void BlankSessionIdIsNotTreatedAsAResume()
    {
        var args = CodexCli.BuildArgs("hi", null, "   ", Path, minimal: false);
        Assert.DoesNotContain("resume", args);
    }

    [Fact]
    public void SkipsTheGitRepoCheckInBothForms()
    {
        // The client work directory is never a git repo, and Codex refuses to run in one that
        // isn't — so this flag has to survive even the stripped-down retry.
        Assert.Contains("--skip-git-repo-check", CodexCli.BuildArgs("hi", null, null, Path, minimal: false));
        Assert.Contains("--skip-git-repo-check", CodexCli.BuildArgs("hi", null, null, Path, minimal: true));
    }

    [Fact]
    public void MinimalDropsTheReportingFlagsOnly()
    {
        var args = CodexCli.BuildArgs("hi", "gpt-6-astra", "sess-7", Path, minimal: true);

        Assert.DoesNotContain("--json", args);
        Assert.DoesNotContain("--output-last-message", args);
        Assert.Contains("--model", args);
        Assert.Contains("resume", args);
        Assert.Equal("hi", args.Last());
    }

    [Fact]
    public void ModelIsPassedOnlyWhenChosen()
    {
        Assert.DoesNotContain("--model", CodexCli.BuildArgs("hi", null, null, Path, minimal: false));
        Assert.DoesNotContain("--model", CodexCli.BuildArgs("hi", "", null, Path, minimal: false));

        var args = CodexCli.BuildArgs("hi", "gpt-5.6-sol", null, Path, minimal: false);
        Assert.Equal("gpt-5.6-sol", args[args.IndexOf("--model") + 1]);
    }

    [Theory]
    [InlineData("error: unexpected argument '--json' found", true)]
    [InlineData("error: unrecognized subcommand 'resume'", true)]
    [InlineData("error: the argument '--last' cannot be used with '[SESSION_ID]'", true)]
    [InlineData("Usage: codex exec [OPTIONS] [PROMPT]", true)]
    // These must NOT trigger the retry: the same command would fail again, and retrying would
    // replace a clear diagnosis with a vaguer one.
    [InlineData("stream error: You are not signed in. Run `codex login`.", false)]
    [InlineData("MCP server 'clauderevit' failed to connect: connection refused", false)]
    [InlineData("model 'gpt-9' not found", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void UsageErrorsAreTheOnlyOnesWorthRetrying(string? err, bool expected)
        => Assert.Equal(expected, CodexCli.LooksLikeUsageError(err));

    [Theory]
    [InlineData("workdir: C:\\Users\\me\\ccwork", true)]
    [InlineData("model: gpt-6-astra", true)]
    [InlineData("sandbox: read-only", true)]
    [InlineData("tokens used: 12345", true)]
    [InlineData("--------", true)]
    [InlineData("", true)]
    [InlineData("I created 4 walls on Level 1.", false)]
    [InlineData("The model: a quick note about it", false)]   // not at the start of the line
    public void BannerLinesAreDroppedFromAPlainTextAnswer(string line, bool expected)
        => Assert.Equal(expected, CodexCli.IsBannerLine(line));
}
