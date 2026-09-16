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
    }

    // The MCP server name we tell users to register; also how we spot its tool calls in the stream.
    public const string ServerName = "clauderevit";

    // The snippet a user pastes into %USERPROFILE%\.codex\config.toml (Settings shows this).
    public static string ConfigSnippet() =>
        $"[mcp_servers.{ServerName}]\n" +
        $"url = \"{McpServer.Url}\"\n" +
        $"bearer_token_env_var = \"CLAUDEREVIT_MCP_TOKEN\"\n\n" +
        $"# Then set the environment variable once (PowerShell):\n" +
        $"#   setx CLAUDEREVIT_MCP_TOKEN \"{SettingsStore.McpToken}\"";

    public static async Task<Result> RunAsync(
        string exe, string prompt, string workDir, string? model,
        Action<string> onText, Action<string> onTool, CancellationToken ct,
        string? resumeSessionId = null)
    {
        var result = new Result();

        // Revit's GUI process usually has a narrower PATH than the user's shell, so resolve to a
        // full path first (shared with the Claude Code path).
        var resolved = ClaudeCodeBackend.Resolve(string.IsNullOrWhiteSpace(exe) ? "codex" : exe);
        if (resolved == null)
        {
            result.Error =
                $"Codex CLI not found ('{exe}'). Install it (npm i -g @openai/codex), sign in once by " +
                "running 'codex', then set the full path to codex.exe/codex.cmd in Settings.";
            return result;
        }

        // --output-last-message gives the final answer through a file instead of us having to infer
        // it from the event stream, whose exact shape is not contractual. The JSONL stream is still
        // read, but only for live progress — so a schema change degrades the progress display
        // rather than losing the answer.
        var lastMsgPath = Path.Combine(Path.GetTempPath(), $"clauderevit-codex-{Guid.NewGuid():N}.txt");

        // Continue the conversation rather than starting fresh each message. A captured session id
        // is preferred over `--last`: --last means "the most recent Codex session on this machine",
        // which could belong to an unrelated project the user ran in a terminal.
        var args = new List<string> { "exec" };
        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            args.Add("resume");
            args.Add(resumeSessionId!);
        }
        args.Add("--json");
        args.Add("--output-last-message");
        args.Add(lastMsgPath);
        if (!string.IsNullOrWhiteSpace(model)) { args.Add("--model"); args.Add(model!); }
        args.Add(prompt);

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

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                ct.ThrowIfCancellationRequested();
                ParseLine(line, result, onText, onTool);
            }

            var err = await proc.StandardError.ReadToEndAsync();
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
