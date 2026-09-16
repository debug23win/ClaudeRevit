using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeRevit.Services;

// Runs the local `codex` CLI headless so OpenAI models can drive Revit THROUGH our MCP server —
// the OpenAI counterpart of ClaudeCodeBackend.
//
// Why a local CLI rather than calling the OpenAI API directly: cloud ChatGPT cannot reach an MCP
// server on 127.0.0.1, and exposing one to the internet would put model-editing (and, with code
// execution on, arbitrary C#) behind nothing but a bearer token. Codex runs on the user's machine
// and supports Streamable-HTTP MCP servers, so it reaches ours over loopback with no tunnel.
//
// MCP wiring is NOT injected per-run on purpose. Codex reads both its config and its credentials
// from CODEX_HOME, so pointing that at a temporary directory to inject an MCP config would also
// hide the user's login. Instead the server is registered once in the user's own config.toml —
// Settings shows the exact snippet — and we just launch `codex exec` against it.
//
// Written without a machine to test against: every failure path returns the CLI's own message
// verbatim rather than a guess, so a wrong flag or a missing login is visible instead of silent.
public static class CodexBackend
{
    public sealed class Result
    {
        public string Text = "";
        public string? Error;
        public long InputTokens;
        public long OutputTokens;
        public int NumTurns;
        // Captured from the stream so the next message can continue this conversation.
        public string? SessionId;
        // Set when the installed CLI rejected our flags and the run had to be retried without them:
        // the answer is real, but the user should hear that their Codex expects a different command
        // line (and that live progress is therefore missing).
        public string? FlagsRejected;
    }

    // The MCP server name we tell users to register; also how we spot its tool calls in the stream.
    public const string ServerName = "clauderevit";

    // The snippet a user pastes into %USERPROFILE%\.codex\config.toml (Settings shows this).
    //
    // The [features] line matters on older Codex builds: those only pick up stdio servers and
    // ignore a `url` entry entirely, which looks exactly like "the tools aren't there" rather than
    // like a version problem. Newer builds take the HTTP transport from `url` alone and the flag is
    // harmless, so it is always included.
    public static string ConfigSnippet() =>
        $"[features]\n" +
        $"experimental_use_rmcp_client = true   # older Codex ignores url-based servers without this\n\n" +
        $"[mcp_servers.{ServerName}]\n" +
        $"url = \"{McpServer.Url}\"\n" +
        $"bearer_token_env_var = \"CLAUDEREVIT_MCP_TOKEN\"\n\n" +
        $"# Then set the environment variable once (PowerShell) and restart Revit:\n" +
        $"#   setx CLAUDEREVIT_MCP_TOKEN \"{SettingsStore.McpToken}\"";

    public static async Task<Result> RunAsync(
        string exe, string prompt, string workDir, string? model,
        Action<string> onText, Action<string> onTool, CancellationToken ct,
        string? resumeSessionId = null)
    {
        // Revit's GUI process usually has a narrower PATH than the user's shell, so resolve to a
        // full path first (shared with the Claude Code path).
        var resolved = ClaudeCodeBackend.Resolve(string.IsNullOrWhiteSpace(exe) ? "codex" : exe);
        if (resolved == null)
            return new Result
            {
                Error =
                    $"Codex CLI not found ('{exe}'). Install it (npm i -g @openai/codex), sign in once by " +
                    "running 'codex', then set the full path to codex.exe/codex.cmd in Settings."
            };

        var lastMsgPath = Path.Combine(Path.GetTempPath(), $"clauderevit-codex-{Guid.NewGuid():N}.txt");

        var full = await RunOnceAsync(
            resolved, CodexCli.BuildArgs(prompt, model, resumeSessionId, lastMsgPath, minimal: false),
            workDir, lastMsgPath, onText, onTool, ct);

        // The exec flags have moved between Codex releases (they are parsed per subcommand, so
        // `--json` and `-o` are accepted in some positions and rejected in others), and a rejected
        // flag fails the whole run with a usage error before the model is ever asked anything. When
        // that is what happened, retry with nothing but the prompt — the answer then comes from
        // plain stdout instead of the event stream, which costs the live progress display but still
        // does the work. The rejected-flag message is kept and reported.
        if (CodexCli.LooksLikeUsageError(full.Error))
        {
            var bare = await RunOnceAsync(
                resolved, CodexCli.BuildArgs(prompt, model, resumeSessionId, lastMsgPath, minimal: true),
                workDir, lastMsgPath: null, onText, onTool, ct);
            if (string.IsNullOrEmpty(bare.Error))
            {
                bare.FlagsRejected = full.Error;
                return bare;
            }
            // Both failed: the first message is the more informative one.
            return full;
        }

        return full;
    }

    // lastMsgPath non-null means the run was launched with --output-last-message, so the final
    // answer is read from that file: it is the authoritative answer, instead of one inferred from
    // an event stream whose exact shape is not contractual. Null means the bare retry, where stdout
    // is the answer.
    private static async Task<Result> RunOnceAsync(
        string resolved, List<string> args, string workDir, string? lastMsgPath,
        Action<string> onText, Action<string> onTool, CancellationToken ct)
    {
        var result = new Result();
        var jsonStream = lastMsgPath != null;

        var viaCmd = resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                     resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        Process proc;
        try
        {
            if (viaCmd)
            {
                var cmdArgs = new List<string> { "/c", resolved };
                cmdArgs.AddRange(args);
                proc = Start("cmd.exe", cmdArgs, workDir);
            }
            else proc = Start(resolved, args, workDir);
        }
        catch (Exception ex)
        {
            result.Error = $"Can't launch Codex ('{resolved}'): {ex.Message}";
            return result;
        }

        try
        {
            proc.StandardInput.Close();   // the prompt is an argument; nothing to feed

            // Concurrent drain — same pipe-buffer deadlock as the Claude Code path.
            var errTask = proc.StandardError.ReadToEndAsync();

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                ct.ThrowIfCancellationRequested();
                if (jsonStream) ParseLine(line, result, onText, onTool);
                else
                {
                    // The bare retry has no event stream: stdout IS the answer, so take it as it
                    // comes. Codex's own banner lines are dropped — they are not part of the reply.
                    if (CodexCli.IsBannerLine(line)) continue;
                    result.Text += line + "\n";
                    onText(line + "\n");
                }
            }

            var err = await errTask;
            await proc.WaitForExitAsync(ct);

            // The authoritative answer.
            try
            {
                if (File.Exists(lastMsgPath))
                {
                    var final = (await File.ReadAllTextAsync(lastMsgPath, ct)).Trim();
                    if (final.Length > 0) result.Text = final;
                }
            }
            catch { /* fall back to whatever the stream yielded */ }

            if (proc.ExitCode != 0 && string.IsNullOrEmpty(result.Text))
                result.Error = string.IsNullOrWhiteSpace(err)
                    ? $"Codex exited with code {proc.ExitCode}."
                    : err.Trim();

            // A run that starts but never reaches Revit is the failure users hit first, and the
            // symptom (an answer with no model changes) is easy to misread as the model refusing.
            if (string.IsNullOrEmpty(result.Error) && result.NumTurns == 0 &&
                !string.IsNullOrEmpty(result.Text))
                result.Error = null;   // plain text answers are legitimate; don't invent an error
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        catch (Exception ex) { result.Error = ex.Message; }
        finally
        {
            try { if (File.Exists(lastMsgPath)) File.Delete(lastMsgPath); } catch { }
        }

        return result;
    }

    // Best-effort progress parsing. The JSONL event shape is not a stable contract, so every field
    // is probed defensively and an unrecognised line is simply ignored.
    private static void ParseLine(string line, Result result, Action<string> onText, Action<string> onTool)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

            // Tool/function calls: surface the tool name so the pane shows progress. Codex prefixes
            // MCP tools with the server name, so strip it for readability.
            if (type.Contains("tool", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("function", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("mcp", StringComparison.OrdinalIgnoreCase))
            {
                var name = FirstString(root, "name", "tool", "tool_name");
                if (!string.IsNullOrEmpty(name))
                {
                    var shown = name!.StartsWith(ServerName + "__", StringComparison.Ordinal)
                        ? name.Substring(ServerName.Length + 2)
                        : name;
                    result.NumTurns++;
                    onTool(shown);
                }
            }

            // Assistant text as it streams.
            var text = FirstString(root, "text", "delta", "content", "message");
            if (!string.IsNullOrEmpty(text) && type.IndexOf("error", StringComparison.OrdinalIgnoreCase) < 0)
            {
                result.Text += text;
                onText(text!);
            }

            // Session id, wherever it appears — needed to continue the conversation next message.
            var sid = FirstString(root, "session_id", "sessionId", "conversation_id", "thread_id");
            if (!string.IsNullOrEmpty(sid)) result.SessionId = sid;

            // Token usage, wherever it appears.
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                if (u.TryGetProperty("input_tokens", out var it) && it.TryGetInt64(out var itv)) result.InputTokens += itv;
                if (u.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var otv)) result.OutputTokens += otv;
            }
        }
        catch { /* not JSON we understand */ }
    }

    private static string? FirstString(JsonElement root, params string[] names)
    {
        foreach (var n in names)
            if (root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    private static Process Start(string file, List<string> args, string workDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // Same reason as the Claude path: without this the prompt is encoded with the OS default
            // (CP1251 on a Russian Windows) and non-ASCII requests arrive as mojibake.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }
}
