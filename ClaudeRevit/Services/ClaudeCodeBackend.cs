using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
        // Diagnostics: MCP server connection status from the init event ("clauderevit=connected"),
        // and whether the final result was flagged an error. Explains a run that launched but did
        // nothing (e.g. MCP failed to connect → no Revit tools → no work).
        public string? McpStatus;
        public bool IsError;
        public string? Subtype;
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
            "--include-partial-messages"
        };
        // MCP + tools are for the "drive Revit" path. The judge runs with neither (empty) — a pure
        // text-grading call — so skip the flags; an empty allowedTools glob denies every tool.
        if (!string.IsNullOrWhiteSpace(mcpConfigPath))
        {
            args.Add("--mcp-config");
            args.Add(mcpConfigPath);
        }
        if (!string.IsNullOrWhiteSpace(allowedToolsGlob))
        {
            args.Add("--allowedTools");
            args.Add(allowedToolsGlob);
        }
        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            args.Add("--resume");
            args.Add(resumeSessionId!);
        }

        var result = new Result();

        // Revit is a GUI process — its PATH is often narrower than the user's shell, so a bare
        // "claude" from an npm/native install frequently isn't found. Resolve to a full path across
        // the common install locations FIRST; that also avoids cmd.exe's localized (and, on a Russian
        // Windows, mojibaked) "'claude' is not recognized as a command" error leaking into results.
        var resolved = Resolve(exe);
        if (resolved == null)
        {
            result.Error =
                $"Claude Code CLI not found ('{exe}'). Install it (npm i -g @anthropic-ai/claude-code), " +
                "run 'claude login' once, then set the full path to claude.cmd/claude.exe in Settings.";
            return result;
        }

        var viaCmd = resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                     resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        Process proc;
        try
        {
            if (viaCmd)
            {
                // .cmd/.bat shims aren't PE images — must run through cmd.exe. The path is real, so
                // cmd won't print a "not recognized" error.
                var cmdArgs = new List<string> { "/c", resolved };
                cmdArgs.AddRange(args);
                proc = Start("cmd.exe", cmdArgs, workDir);
            }
            else
            {
                proc = Start(resolved, args, workDir);
            }
        }
        catch (Exception ex)
        {
            result.Error = $"Can't launch Claude Code ('{resolved}'): {ex.Message}";
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

    // A one-shot, no-tools text completion on the subscription — used for the impartial benchmark
    // judge so grading costs nothing on the API. The bogus allowedTools glob matches no tool, so in
    // headless (-p) mode every built-in tool is auto-denied and the model just returns text.
    public static async Task<string> CompleteAsync(string exe, string prompt, string workDir, CancellationToken ct)
    {
        var res = await RunAsync(
            exe, prompt, workDir, mcpConfigPath: "", resumeSessionId: null,
            allowedToolsGlob: "__deny_all_tools__",
            onText: _ => { }, onTool: _ => { }, ct);
        if (string.IsNullOrEmpty(res.Text) && !string.IsNullOrEmpty(res.Error))
            throw new InvalidOperationException(res.Error);
        return res.Text;
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

            // The init event lists the MCP servers Claude Code tried to attach and whether each
            // connected — the single most useful signal when a run launches but does no work.
            if (type == "system" && root.TryGetProperty("mcp_servers", out var servers) &&
                servers.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var s in servers.EnumerateArray())
                {
                    var name = s.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() : "?";
                    var status = s.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                        ? st.GetString() : "?";
                    parts.Add($"{name}={status}");
                }
                if (parts.Count > 0) result.McpStatus = string.Join(", ", parts);
            }

            if (type == "result")
            {
                if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String)
                    result.Text = res.GetString() ?? result.Text;
                if (root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True)
                    result.IsError = true;
                if (root.TryGetProperty("subtype", out var sub) && sub.ValueKind == JsonValueKind.String)
                    result.Subtype = sub.GetString();
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

    // Find the `claude` executable. Honours an explicit path, then PATH (+ Windows extensions),
    // then the well-known npm-global and native-install locations that Revit's PATH usually misses.
    // Returns a full path, or null if nothing exists.
    private static string? Resolve(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) exe = "claude";

        // Explicit path (has a directory separator) — trust it if it exists.
        if (exe.IndexOf(Path.DirectorySeparatorChar) >= 0 || exe.IndexOf('/') >= 0)
            return File.Exists(exe) ? exe : null;

        var exts = new[] { "", ".cmd", ".exe", ".bat", ".ps1" };

        // Search each PATH entry.
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in exts)
            {
                try { var p = Path.Combine(dir, exe + ext); if (File.Exists(p)) return p; }
                catch { /* bad PATH entry */ }
            }
        }

        // Common install locations the GUI PATH tends to omit.
        string? Env(string v) => Environment.GetEnvironmentVariable(v);
        var candidates = new List<string?>
        {
            // npm global (default prefix)
            Env("APPDATA") is { } ad ? Path.Combine(ad, "npm", exe + ".cmd") : null,
            Env("APPDATA") is { } ad2 ? Path.Combine(ad2, "npm", exe + ".ps1") : null,
            // native installer (irm https://claude.ai/install.ps1 | iex) → %USERPROFILE%\.local\bin\claude.exe
            Env("USERPROFILE") is { } upn ? Path.Combine(upn, ".local", "bin", exe + ".exe") : null,
            Env("USERPROFILE") is { } up3 ? Path.Combine(up3, ".local", "bin", exe) : null,
            // other local installs
            Env("LOCALAPPDATA") is { } la ? Path.Combine(la, "Programs", "claude", exe + ".exe") : null,
            Env("USERPROFILE") is { } up ? Path.Combine(up, ".claude", "local", exe + ".exe") : null,
            Env("USERPROFILE") is { } up2 ? Path.Combine(up2, ".claude", "local", exe) : null,
            // unix-y (in case Revit ever runs elsewhere)
            "/usr/local/bin/" + exe,
            "/usr/bin/" + exe,
        };
        foreach (var c in candidates)
        {
            if (c != null) { try { if (File.Exists(c)) return c; } catch { } }
        }

        // Claude Desktop (the MSIX Store app) BUNDLES the Claude Code CLI under its package dir at
        //   %LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\claude-code\<version>\claude.exe
        // Users who only have the desktop app still have a working headless claude.exe here — it's just
        // not on PATH. Glob for it and take the newest version folder.
        try
        {
            var packages = Env("LOCALAPPDATA") is { } lad ? Path.Combine(lad, "Packages") : null;
            if (packages != null && Directory.Exists(packages))
            {
                var best = Directory.EnumerateDirectories(packages, "Claude_*")
                    .Select(pkg => Path.Combine(pkg, "LocalCache", "Roaming", "Claude", "claude-code"))
                    .Where(Directory.Exists)
                    .SelectMany(cc => Directory.EnumerateDirectories(cc))
                    .Select(ver => Path.Combine(ver, "claude.exe"))
                    .Where(File.Exists)
                    .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase) // newest version last-sorts first
                    .FirstOrDefault();
                if (best != null) return best;
            }
        }
        catch { /* enumeration raced or access denied */ }

        return null;
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
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
    }
}
