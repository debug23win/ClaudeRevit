using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeRevit.Services;

// Separate from ClaudeCodeBackend: the working Claude subscription path is unchanged.
public static class CodexBackend
{
    public sealed class Result
    {
        public string? SessionId;
        public string? Error;
        public string Text = "";
        public long InputTokens, OutputTokens, CachedInputTokens, ReasoningTokens;
        public string UsageScope = "session_cumulative";
        public bool Completed;
        public int SubagentCalls;
    }

    public static string? ResolveExecutable(string? requestedExe = null)
    {
        if (!string.IsNullOrWhiteSpace(requestedExe) && (requestedExe.Contains('\\') || requestedExe.Contains('/')))
            return File.Exists(requestedExe) && CodexCli.LooksLikeCodexBinary(requestedExe) ? requestedExe : null;
        var candidates = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var suffix in new[] { ".exe", ".cmd", ".bat" })
            {
                var path = Path.Combine(dir.Trim('"'), "codex" + suffix);
                if (File.Exists(path)) candidates.Add(path);
            }
        }
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        if (File.Exists(installed)) candidates.Add(installed);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(root))
        {
            try { candidates.AddRange(Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var native = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "codex.exe");
        if (File.Exists(native)) candidates.Add(native);
        // PATH can still point at an older desktop installation. Ask the actual binaries:
        // Rust CLI executables have no Windows version resource. Cache by file fingerprint,
        // probe concurrently with a bounded wait, and keep an explicit user path pinned.
        var distinct = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var versions = Task.WhenAll(distinct.Select(path => Task.Run(() => ReadVersion(path)))).GetAwaiter().GetResult();
        return SelectExecutable(distinct.Zip(versions, (path, version) => (path, version)));
    }

    internal static string? SelectExecutable(IEnumerable<(string Path, Version? Version)> candidates) =>
        candidates.OrderByDescending(c => c.Version ?? new Version(0, 0)).Select(c => c.Path).FirstOrDefault();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Stamp, long Size, Version? Version)> VersionCache = new(StringComparer.OrdinalIgnoreCase);
    private static Version? ReadVersion(string path)
    {
        try
        {
            var file = new FileInfo(path);
            var stamp = file.LastWriteTimeUtc.Ticks; var size = file.Length;
            if (VersionCache.TryGetValue(path, out var cached) && cached.Stamp == stamp && cached.Size == size) return cached.Version;
            using var process = Start(path, new[] { "--version" }, Path.GetDirectoryName(path)!);
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(2000)) { process.Kill(entireProcessTree: true); return null; }
            // Only accept the CLI's product/version line, not arbitrary digits from warnings.
            var match = System.Text.RegularExpressions.Regex.Match(output.GetAwaiter().GetResult(),
                @"(?m)^codex(?:-cli)?\s+(\d+\.\d+\.\d+)(?:\S*)\s*$");
            Version? version = match.Success && Version.TryParse(match.Groups[1].Value, out var parsed) ? parsed : null;
            if (process.ExitCode != 0) return null;
            VersionCache[path] = (stamp, size, version);
            return version;
        }
        catch { return null; }
    }

    public static string ConfigSnippet(string url) =>
        "[mcp_servers.clauderevit]\nurl = " + JsonSerializer.Serialize(url) +
        "\nbearer_token_env_var = \"CLAUDEREVIT_MCP_TOKEN\"\n" +
        "# External clients: set CLAUDEREVIT_MCP_TOKEN to the token shown above.\n" +
        "# The in-Revit pane configures this connection automatically.";

    public static List<string> Arguments(string url, string? sessionId, string? imagePath = null,
        string? model = null, string? effort = null)
    {
        var args = new List<string> { "exec", "--json", "--ignore-user-config", "--skip-git-repo-check",
            "--sandbox", "read-only", "-c", "features.shell_tool=false", "-c", "features.multi_agent=true",
            "-c", "agents.enabled=true", "-c", "agents.max_concurrent_threads_per_session=3",
            "-c", "web_search=\"disabled\"", "-c", "model_auto_compact_token_limit=48000",
            "-c", "tool_output_token_limit=4000", "-c",
            "mcp_servers.clauderevit={url=" + JsonSerializer.Serialize(url) +
            ",bearer_token_env_var=\"CLAUDEREVIT_MCP_TOKEN\",required=true,tool_timeout_sec=120,default_tools_approval_mode=\"approve\"}" };
        if (!string.IsNullOrWhiteSpace(model)) args.AddRange(new[] { "--model", model.Trim() });
        if (!string.IsNullOrWhiteSpace(effort)) CodexConfiguration.Add(args, "model_reasoning_effort", effort.Trim());
        if (!string.IsNullOrWhiteSpace(sessionId)) { args.Add("resume"); args.Add(sessionId); }
        if (imagePath != null) { args.Add("--image"); args.Add(imagePath); }
        args.Add("-"); // Prompt goes to stdin, never through a shell or command line.
        return args;
    }

    private static Process Start(string exe, IEnumerable<string> args, string workDir, string? token = null)
    {
        var shim = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(shim ? "cmd.exe" : exe) { WorkingDirectory = workDir, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (shim) { psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(exe); }
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment.Remove("OPENAI_API_KEY");
        psi.Environment.Remove("CODEX_API_KEY");
        if (token != null) psi.Environment["CLAUDEREVIT_MCP_TOKEN"] = token;
        return Process.Start(psi) ?? throw new InvalidOperationException("Could not start Codex.");
    }

    public static async Task<Result> RunAsync(string prompt, string workDir, string url, string token,
        string? sessionId, Action<string> onText, Action<string> onTool, CancellationToken ct,
        string? imagePath = null, string? model = null, string? effort = null, string? executable = null)
    {
        var exe = ResolveExecutable(executable) ?? throw new InvalidOperationException(
            "Install the Codex desktop app and sign in with ChatGPT to use the OpenAI subscription.");
        Directory.CreateDirectory(workDir);
        // Verify subscription auth without reading/copying credentials or logging the user out.
        using (var login = Start(exe, new[] { "login", "status" }, workDir))
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var killLogin = timeout.Token.Register(() => { try { login.Kill(true); } catch { } });
            var stdout = login.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = login.StandardError.ReadToEndAsync(timeout.Token);
            await login.WaitForExitAsync(timeout.Token);
            var status = await stdout + await stderr;
            if (login.ExitCode != 0 || !status.Contains("Logged in using ChatGPT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Sign in to Codex with ChatGPT first. This subscription mode does not use an OpenAI API key.");
        }
        var startedUtc = DateTime.UtcNow;
        using var process = Start(exe, Arguments(url, sessionId, imagePath, model, effort), workDir, token);
        using var cancel = ct.Register(() => { try { process.Kill(true); } catch { } });
        var errors = process.StandardError.ReadToEndAsync(ct); // drain concurrently to avoid pipe deadlock
        var result = new Result();
        try
        {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
            process.StandardInput.Close();
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                ParseLine(line, result, onText, onTool);
            await process.WaitForExitAsync(ct);
            var error = await errors;
            if (process.ExitCode != 0 || !result.Completed)
                result.Error ??= string.IsNullOrWhiteSpace(error) ? "Codex did not complete the turn." : TextUtil.Truncate(error.Trim(), 1800);
            result.SessionId ??= sessionId;
            if (result.SessionId is { } id && CodexUsage.ReadTurn(id, startedUtc) is { } usage)
            {
                result.InputTokens = usage.Input; result.OutputTokens = usage.Output;
                result.CachedInputTokens = usage.Cached; result.ReasoningTokens = usage.Reasoning; result.UsageScope = usage.Scope;
            }
            if(result.SubagentCalls>0)result.UsageScope+="_parent_only";
            return result;
        }
        catch { try { process.Kill(true); } catch { } throw; }
    }

    public static void ParseLine(string line, Result result, Action<string> onText, Action<string> onTool)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException) { return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeNode)) return;
            var type = typeNode.GetString();
            if (type == "thread.started") result.SessionId = root.GetProperty("thread_id").GetString();
            else if (type == "turn.completed")
            {
                result.Completed = true;
                if (root.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("input_tokens", out var i)) result.InputTokens = i.GetInt64();
                    if (usage.TryGetProperty("output_tokens", out var o)) result.OutputTokens = o.GetInt64();
                }
            }
            else if (type is "turn.failed" or "error")
                result.Error = root.TryGetProperty("message", out var m) ? m.GetString()
                    : root.TryGetProperty("error", out var e) && e.TryGetProperty("message", out m) ? m.GetString() : "Codex turn failed.";
            else if (type is "item.started" or "item.completed" && root.TryGetProperty("item", out var item))
            {
                var itemType = item.GetProperty("type").GetString();
                if (type == "item.completed" && itemType == "agent_message")
                {
                    var text = item.GetProperty("text").GetString() ?? "";
                    if (result.Text.Length > 0) text = "\n\n" + text;
                    result.Text += text;
                    onText(text);
                }
                else if (type == "item.started" && itemType is "collab_tool_call" or "collab_agent_tool_call")
                {
                    result.SubagentCalls++;
                    onTool("subagent");
                }
                else if (type == "item.started" && itemType == "mcp_tool_call")
                    onTool(item.TryGetProperty("tool", out var tool) ? tool.GetString() ?? "Revit" : "Revit");
            }
        }
    }

    // A judge has no MCP connection or shell/web tools. It cannot change the model
    // it is grading, and uses the explicitly selected subscription model/effort.
    public static async Task<string> CompleteAsync(string prompt, string workDir, CancellationToken ct,
        string? model = null, string? effort = null, string? executable = null)
    {
        var exe = ResolveExecutable(executable) ?? throw new InvalidOperationException("Codex CLI not found.");
        Directory.CreateDirectory(workDir);
        using (var login = Start(exe, new[] { "login", "status" }, workDir))
        {
            using var killLogin = ct.Register(() => { try { login.Kill(true); } catch { } });
            var stdout = login.StandardOutput.ReadToEndAsync(ct);
            var stderr = login.StandardError.ReadToEndAsync(ct);
            await login.WaitForExitAsync(ct);
            var status = await stdout + await stderr;
            if (login.ExitCode != 0 || !status.Contains("Logged in using ChatGPT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The benchmark judge requires Codex signed in with ChatGPT.");
        }
        var models = await CodexModelCatalog.ReadAsync(exe, workDir, ct);
        var choice = CodexModels.Select(models, model, effort);
        var args = new List<string> { "exec", "--json", "--ignore-user-config", "--skip-git-repo-check", "--sandbox", "read-only",
            "-c", "features.shell_tool=false", "-c", "features.multi_agent=false", "-c", "agents.enabled=false", "-c", "web_search=\"disabled\"", "--model", choice.Model };
        if (choice.Effort != null) CodexConfiguration.Add(args, "model_reasoning_effort", choice.Effort);
        args.Add("-");
        using var process = Start(exe, args, workDir);
        using var cancel = ct.Register(() => { try { process.Kill(true); } catch { } });
        var errors = process.StandardError.ReadToEndAsync(ct);
        var result = new Result();
        await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
        process.StandardInput.Close();
        while (await process.StandardOutput.ReadLineAsync(ct) is { } line) ParseLine(line, result, _ => { }, _ => { });
        await process.WaitForExitAsync(ct);
        var error = await errors;
        if (process.ExitCode != 0 || !result.Completed || result.Error != null)
            throw new InvalidOperationException(result.Error ?? (error.Length > 0 ? TextUtil.Truncate(error, 1000) : "Codex judge did not finish."));
        return result.Text;
    }
}
