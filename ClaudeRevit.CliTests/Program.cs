using System.Diagnostics;
using System.Text.Json;
using ClaudeRevit.Services;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new System.Text.UTF8Encoding(false);
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        if (args.Length > 0 && args[0] is "auth" or "login" or "app-server" or "exec" or "-p" or "--version") return await Fake(args);
        if (args.Length > 0 && args[0] == "--live") return await Live(args);
        var root = Path.Combine(Path.GetTempPath(), "clauderevit-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
        var names = new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY", "OPENAI_API_KEY", "CODEX_API_KEY", "CLAUDEREVIT_TEST_MODE", "CLAUDEREVIT_TEST_AUTH", "PATH" };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var key in names.Take(8)) Environment.SetEnvironmentVariable(key, "dummy-test-value");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var ct = timeout.Token;
            if (OperatingSystem.IsWindows())
            {
                var oldDir = Path.Combine(root, "old"); var newDir = Path.Combine(root, "new");
                foreach (var dir in new[] { oldDir, newDir })
                {
                    Directory.CreateDirectory(dir);
                    foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "codex.*")) File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
                }
                // Deliberately put the older binary first; both have valid app-host payloads.
                Environment.SetEnvironmentVariable("PATH", oldDir + Path.PathSeparator + newDir + Path.PathSeparator + saved["PATH"]);
                Check(CodexBackend.ResolveExecutable() == Path.Combine(newDir, "codex.exe"), "Automatic CLI discovery picked older PATH binary");
                Check(CodexBackend.ResolveExecutable(Path.Combine(oldDir, "codex.exe")) == Path.Combine(oldDir, "codex.exe"), "Explicit CLI path was not pinned");
                Environment.SetEnvironmentVariable("PATH", saved["PATH"]);
            }
            var catalog = await CodexModelCatalog.ReadAsync(exe, root, ct);
            Check(catalog.Count == 2, "Model discovery failed");
            foreach (var model in new[] { "gpt-test-a", "gpt-test-b" })
            {
                var raw = await CodexBackend.CompleteAsync("Reply to test", root, ct, model, "low", exe);
                using var result = JsonDocument.Parse(raw);
                Check(result.RootElement.GetProperty("model").GetString() == model, "Codex judge ignored selected model");
                Check(!result.RootElement.GetProperty("args").EnumerateArray().Any(a => a.GetString()!.Contains("mcp_servers")), "Judge received MCP");
            }
            foreach (var model in new[] { "sonnet", "opus" })
            {
                var raw = await ClaudeCodeBackend.CompleteAsync(exe, "Привет, тест", root, ct, model, "medium");
                using var result = JsonDocument.Parse(raw);
                Check(result.RootElement.GetProperty("model").GetString() == model, "Claude judge ignored selected model");
                Check(result.RootElement.GetProperty("prompt").GetString() == "Привет, тест", "Prompt encoding changed");
                var argv = result.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
                Check(argv.Contains("--strict-mcp-config") && argv.Contains("--setting-sources"), "Subscription settings were not isolated");
                Check(argv[Array.IndexOf(argv, "--tools") + 1] == "", "Judge tools were not disabled");
                Check(!argv.Contains("--bare"), "Bare would disable OAuth");
            }
            var run = await ClaudeCodeBackend.RunAsync(exe, "test", root, "mcp.json", null, "mcp__clauderevit__*", _ => { }, _ => { }, ct, "sonnet", "high");
            using (var result = JsonDocument.Parse(run.Text)) Check(result.RootElement.GetProperty("args").EnumerateArray().Any(a => a.GetString() == "--mcp-config"), "Modeller lost its MCP configuration");
            Environment.SetEnvironmentVariable("CLAUDEREVIT_TEST_AUTH", "api");
            await Reject(() => ClaudeCodeBackend.CompleteAsync(exe, "test", root, ct), "Claude API auth accepted as subscription");
            await Reject(() => CodexBackend.CompleteAsync("test", root, ct, executable: exe), "Codex API auth accepted as subscription");
            Environment.SetEnvironmentVariable("CLAUDEREVIT_TEST_AUTH", null);
            Environment.SetEnvironmentVariable("CLAUDEREVIT_TEST_MODE", "error");
            await Reject(() => ClaudeCodeBackend.CompleteAsync(exe, "test", root, ct), "CLI error marked successful");
            Environment.SetEnvironmentVariable("CLAUDEREVIT_TEST_MODE", "hang");
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var watch = Stopwatch.StartNew();
            try { await ClaudeCodeBackend.CompleteAsync(exe, "test", root, cancel.Token); throw new Exception("Hung child was not cancelled"); }
            catch (OperationCanceledException) { Check(watch.Elapsed < TimeSpan.FromSeconds(5), "Cancellation hung"); }
            Console.WriteLine("CLI checks passed: subscription authentication, API environment exclusion, multiple models, judge tool isolation, modeller MCP flags, UTF-8, errors and cancellation.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            Directory.Delete(root, true);
        }
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static async Task Reject(Func<Task<string>> run, string message)
    { try { await run(); } catch (InvalidOperationException) { return; } throw new Exception(message); }
    private static async Task<int> Fake(string[] args)
    {
        if (args[0] == "--version")
        { Console.WriteLine("codex-cli " + (new DirectoryInfo(AppContext.BaseDirectory).Name == "new" ? "9.999.2" : "9.999.1")); return 0; }
        if (args[0] == "auth")
        {
            Check(new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY" }.All(k => Environment.GetEnvironmentVariable(k) == null), "Claude received API/cloud credentials");
            Console.WriteLine(JsonSerializer.Serialize(new { loggedIn = true, authMethod = Environment.GetEnvironmentVariable("CLAUDEREVIT_TEST_AUTH") == "api" ? "api_key" : "claude.ai" })); return 0;
        }
        if (args[0] is "login" or "app-server")
        {
            Check(Environment.GetEnvironmentVariable("OPENAI_API_KEY") == null && Environment.GetEnvironmentVariable("CODEX_API_KEY") == null, "Codex received API credentials");
            if (args[0] == "login") { Console.Error.WriteLine(Environment.GetEnvironmentVariable("CLAUDEREVIT_TEST_AUTH") == "api" ? "Logged in using an API key" : "Logged in using ChatGPT"); return 0; }
            while (await Console.In.ReadLineAsync() is { } line)
            {
                using var request = JsonDocument.Parse(line); var r = request.RootElement;
                var method = r.GetProperty("method").GetString();
                if (method == "initialize") Console.WriteLine("{\"id\":1,\"result\":{}}");
                if (method == "model/list") Console.WriteLine(JsonSerializer.Serialize(new { id = 2, result = new { data = new[] { "gpt-test-a", "gpt-test-b" }.Select((m,i) => new { model = m, displayName = m, defaultReasoningEffort = "low", supportedReasoningEfforts = new[] { new { reasoningEffort = "low" } }, isDefault = i == 0 }), nextCursor = (string?)null } }));
            }
            return 0;
        }
        var prompt = await Console.In.ReadToEndAsync();
        if (Environment.GetEnvironmentVariable("CLAUDEREVIT_TEST_MODE") == "hang") await Task.Delay(Timeout.Infinite);
        var index = Array.IndexOf(args, "--model"); var model = index < 0 ? "default" : args[index + 1];
        var raw = JsonSerializer.Serialize(new { model, args, prompt, pass = true, score = 100, reason = "test" });
        if (args[0] == "exec")
        { Console.WriteLine(JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "agent_message", text = raw } })); Console.WriteLine("{\"type\":\"turn.completed\"}"); }
        else Console.WriteLine(JsonSerializer.Serialize(new { type = "result", result = raw, is_error = Environment.GetEnvironmentVariable("CLAUDEREVIT_TEST_MODE") == "error", subtype = "success", num_turns = 1 }));
        return 0;
    }
    private static async Task<int> Live(string[] args)
    {
        // Explicit manual smoke only: never called by CI. One no-tools completion.
        var root = Path.Combine(Environment.CurrentDirectory, "cli-live"); Directory.CreateDirectory(root);
        using var ct = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        const string prompt = "Reply with only the word READY. Do not use tools.";
        var raw = args[1] == "claude"
            ? await ClaudeCodeBackend.CompleteAsync(args[2], prompt, root, ct.Token, args[3], "low")
            : await CodexBackend.CompleteAsync(prompt, root, ct.Token, args[3], "low", args[2]);
        Console.WriteLine(JsonSerializer.Serialize(new { provider = args[1], requested_model = args[3], reply = raw })); return raw.Trim() == "READY" ? 0 : 1;
    }
}

namespace ClaudeRevit.Services
{
    public static class McpServer { public const string DrivingRules = "Use only the configured Revit MCP tools."; }
}
