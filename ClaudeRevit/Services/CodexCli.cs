using System;
using System.Collections.Generic;

namespace ClaudeRevit.Services;

// How to talk to the `codex` CLI: the command line, and how to read what it says back.
//
// Split out of CodexBackend because that file cannot be tested — it starts processes and reaches
// Revit settings — while this part is exactly where the mistakes are, and is pure string work.
internal static class CodexCli
{
    // `--json` + `--output-last-message` are what make a run observable; `minimal` drops every
    // optional flag for the retry that runs when the installed CLI rejects them.
    //
    // Order is deliberate: the exec options come FIRST and `resume <id>` last. Codex parses these
    // options on the `exec` command, not on its `resume` subcommand, so `exec resume <id> --json`
    // is rejected with "unexpected argument '--json' found" while `exec --json resume <id>` works.
    //
    // --skip-git-repo-check is not optional for us: Codex refuses to run in a directory that isn't
    // a git repo, and the client work directory (AppData\...\ccwork) never is one.
    public static List<string> BuildArgs(
        string prompt, string? model, string? resumeSessionId, string lastMsgPath, bool minimal)
    {
        var args = new List<string> { "exec", "--skip-git-repo-check" };
        if (!minimal)
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
