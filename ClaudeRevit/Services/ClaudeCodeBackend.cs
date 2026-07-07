using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeRevit.Services;

// EXPERIMENTAL: runs the local `claude` CLI (Claude Code) headless as the "brain", so the in-Revit
// chat pane can be used while the work runs on the user's Claude Pro/Max SUBSCRIPTION instead of
// the pay-per-token API. Claude Code reaches the Revit tools through our own MCP server (which
// executes them on Revit's UI thread). We stream `--output-format stream-json` back into the pane.
//
// Requires the user to have installed Claude Code and run `claude login` once. Built without a live
// machine to test against — needs field validation (esp. how `claude` resolves on PATH: a native
// install is a real .exe; an npm install is a `.cmd` shim that must go through cmd.exe).
public static class ClaudeCodeBackend
{
    public sealed class Result
    {
        public string Text = "";
        public string? SessionId;
        public string? Error;
        // From the final `result` event — real numbers even on a subscription.
        public long InputTokens;
        public long OutputTokens;
        public int NumTurns;
        public double CostUsd;
        public long DurationMs;
    }

    public static async Task<Result> RunAsync(
        string exe, string prompt, string workDir, string mcpConfigPath, string? resumeSessionId,
        string allowedToolsGlob, Action<string> onText, Action<string> onTool, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--mcp-config", mcpConfigPath,
            "--allowedTools", allowedToolsGlob
        };
        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            args.Add("--resume");
            args.Add(resumeSessionId!);
        }

        var result = new Result();
        Process proc;
        try
        {
            proc = Start(exe, args, workDir);
        }
        catch (Win32Exception)
        {
            // npm-installed `claude` is a .cmd shim — not directly launchable; go through cmd.exe.
            try
            {
                var viaCmd = new List<string> { "/c", exe };
                viaCmd.AddRange(args);
                proc = Start("cmd.exe", viaCmd, workDir);
            }
            catch (Exception ex)
            {
                result.Error = $"Can't launch Claude Code ('{exe}'). Install it and run 'claude login', " +
                               $"or set the full path in Settings. ({ex.Message})";
                return result;
            }
        }
        catch (Exception ex)
        {
            result.Error = $"Can't launch Claude Code ('{exe}'): {ex.Message}";
            return result;
        }

        try
        {
            await proc.StandardInput.WriteAsync(prompt);
            proc.StandardInput.Close();

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                ct.ThrowIfCancellationRequested();
                ParseLine(line, result, onText, onTool);
            }

            var err = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0 && string.IsNullOrEmpty(result.Text))
                result.Error = string.IsNullOrWhiteSpace(err)
                    ? $"Claude Code exited with code {proc.ExitCode}."
                    : err.Trim();
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        return result;
    }

    // Each stdout line is one JSON event. We pull: the session id (to resume the conversation next
    // turn), streamed assistant text deltas, tool-call names, and the final result text.
    private static void ParseLine(string line, Result result, Action<string> onText, Action<string> onTool)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                result.SessionId = sid.GetString();

            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;

            if (type == "result")
            {
                if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String)
                    result.Text = res.GetString() ?? result.Text;
                if (root.TryGetProperty("num_turns", out var nt) && nt.TryGetInt32(out var ntv)) result.NumTurns = ntv;
                if (root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDouble(out var cv)) result.CostUsd = cv;
                if (root.TryGetProperty("duration_ms", out var dm) && dm.TryGetInt64(out var dmv)) result.DurationMs = dmv;
                if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    if (u.TryGetProperty("input_tokens", out var it) && it.TryGetInt64(out var itv)) result.InputTokens = itv;
                    if (u.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var otv)) result.OutputTokens = otv;
                }
            }

            if (type == "stream_event" && root.TryGetProperty("event", out var ev))
            {
                var et = ev.TryGetProperty("type", out var ett) && ett.ValueKind == JsonValueKind.String
                    ? ett.GetString() : null;

                if (et == "content_block_delta" && ev.TryGetProperty("delta", out var delta))
                {
                    var dt = delta.TryGetProperty("type", out var dtt) && dtt.ValueKind == JsonValueKind.String
                        ? dtt.GetString() : null;
                    if (dt == "text_delta" && delta.TryGetProperty("text", out var txt) &&
                        txt.ValueKind == JsonValueKind.String)
                        onText(txt.GetString() ?? "");
                }
                else if (et == "content_block_start" && ev.TryGetProperty("content_block", out var cb))
                {
                    var cbt = cb.TryGetProperty("type", out var cbtt) && cbtt.ValueKind == JsonValueKind.String
                        ? cbtt.GetString() : null;
                    if ((cbt == "tool_use" || cbt == "mcp_tool_use" || cbt == "server_tool_use") &&
                        cb.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                        onTool(nm.GetString() ?? "tool");
                }
            }
        }
        catch { /* non-JSON or partial line — ignore */ }
    }

    private static Process Start(string file, IEnumerable<string> args, string workDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            WorkingDirectory = string.IsNullOrWhiteSpace(workDir) ? null : workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
    }
}
