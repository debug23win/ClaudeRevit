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
        public bool Completed;
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

    // Map our internal model tag to what Claude Code accepts for `--model`.
    //
    // The older tags use the coarse family aliases ("opus"/"sonnet"/"haiku"), which resolve to
    // whatever the CLI currently considers that family's model. That is fine for them, but it can't
    // express "Opus 5 specifically" — the family alias would just pick the CLI's current Opus and
    // make two dropdown entries behave identically. So the newer models pass their FULL model id,
    // which Claude Code also accepts, and the pane's choice actually decides.
    //
    // Returns null for "auto" and "fable-5" — there we let the CLI pick, because the
    // advisor-escalation that "auto" means on the API has no equivalent inside Claude Code's own
    // loop. A tag the CLI doesn't recognise surfaces as a CLI error rather than a silent fallback.
    public static string? ModelAlias(string? tag) => tag switch
    {
        "opus-5" => "claude-opus-5",
        "fable-5-1" => "claude-fable-5-1",
        "opus-4-8" or "opus-4-7" or "opus-4-6" => "opus",
        "sonnet-5" or "sonnet-4-6" => "sonnet",
        "haiku-4-5" => "haiku",
        _ => null
    };

    public static async Task<Result> RunAsync(
        string exe, string prompt, string workDir, string mcpConfigPath, string? resumeSessionId,
        string allowedToolsGlob, Action<string> onText, Action<string> onTool, CancellationToken ct,
        string? model = null, string? effort = null)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages"
        };
        // Keep subscription OAuth, while excluding settings that can supply an
        // API-key helper, unrelated MCP servers or project hooks.
        args.AddRange(new[] { "--setting-sources", "", "--strict-mcp-config" });
        args.AddRange(new[] { "--tools", string.IsNullOrWhiteSpace(mcpConfigPath) ? "" : "Agent" });
        McpAgentSelection.AddClaudeOptions(args, model, effort);
        // MCP + tools are for the "drive Revit" path. The judge runs with neither (empty) — a pure
        // text-grading call — so skip the flags; an empty allowedTools glob denies every tool.
        if (!string.IsNullOrWhiteSpace(mcpConfigPath))
        {
            args.Add("--mcp-config");
            args.Add(mcpConfigPath);

            // The rules that make the difference between a model that drives Revit well and one
            // that guesses — units, verification, batching — lived only in the MCP handshake
            // instructions, which a client may or may not surface to the model. On this path we
            // launch the CLI ourselves, so they go in where they are certain to be read.
            args.Add("--append-system-prompt");
            args.Add(McpServer.DrivingRules);
            args.AddRange(new[] { "--agents", JsonSerializer.Serialize(new
            {
                revit_planner = new { description = "Plan independent Revit geometry or parameter work from the supplied snapshot.",
                    prompt = "Return a compact plan, parameters and checks. Work only from the context supplied by the parent. Never modify Revit or claim to have executed tools.", tools = Array.Empty<string>() },
                revit_checker = new { description = "Independently check dimensions, family formulas and reinforcement plans.",
                    prompt = "Check the supplied evidence and proposed geometry. Return concrete defects and suggested corrections. Never modify Revit or claim to have executed tools.", tools = Array.Empty<string>() }
            }) });
        }
        if (!string.IsNullOrWhiteSpace(allowedToolsGlob))
        {
            args.Add("--allowedTools");
            args.Add(allowedToolsGlob);
            if (!string.IsNullOrWhiteSpace(mcpConfigPath)) args.Add("Agent");
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

        await VerifySubscriptionAsync(resolved, workDir, ct);

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

        using var processLifetime = proc;
        using var cancelled = ct.Register(() => { try { proc.Kill(true); } catch { } });
        try
        {
            await proc.StandardInput.WriteAsync(prompt);
            proc.StandardInput.Close();

            // Drain stderr CONCURRENTLY. Reading it only after stdout ends deadlocks the moment the
            // child writes more than the stderr pipe buffer holds: it blocks on the write, so it
            // never finishes stdout, so we never start reading stderr.
            var errTask = proc.StandardError.ReadToEndAsync();

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
            {
                ct.ThrowIfCancellationRequested();
                ParseLine(line, result, onText, onTool);
            }

            var err = await errTask;
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0 && result.Error == null)
                result.Error = string.IsNullOrWhiteSpace(err) && result.Text.Length > 0 ? result.Text : string.IsNullOrWhiteSpace(err)
                    ? $"Claude Code exited with code {proc.ExitCode}."
                    : err.Trim();
            if (proc.ExitCode == 0 && !result.Completed && result.Error == null) result.Error = "Claude Code returned no completion event. Update the CLI or check its output format.";
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            result.Error = ex.Message;
        }
        return result;
    }

    // A one-shot, no-tools text completion on the subscription — used for the impartial benchmark
    // judge so grading costs nothing on the API. The bogus allowedTools glob matches no tool, so in
    // headless (-p) mode every built-in tool is auto-denied and the model just returns text.
    public static async Task<string> CompleteAsync(string exe, string prompt, string workDir, CancellationToken ct,
        string? model = null, string? effort = null)
    {
        var res = await RunAsync(
            exe, prompt, workDir, mcpConfigPath: "", resumeSessionId: null,
            allowedToolsGlob: "__deny_all_tools__",
            onText: _ => { }, onTool: _ => { }, ct, model: model, effort: effort);
        if (res.IsError || !string.IsNullOrEmpty(res.Error))
            throw new InvalidOperationException(res.Error ?? res.Text);
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
                // Report ONLY our own server — the user's Claude Code may have many unrelated
                // connectors (Gmail, Drive, Booking.com…) whose statuses would otherwise flood the
                // diagnostic line.
                var parts = new List<string>();
                foreach (var s in servers.EnumerateArray())
                {
                    var name = s.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() ?? "" : "";
                    if (name.IndexOf("clauderevit", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var status = s.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                        ? st.GetString() : "?";
                    parts.Add($"{name}={status}");
                }
                if (parts.Count > 0) result.McpStatus = string.Join(", ", parts);
                else if (result.McpStatus == null) result.McpStatus = "clauderevit=absent";
            }

            if (type == "result")
            {
                result.Completed = true;
                if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String)
                    result.Text = res.GetString() ?? result.Text;
                if (root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True)
                    result.IsError = true;
                if (result.IsError && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                    result.Error = string.Join("; ", errors.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()));
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

    // Find a CLI executable by name. Honours an explicit path, then PATH (+ Windows extensions),
    // then the well-known npm-global and native-install locations that Revit's PATH usually misses.
    // Returns a full path, or null if nothing exists.
    //
    // Shared with CodexBackend, so the tool-specific locations below are gated on the name being
    // asked for. That gating is not cosmetic: the Claude Desktop branch used to return claude.exe
    // whatever it was asked for, so a machine with Claude Desktop and no Codex "found" Codex and
    // ran `claude.exe exec --json ...`, whose reply ("error: unknown option") reads like a broken
    // Codex install rather than like the wrong program being launched.
    internal static string? Resolve(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) exe = "claude";
        var wantsClaude = string.Equals(exe, "claude", StringComparison.OrdinalIgnoreCase);
        var wantsCodex = string.Equals(exe, "codex", StringComparison.OrdinalIgnoreCase);

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
            Env("LOCALAPPDATA") is { } la ? Path.Combine(la, "Programs", exe, exe + ".exe") : null,
            wantsClaude && Env("USERPROFILE") is { } up ? Path.Combine(up, ".claude", "local", exe + ".exe") : null,
            wantsClaude && Env("USERPROFILE") is { } up2 ? Path.Combine(up2, ".claude", "local", exe) : null,
            // Codex's own installer keeps the binary out of PATH in the same way Claude's does.
            wantsCodex && Env("USERPROFILE") is { } cx ? Path.Combine(cx, ".codex", "bin", exe + ".exe") : null,
            wantsCodex && Env("USERPROFILE") is { } cx2 ? Path.Combine(cx2, ".codex", "bin", exe) : null,
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
        //
        // Only when claude is what was asked for: this branch hard-codes claude.exe, so without the
        // guard it answers every lookup with the wrong program.
        try
        {
            if (!wantsClaude) return null;
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

    // Checked before every CLI run, so a confirmed sign-in is remembered for a while instead of
    // paying a process start each message — keyed by AuthFingerprint, so switching the CLI to an
    // API key is caught on the next message rather than billed. An expired session still fails on
    // the real run with the CLI's own message. A slow check is reported as such, not as a cancellation (which the pane
    // shows as "Cancelled", as if the user had pressed Stop).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> SignedIn = new(StringComparer.OrdinalIgnoreCase);

    private static async Task VerifySubscriptionAsync(string exe, string workDir, CancellationToken ct)
    {
        var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var key = AuthFingerprint.For(exe, Path.Combine(home, ".credentials.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"));
        if (SignedIn.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(30)) return;
        try { await VerifySubscriptionCoreAsync(exe, workDir, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("'claude auth status' did not answer within 15 seconds. Check that Claude Code starts in a terminal, then retry.");
        }
        catch { SignedIn.TryRemove(key, out _); throw; }
        SignedIn[key] = DateTime.UtcNow;
    }

    private static async Task VerifySubscriptionCoreAsync(string exe, string workDir, CancellationToken ct)
    {
        var shim = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var args = shim ? new List<string> { "/c", exe, "auth", "status", "--json" } : new List<string> { "auth", "status", "--json" };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var status = Start(shim ? "cmd.exe" : exe, args, workDir);
        using var cancel = timeout.Token.Register(() => { try { status.Kill(true); } catch { } });
        var stdout = status.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = status.StandardError.ReadToEndAsync(timeout.Token);
        await status.WaitForExitAsync(timeout.Token);
        var raw = await stdout; await stderr;
        try
        {
            using var json = JsonDocument.Parse(raw);
            if (status.ExitCode == 0 && json.RootElement.TryGetProperty("loggedIn", out var logged) && logged.ValueKind == JsonValueKind.True &&
                json.RootElement.TryGetProperty("authMethod", out var method) && method.GetString() == "claude.ai") return;
        }
        catch (JsonException) { }
        throw new InvalidOperationException("Sign in to Claude Code with your Claude subscription. Update the CLI if 'claude auth status --json' is unavailable. API/Console login is not used in subscription mode.");
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
            StandardErrorEncoding = Encoding.UTF8,
            // CRITICAL: the prompt (with Cyrillic / any non-ASCII) is written to the CLI's stdin. Without
            // this, .NET encodes it with the OS default (CP1251/OEM on a Russian Windows) and `claude`
            // receives mojibake — the model literally can't read the request. UTF-8, no BOM.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // This backend is explicitly the Claude subscription path. Inherited API
        // credentials/cloud switches must not silently charge a different account.
        foreach (var key in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL",
                     "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY" })
            psi.Environment.Remove(key);
        return Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
    }
}
