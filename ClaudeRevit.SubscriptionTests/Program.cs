using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeRevit.Services;

namespace ClaudeRevit.SubscriptionTests;

internal static class Program
{
    private static int passed;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "clauderevit-subscription-" + Guid.NewGuid().ToString("N"));
    private static string Executable => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "subscription-fixture.exe" : "subscription-fixture");

    private static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);
        if (args.FirstOrDefault() is "--acp" or "acp") return await FakeSubscriptionAcp.RunAsync(args);
        Directory.CreateDirectory(Root);
        string? scenario = Environment.GetEnvironmentVariable("CLAUDEREVIT_SUBSCRIPTION_SCENARIO");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var ct = timeout.Token;
            foreach (var agent in new[] { "qwen", "kimi" })
            {
                await Test(agent + " catalog reads subscription metadata only", async () =>
                {
                    var backend = Backend(agent);
                    var catalog = await backend.ReadModelsCoreAsync(ct);
                    Check(catalog.Count == 2 && catalog[0].IsDefault && !catalog[1].IsDefault, "Catalog/default selection differs");
                    Check(catalog.All(model => model.Id != "paid-openai"), "API model leaked into catalog");
                    var trace = Trace(backend);
                    Check(!trace.Any(row => Method(row) is "session/prompt" or "authenticate"), "Catalog sent inference or changed credentials");
                    Check(trace.Single(row => Method(row) == "session/new").GetProperty("value").GetProperty("params").GetProperty("mcpServers").GetArrayLength() == 0, "Catalog connected to Revit");
                });
                await Test(agent + " streams, scopes tools, rejects forged permissions and resumes without replay", async () =>
                {
                    var backend = Backend(agent);
                    var chunks = new StringBuilder(); var tools = new List<string>();
                    var result = await backend.RunAsync("ACP_PERMISSIONS", part => chunks.Append(part), tools.Add, ct);
                    Check(result.Completed && result.Text == "Подписка ✓" && chunks.ToString() == result.Text, "Streaming did not preserve Unicode/text");
                    Check(tools.SequenceEqual(new[] { "create_wall" }), "Tool updates duplicated or included unrelated calls");
                    Check(backend.SessionId == "acp-fixture-session", "Session identity not retained");
                    chunks.Clear(); tools.Clear();
                    result = await backend.RunAsync("follow-up", part => chunks.Append(part), tools.Add, ct);
                    Check(result.Text == "Подписка ✓" && chunks.ToString() == result.Text, "Restored history/foreign text leaked into current turn");
                    Check(Trace(backend).Count(row => Method(row) == "session/load") == 1, "Session was recreated instead of restored");
                });
                await Test(agent + " exact selected model and image input", async () =>
                {
                    string model = (agent == "kimi" ? "kimi-code/" : "") + "fixture-alt";
                    var backend = Backend(agent, model);
                    var result = await backend.RunAsync("ACP_ATTACHMENTS Чертёж ✓", _ => { }, _ => { }, ct,
                        [new SubscriptionImage("image/png", "iVBORw0KGgo=")]);
                    Check(result.Model == model && result.Completed, "Selected model was not used");
                    string method = agent == "kimi" ? "session/set_config_option" : "session/set_model";
                    Check(Trace(backend).Count(row => Method(row) == method) == 1, "Model selection missing");
                });
                await Test(agent + " rejects selected API route before prompt", async () =>
                {
                    var backend = Backend(agent, "paid-openai");
                    await Throws<IOException>(() => backend.RunAsync("do not send", _ => { }, _ => { }, ct));
                    Check(!Trace(backend).Any(row => Method(row) == "session/prompt"), "Paid model received a prompt");
                });
                await Test(agent + " rejects restored API route before prompt", async () =>
                {
                    var backend = Backend(agent, scenario: "paid-current"); backend.SessionId = "acp-fixture-session";
                    await Throws<IOException>(() => backend.RunAsync("do not send", _ => { }, _ => { }, ct));
                    Check(!Trace(backend).Any(row => Method(row) is "session/prompt" or "session/set_model" or "session/set_config_option"), "Paid restored route was used");
                });
                await Test(agent + " EOF and missing session never retry a prompt", async () =>
                {
                    var backend = Backend(agent);
                    await Throws<IOException>(() => backend.RunAsync("ACP_EOF", _ => { }, _ => { }, ct));
                    backend.SessionId = "missing-session";
                    await Throws<IOException>(() => backend.RunAsync("do not send", _ => { }, _ => { }, ct));
                    Check(Trace(backend).Count(row => Method(row) == "session/prompt") == 1, "Incomplete prompt was retried");
                    Check(Trace(backend).Count(row => Method(row) == "session/new") == 1, "Missing session was silently reset");
                });
                await Test(agent + " cancellation is sent and completion stays false", async () =>
                {
                    var backend = Backend(agent);
                    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var watch = Stopwatch.StartNew();
                    await Throws<OperationCanceledException>(() => backend.RunAsync("ACP_CANCEL", _ => cancel.Cancel(), _ => { }, cancel.Token));
                    Check(watch.Elapsed < TimeSpan.FromSeconds(6), "Cancellation did not terminate promptly");
                    Check(Trace(backend).Any(row => Method(row) == "session/cancel"), "Protocol cancellation not delivered");
                });
                await Test(agent + " requires HTTP MCP capability", async () =>
                {
                    var backend = Backend(agent, scenario: "no-http");
                    await Throws<IOException>(() => backend.RunAsync("do not send", _ => { }, _ => { }, ct));
                    Check(!Trace(backend).Any(row => Method(row) is "session/prompt" or "session/new"), "Unsupported MCP transport reached session/prompt");
                });
                await Test(agent + " incomplete stop reason is an error", async () =>
                {
                    var backend = Backend(agent, scenario: "truncated");
                    await Throws<IOException>(() => backend.RunAsync("test", _ => { }, _ => { }, ct));
                    Check(Trace(backend).Count(row => Method(row) == "session/prompt") == 1, "Incomplete turn was repeated");
                });
                await Test(agent + " concurrent stderr is bounded and does not deadlock", async () =>
                {
                    var backend = Backend(agent, scenario: "large-stderr");
                    Check((await backend.RunAsync("test", _ => { }, _ => { }, ct)).Completed, "Large stderr blocked completion");
                });
                await Test(agent + " redacts the MCP bearer token from CLI errors", async () =>
                {
                    var backend = Backend(agent, scenario: "secret-error");
                    var error = await Throws<IOException>(() => backend.RunAsync("test", _ => { }, _ => { }, ct));
                    Check(!error.Message.Contains("fixture-secret", StringComparison.Ordinal) && error.Message.Contains("[redacted]", StringComparison.Ordinal), "MCP secret was exposed");
                });
                await Test(agent + " interactive login isolates inherited API environment", () =>
                {
                    var backend = Backend(agent); var info = backend.StartInfo(login: true);
                    Check(!info.CreateNoWindow && !info.RedirectStandardInput && !info.UseShellExecute, "Login cannot open an interactive console");
                    Check(agent == "qwen" ? info.ArgumentList.SequenceEqual(new[] { "--auth-type", "qwen-subscription" }) : info.ArgumentList.SequenceEqual(new[] { "login" }), "Incorrect login flags");
                    info.Environment["OPENAI_API_KEY"] = "dummy-key"; info.Environment["QWEN_AUTH_TYPE"] = "openai";
                    info.Environment["KIMI_CODE_BASE_URL"] = "https://unrelated.invalid";
                    info.Environment["KIMI_SHELL_PATH"] = "C:\\Custom Git\\bin\\bash.exe";
                    SubscriptionProcess.RemoveProviderOverrides(info);
                    Check(!info.Environment.ContainsKey("OPENAI_API_KEY") && !info.Environment.ContainsKey("QWEN_AUTH_TYPE") && !info.Environment.ContainsKey("KIMI_CODE_BASE_URL"), "Inherited API route survived");
                    Check(info.Environment["KIMI_SHELL_PATH"] == "C:\\Custom Git\\bin\\bash.exe", "Runtime Git Bash path was stripped");
                    return Task.CompletedTask;
                });
            }
            await Test("Qwen requires exact authentication metadata", async () =>
            {
                var backend = Backend("qwen", scenario: "no-metadata");
                await Throws<IOException>(() => backend.RunAsync("do not send", _ => { }, _ => { }, ct));
                Check(!Trace(backend).Any(row => Method(row) == "session/prompt"), "Unverified auth route used");
            });
            await Test("Kimi rejects legacy Python CLI dialect", async () =>
            {
                var backend = Backend("kimi", scenario: "legacy-kimi");
                await Throws<IOException>(() => backend.RunAsync("do not send", _ => { }, _ => { }, ct));
                Check(!Trace(backend).Any(row => Method(row) is "session/prompt" or "session/new"), "Unsupported CLI started a session");
            });
            await Test("authentication check never changes login or generates a prompt", async () =>
            {
                var backend = Backend("qwen", scenario: "auth-failure");
                var error = await Throws<IOException>(() => backend.ReadModelsCoreAsync(ct));
                Check(error.Message.Contains("/auth", StringComparison.Ordinal), "Missing login guidance");
                Check(!Trace(backend).Any(row => Method(row) is "session/prompt" or "authenticate"), "Auth check changed account or called a model");
            });
            await Test("Qwen grouped opaque IDs fail closed on absent or conflicting auth", () =>
            {
                var session = JsonSerializer.SerializeToElement(new
                {
                    configOptions = new[] { new { id = "model", type = "select", currentValue = "opaque", options = new[]
                    { new { value = "opaque", name = "Subscription" }, new { value = "paid(qwen-subscription)", name = "Misleading" },
                        new { value = "missing(qwen-subscription)", name = "Missing" }, new { value = "conflicting", name = "Conflicting" } } } },
                    models = new { availableModels = new object[]
                    { new { modelId = "opaque", _meta = new { authType = "qwen-subscription" } },
                        new { modelId = "paid(qwen-subscription)", _meta = new { authType = "openai" } },
                        new { modelId = "missing(qwen-subscription)" },
                        new { modelId = "conflicting", _meta = new { authType = "qwen-subscription" } },
                        new { modelId = "conflicting", _meta = new { authType = "openai" } } } }
                });
                var models = SubscriptionAcpProtocol.Models(session, SubscriptionCli.Qwen);
                Check(models.Count == 1 && models[0].Id == "opaque" && models[0].IsDefault, "Unverified or duplicate route passed");
                return Task.CompletedTask;
            });
            await Test("Kimi isolation preserves OAuth config and pins tools", () =>
            {
                var backend = Backend("kimi"); var info = backend.StartInfo();
                var home = info.Environment["KIMI_CODE_HOME"]!;
                var configPath = Path.Combine(home, "config.toml");
                File.WriteAllText(configPath, "default_model = \"kimi-code/fixture-default\"\n[tools]\nenabled = [\"Bash\"]\ndisabled = []\n[providers.kimi-code]\ntype = \"kimi\"\n");
                backend.StartInfo(); var config = File.ReadAllText(configPath);
                Check(config.Contains(KimiSubscriptionConfiguration.ToolsSection, StringComparison.Ordinal) && !config.Contains("Bash", StringComparison.Ordinal), "Tool policy not protected");
                Check(config.Contains("[providers.kimi-code]", StringComparison.Ordinal), "CLI-owned credential section changed");
                Check(KimiSubscriptionConfiguration.ProtectTools(config) == config, "Repeated preparation mutates unchanged config");
                File.WriteAllText(Path.Combine(home, "mcp.json"), "{}");
                try { backend.StartInfo(); throw new Exception("Extra MCP configuration accepted"); } catch (IOException) { }
                return Task.CompletedTask;
            });
            await Test("Kimi normalizes legal tools headers and rejects ambiguous TOML before launch", () =>
            {
                foreach (string header in new[] { "[tools]", "[ tools ]", "[\"tools\"]", "['tools']", "[\"\\u0074ools\"]" })
                {
                    string value = KimiSubscriptionConfiguration.ProtectTools(header + "\nenabled = [\"Bash\"]\n[providers.kimi-code]\ntype = \"kimi\"\n");
                    Check(value.Contains(KimiSubscriptionConfiguration.ToolsSection, StringComparison.Ordinal) &&
                        value.Contains("[providers.kimi-code]", StringComparison.Ordinal) && !value.Contains("Bash", StringComparison.Ordinal), "Legal table variant lost the policy or OAuth section");
                    Check(value == KimiSubscriptionConfiguration.ProtectTools(value), "Normalized table is not stable");
                }
                foreach (string invalid in new[]
                {
                    "[tools]\nenabled=[]\n[ tools ]\nenabled=[]\n", "[tools.enabled]\nBash = true\n",
                    "[[tools]]\nenabled=[]\n", "tools = { enabled = [\"Bash\"] }\n", "tools.enabled = [\"Bash\"]\n",
                    "[[\"hooks\"]]\ncommand = \"ignored\"\n"
                })
                {
                    try { KimiSubscriptionConfiguration.ProtectTools(invalid); throw new Exception("Ambiguous tools/hooks config accepted"); }
                    catch (IOException) { }
                }
                return Task.CompletedTask;
            });
            await Test("malformed unrelated Kimi TOML fails before process launch without exposing secrets", async () =>
            {
                var backend = Backend("kimi");
                var info = backend.StartInfo();
                var home = info.Environment["KIMI_CODE_HOME"]!;
                string path = Path.Combine(home, "config.toml");
                foreach (string malformed in new[] { "[providers.kimi-code]\napi_key = \"private-secret", "[unrelated]\nvalue=1\nvalue=2\n", "[unrelated]\nvalue=[1,\n" })
                {
                    string content = KimiSubscriptionConfiguration.ToolsSection + malformed;
                    File.WriteAllText(path, content);
                    var error = await Throws<IOException>(() => backend.RunAsync("must not launch", _ => { }, _ => { }, ct));
                    Check(!File.Exists(Path.Combine(info.WorkingDirectory, "acp-fixture.jsonl")), "Malformed config started the CLI");
                    Check(File.ReadAllText(path) == content, "Malformed config was rewritten");
                    Check(!error.ToString().Contains("private-secret", StringComparison.Ordinal), "Parser diagnostics exposed a credential");
                }
            });
            await Test("npm entry resolution never invokes a command shell", () =>
            {
                var folder = Path.Combine(Root, "path with spaces & percent% PATH");
                var package = Path.Combine(folder, "node_modules", "@qwen-code", "qwen-code"); Directory.CreateDirectory(package);
                var shim = Path.Combine(folder, "qwen.cmd"); File.WriteAllText(shim, "do not run");
                var manifest = Path.Combine(package, "package.json"); File.WriteAllText(manifest, "{\"bin\":{\"qwen\":\"cli.js\"}}");
                File.WriteAllText(Path.Combine(package, "cli.js"), ""); var node = Path.Combine(folder, OperatingSystem.IsWindows() ? "node.exe" : "node"); File.WriteAllText(node, "");
                var backend = new SubscriptionAcpBackend(new("qwen", shim, Root)); var info = backend.StartInfo();
                Check(info.FileName == node && info.ArgumentList[0] == Path.Combine(package, "cli.js") && !info.UseShellExecute, "npm launch uses shell interpolation");
                Check(info.ArgumentList.Contains("qwen-subscription") && info.ArgumentList.Contains("--bare") && !info.ArgumentList.Contains("--yolo"), "Unsafe CLI flags");
                Check(new[] { "exec", "run_shell_command", "agent", "web_fetch", "write_file", "read_mcp_resource" }.All(info.ArgumentList.Contains), "Qwen built-in execution route remains enabled");
                File.WriteAllText(manifest, "{\"bin\":{\"qwen\":\"../../../escape.js\"}}");
                try { backend.StartInfo(); throw new Exception("Entry point escaped package"); } catch (IOException) { }
                return Task.CompletedTask;
            });
            await Test("loopback MCP binding rejects external destinations before process launch", async () =>
            {
                var backend = new SubscriptionAcpBackend(new("qwen", Executable, Root, McpUrl: "https://external.invalid/mcp", McpToken: "fixture-secret"));
                await Throws<ArgumentException>(() => backend.RunAsync("do not send", _ => { }, _ => { }, ct));
            });
            Console.WriteLine($"Subscription checks passed: {passed}. No accounts, model requests, or Revit runtime used.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDEREVIT_SUBSCRIPTION_SCENARIO", scenario);
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static SubscriptionAcpBackend Backend(string agent, string? model = null, string scenario = "normal")
    {
        Environment.SetEnvironmentVariable("CLAUDEREVIT_SUBSCRIPTION_SCENARIO", scenario);
        var cwd = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(cwd);
        return new SubscriptionAcpBackend(new(agent, Executable, cwd, model, McpUrl: "http://127.0.0.1:8788/mcp?channel=fixture",
            McpToken: "fixture-secret", AllowedTools: new[] { "create_wall" }), Path.Combine(cwd, "kimi-home"));
    }
    private static JsonElement[] Trace(SubscriptionAcpBackend backend) => File.ReadAllLines(Path.Combine(backend.StartInfo().WorkingDirectory, "acp-fixture.jsonl"))
        .Select(line => { using var json = JsonDocument.Parse(line); return json.RootElement.Clone(); }).ToArray();
    private static string? Method(JsonElement row) => row.GetProperty("direction").GetString() == "in" && row.GetProperty("value").TryGetProperty("method", out var method) ? method.GetString() : null;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Test(string name, Func<Task> body) { await body(); passed++; Console.WriteLine("PASS " + name); }
    private static async Task<T> Throws<T>(Func<Task> body) where T : Exception
    { try { await body(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
}
