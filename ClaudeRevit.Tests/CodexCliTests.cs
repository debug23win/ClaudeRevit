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
        var args = CodexCli.BuildArgs("do the thing", null, "sess-42", Path, CodexCli.Level.Full);

        var json = args.IndexOf("--json");
        var resume = args.IndexOf("resume");

        Assert.True(json >= 0 && resume > json, string.Join(" ", args));
        Assert.Equal("sess-42", args[resume + 1]);
    }

    [Fact]
    public void PromptIsAlwaysLast()
    {
        var withResume = CodexCli.BuildArgs("prompt text", "gpt-6-astra", "sess-1", Path, CodexCli.Level.Full);
        var fresh = CodexCli.BuildArgs("prompt text", null, null, Path, CodexCli.Level.Full);

        Assert.Equal("prompt text", withResume.Last());
        Assert.Equal("prompt text", fresh.Last());
    }

    [Fact]
    public void FreshRunHasNoResume()
    {
        var args = CodexCli.BuildArgs("hi", null, null, Path, CodexCli.Level.Full);
        Assert.DoesNotContain("resume", args);
    }

    [Fact]
    public void BlankSessionIdIsNotTreatedAsAResume()
    {
        var args = CodexCli.BuildArgs("hi", null, "   ", Path, CodexCli.Level.Full);
        Assert.DoesNotContain("resume", args);
    }

    [Fact]
    public void TheFullFormWaivesTheGitRepoCheck()
    {
        // The client work directory is never a git repo, and Codex refuses to run in one that
        // isn't — so the preferred form always waives that check.
        Assert.Contains("--skip-git-repo-check", CodexCli.BuildArgs("hi", null, null, Path, CodexCli.Level.Full));
    }

    [Fact]
    public void EachLevelDropsOnlyItsOwnFlags()
    {
        var noWaiver = CodexCli.BuildArgs("hi", "gpt-6-astra", "sess-7", Path, CodexCli.Level.NoGitWaiver);
        var bare = CodexCli.BuildArgs("hi", "gpt-6-astra", "sess-7", Path, CodexCli.Level.Bare);

        // Step one: only the flag the CLI didn't know goes; reporting survives.
        Assert.DoesNotContain("--skip-git-repo-check", noWaiver);
        Assert.Contains("--json", noWaiver);
        Assert.Contains("--output-last-message", noWaiver);

        // Step two: reporting goes too, but the model and the conversation must not.
        Assert.DoesNotContain("--json", bare);
        Assert.DoesNotContain("--output-last-message", bare);
        Assert.DoesNotContain("--skip-git-repo-check", bare);
        Assert.Contains("--model", bare);
        Assert.Contains("resume", bare);
        Assert.Equal("hi", bare.Last());
    }

    [Theory]
    // commander (the old Node build) — no `codex exec`, no MCP client, so no retry can help.
    [InlineData("error: unknown option '--skip-git-repo-check'", true)]
    [InlineData("error: unknown command 'exec'", true)]
    // clap (the current Rust build) — a flag in the wrong place, worth stepping down for.
    [InlineData("error: unexpected argument '--json' found", false)]
    [InlineData("stream error: You are not signed in.", false)]
    [InlineData(null, false)]
    public void TheOldNodeCliIsRecognisedByHowItWords(string? err, bool expected)
        => Assert.Equal(expected, CodexCli.LooksLikeLegacyNodeCli(err));

    [Fact]
    public void ModelIsPassedOnlyWhenChosen()
    {
        Assert.DoesNotContain("--model", CodexCli.BuildArgs("hi", null, null, Path, CodexCli.Level.Full));
        Assert.DoesNotContain("--model", CodexCli.BuildArgs("hi", "", null, Path, CodexCli.Level.Full));

        var args = CodexCli.BuildArgs("hi", "gpt-5.6-sol", null, Path, CodexCli.Level.Full);
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

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Roaming\npm\codex.cmd", true)]
    [InlineData(@"C:\Users\me\.codex\bin\codex.exe", true)]
    [InlineData("/usr/local/bin/codex", true)]
    // The one that actually happened: with Claude Desktop installed and no Codex, the executable
    // search answered "codex" with claude.exe, which then complained about our flags.
    [InlineData(@"C:\Users\me\AppData\Local\Packages\Claude_abc\...\claude.exe", false)]
    [InlineData(@"C:\Program Files\nodejs\node.exe", false)]
    public void TheResolvedBinaryHasToBeCodex(string path, bool expected)
        => Assert.Equal(expected, CodexCli.LooksLikeCodexBinary(path));
}
