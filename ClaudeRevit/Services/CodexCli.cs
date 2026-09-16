using System;
using System.Collections.Generic;

namespace ClaudeRevit.Services;

// How to talk to the `codex` CLI: the command line, and how to read what it says back.
//
// Split out of CodexBackend because that file cannot be tested — it starts processes and reaches
// Revit settings — while this part is exactly where the mistakes are, and is pure string work.
internal static class CodexCli
{
    // How much of the command line to use. Codex has added and moved these flags across releases,
    // so a rejected flag is answered by stepping down a level rather than by failing the run.
    public enum Level
    {
        Full = 0,       // everything: the git-repo-check waiver plus the reporting flags
        NoGitWaiver,    // --skip-git-repo-check is not known to this build
        Bare            // no --json / --output-last-message either: stdout is the answer
    }

    // `--json` + `--output-last-message` are what make a run observable, so they are preferred.
    //
    // Order is deliberate: the exec options come FIRST and `resume <id>` last. Codex parses these
    // options on the `exec` command, not on its `resume` subcommand, so `exec resume <id> --json`
    // is rejected with "unexpected argument '--json' found" while `exec --json resume <id>` works.
    //
    // --skip-git-repo-check matters because Codex refuses to run in a directory that isn't a git
    // repo, and the client work directory (AppData\...\ccwork) never is one. Builds old enough not
    // to know the flag also predate that check, which is why dropping it is a sane first step down.
    public static List<string> BuildArgs(
        string prompt, string? model, string? resumeSessionId, string lastMsgPath, Level level)
    {
        var args = new List<string> { "exec" };
        if (level == Level.Full) args.Add("--skip-git-repo-check");
        if (level != Level.Bare)
        {
            args.Add("--json");
            args.Add("--output-last-message");
            args.Add(lastMsgPath);
        }
        if (!string.IsNullOrWhiteSpace(model)) { args.Add("--model"); args.Add(model!); }

        // Continue the conversation rather than starting fresh each message. A captured session id
        // is preferred over `--last`: --last means "the most recent Codex session on this machine",
        // which could belong to an unrelated project the user ran in a terminal.
        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            args.Add("resume");
            args.Add(resumeSessionId!);
        }
        args.Add(prompt);
        return args;
    }

    // A command-line parse failure, as opposed to a run that failed: the CLI never reached the
    // model, so retrying with fewer flags is worth a try. Anything else (not signed in, unknown
    // model, MCP server unreachable) would fail again identically, and a retry would only hide it.
    public static bool LooksLikeUsageError(string? err) =>
        !string.IsNullOrWhiteSpace(err) &&
        (err!.Contains("unexpected argument", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("cannot be used with", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("Usage: codex", StringComparison.OrdinalIgnoreCase));

    // Whether a resolved path is actually the Codex CLI, as opposed to some other agent CLI that the
    // executable search happened to land on. Worth checking: launching the wrong program produces a
    // complaint about our flags, which reads as "Codex is broken" and sends the user off fixing an
    // install that was never the problem.
    public static bool LooksLikeCodexBinary(string path)
    {
        // Both separators are handled explicitly rather than through Path.GetFileName: these paths
        // are Windows paths, and on any other OS that method would treat a backslash as an ordinary
        // character and hand back the whole string.
        var cut = path.LastIndexOfAny(new[] { '\\', '/' });
        var name = cut >= 0 ? path.Substring(cut + 1) : path;
        return name.StartsWith("codex", StringComparison.OrdinalIgnoreCase);
    }

    // The legacy Node build of Codex, which is worth naming precisely because no amount of flag
    // juggling can rescue it: it has no `codex exec` and no MCP client at all, so it can never
    // reach the Revit tools. The tell is the wording — "unknown option" is commander (Node), while
    // the current Rust build parses with clap and says "unexpected argument ... found".
    public static bool LooksLikeLegacyNodeCli(string? err) =>
        !string.IsNullOrWhiteSpace(err) &&
        (err!.Contains("unknown option", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("unknown command", StringComparison.OrdinalIgnoreCase));

    public const string LegacyCliAdvice =
        "Your `codex` is the old Node build of the CLI. It has no `codex exec` and no MCP client at " +
        "all, so it can't reach the Revit tools no matter how it's called — MCP arrived with the " +
        "Rust rewrite. Update it (npm i -g @openai/codex@latest), run `codex` once to sign in, and " +
        "check `codex --version`. If Settings points at a specific codex.exe, make sure it isn't an " +
        "old copy left behind by the previous install.";

    // Codex's human-readable output opens with a metadata block (workdir, model, provider, approval
    // and sandbox settings, session id) and closes with a token count. Only the retry path needs
    // this — with --json the answer comes from the event stream instead.
    public static bool IsBannerLine(string line)
    {
        var t = line.TrimStart();
        if (t.Length == 0 || t.StartsWith("--------", StringComparison.Ordinal)) return true;
        foreach (var prefix in BannerPrefixes)
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly string[] BannerPrefixes =
    {
        "workdir:", "model:", "provider:", "approval:", "sandbox:",
        "reasoning ", "session id:", "tokens used:"
    };
}
