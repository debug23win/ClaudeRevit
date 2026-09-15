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
        public long InputTokens, OutputTokens;
        public bool Completed;
    }

    public static string? ResolveExecutable()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var path = Path.Combine(dir.Trim('"'), "codex.exe");
            if (File.Exists(path)) return path;
        }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(root))
        {
            var found = Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (found != null) return found;
        }
        var native = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "codex.exe");
        return File.Exists(native) ? native : null;
    }

    public static List<string> Arguments(string url, string? sessionId, string? imagePath = null)
    {
        var args = new List<string> { "exec", "--json", "--ignore-user-config", "--skip-git-repo-check",
            "--sandbox", "read-only", "-c", "features.shell_tool=false", "-c", "features.multi_agent=false",
            "-c", "web_search=\"disabled\"", "-c",
            "mcp_servers.clauderevit={url=" + JsonSerializer.Serialize(url) +
            ",bearer_token_env_var=\"CLAUDEREVIT_MCP_TOKEN\",required=true,tool_timeout_sec=120,default_tools_approval_mode=\"approve\"}" };
        if (!string.IsNullOrWhiteSpace(sessionId)) { args.Add("resume"); args.Add(sessionId); }
        if (imagePath != null) { args.Add("--image"); args.Add(imagePath); }
        args.Add("-"); // Prompt goes to stdin, never through a shell or command line.
        return args;
    }

    private static Process Start(string exe, IEnumerable<string> args, string workDir, string? token = null)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = workDir, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment.Remove("OPENAI_API_KEY");
        psi.Environment.Remove("CODEX_API_KEY");
        if (token != null) psi.Environment["CLAUDEREVIT_MCP_TOKEN"] = token;
        return Process.Start(psi) ?? throw new InvalidOperationException("Could not start Codex.");
    }

    public static async Task<Result> RunAsync(string prompt, string workDir, string url, string token,
        string? sessionId, Action<string> onText, Action<string> onTool, CancellationToken ct,
        string? imagePath = null)
    {
        var exe = ResolveExecutable() ?? throw new InvalidOperationException(
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
        using var process = Start(exe, Arguments(url, sessionId, imagePath), workDir, token);
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
                else if (type == "item.started" && itemType == "mcp_tool_call")
                    onTool(item.TryGetProperty("tool", out var tool) ? tool.GetString() ?? "Revit" : "Revit");
            }
        }
    }
}
