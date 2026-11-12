using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

public sealed class PluginSettings : IRevitTool
{
    public string Name => "plugin_settings";
    public string Description => "Read or update plugin settings (non-secret preferences only). action=get lists supported names and values; action=update takes a changes object; get_agent/set_agent read or save agent/model/effort independently for codex or claudecode. Never returns API keys or MCP tokens. Changes are blocked during a benchmark. ChatUiMode requires restarting Revit.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => new() { Properties = new Dictionary<string, JsonElement>
    {
        ["action"] = JsonSerializer.SerializeToElement(new { type = "string", @enum = new[] { "get", "update", "get_agent", "set_agent" } }),
        ["agent"] = JsonSerializer.SerializeToElement(new { type = "string", @enum = new[] { "codex", "claudecode" } }),
        ["model"] = JsonSerializer.SerializeToElement(new { type = "string" }),
        ["effort"] = JsonSerializer.SerializeToElement(new { type = "string" }),
        ["changes"] = JsonSerializer.SerializeToElement(new { type = "object", description = "Setting name/value pairs. Call get first for supported names." })
    }, Required = ["action"] };
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var action = ToolInput.RequiredString(input, "action");
        if (action == "get") return JsonSerializer.Serialize(AgentSettings.Read());
        if (action is "get_agent" or "set_agent")
        {
            var agent = ToolInput.RequiredString(input, "agent");
            if (agent is not ("codex" or "claudecode")) throw new ArgumentException("Use codex or claudecode.");
            if (action == "get_agent") return JsonSerializer.Serialize(SettingsStore.GetAgentSelection(agent));
            if (BenchmarkRunner.IsRunning || BenchmarkControl.Pending) throw new InvalidOperationException("Wait until the benchmark stops before changing settings.");
            var model = input.ContainsKey("model") ? ToolInput.RequiredString(input, "model") : null;
            var effort = input.ContainsKey("effort") ? ToolInput.RequiredString(input, "effort") : null;
            if (model?.Length > 200 || effort?.Length > 40) throw new ArgumentException("Invalid model or effort.");
            SettingsStore.SaveAgentSelection(new(agent, model, effort));
            return JsonSerializer.Serialize(SettingsStore.GetAgentSelection(agent));
        }
        if (action != "update") throw new ArgumentException("Use get, update, get_agent or set_agent.");
        if (BenchmarkRunner.IsRunning || BenchmarkControl.Pending) throw new InvalidOperationException("Wait until the benchmark stops before changing settings.");
        if (!input.TryGetValue("changes", out var changes)) throw new ArgumentException("changes is required.");
        return JsonSerializer.Serialize(AgentSettings.Update(changes));
    }
}

public sealed class BenchmarkControl : IRevitTool
{
    private static int _pending;
    public static bool Pending => Volatile.Read(ref _pending) != 0;
    private static string? _error;
    private static CancellationTokenSource? _queuedCancellation;
    public string Name => "benchmark_control";
    public string Description => "Benchmark management: tasks lists task IDs/prompts; start runs selected task_ids asynchronously in disposable copies of a saved local scratch RVT/RFA; status polls progress and log path; stop cancels and restores the seed; logs lists run files; read_log pages JSONL diagnostics by run_id/offset/limit. Modeller and judge have independent backend/model/effort. Defaults: codex, 24 rounds, 300 seconds per task. Requires an active document. Poll status after start. Logs contain model data and generated text.";
    public bool RequiresTransaction => false;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => new() { Properties = new Dictionary<string, JsonElement>
    {
        ["action"] = JsonSerializer.SerializeToElement(new { type = "string", @enum = new[] { "tasks", "start", "status", "stop", "logs", "read_log" } }),
        ["task_ids"] = JsonSerializer.SerializeToElement(new { type = "array", items = new { type = "string" }, description = "Required for start; explicit nonempty task IDs." }),
        ["backend"] = StringSchema("codex, claudecode or api"), ["model"] = StringSchema("Modeller model ID"), ["effort"] = StringSchema("Modeller effort"),
        ["judge_backend"] = StringSchema("codex, claudecode or api"), ["judge_model"] = StringSchema("Judge model ID"), ["judge_effort"] = StringSchema("Judge effort"),
        ["max_rounds"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "1..1000, default 24" }),
        ["max_seconds"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "1..36000, default 300" }),
        ["run_id"] = StringSchema("Run ID from status or logs"),
        ["offset"] = JsonSerializer.SerializeToElement(new { type = "integer" }), ["limit"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "1..100, default 20" })
    }, Required = ["action"] };
    private static JsonElement StringSchema(string description) => JsonSerializer.SerializeToElement(new { type = "string", description });
    private static string? Text(IReadOnlyDictionary<string, JsonElement> input, string key) => input.ContainsKey(key) ? ToolInput.RequiredString(input, key) : null;
    private static int Number(IReadOnlyDictionary<string, JsonElement> input, string key, int fallback) => input.ContainsKey(key) ? ToolInput.RequiredInt(input, key) : fallback;
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var action = ToolInput.RequiredString(input, "action");
        var taskId = McpSession.Executing?.ChannelId is { } channelId ? McpTurnChannel.Find(channelId)?.TaskId : TaskJournal.CurrentId;
        if (action is "start" or "stop" && TaskJournal.IsCaptured(taskId))
            throw new InvalidOperationException("The benchmark modeller cannot control its own benchmark run.");
        object result;
        switch (action)
        {
            case "tasks": result = BenchmarkTasks.All; break;
            case "status": result = new { running = BenchmarkRunner.IsRunning, pending = Pending, status = BenchmarkRunner.Status, run_id = BenchmarkRunner.LastRunId, log_path = BenchmarkRunner.LastLogPath, error = _error ?? BenchmarkRunner.LastError }; break;
            case "stop": _queuedCancellation?.Cancel(); BenchmarkRunner.RequestStop(); result = new { stop_requested = true, pending = Pending }; break;
            case "logs": result = System.IO.Directory.Exists(BenchmarkLog.DirectoryName)
                ? System.IO.Directory.EnumerateFiles(BenchmarkLog.DirectoryName, "*.jsonl").OrderByDescending(System.IO.File.GetLastWriteTimeUtc).Take(100).Select(p => new { run_id = System.IO.Path.GetFileNameWithoutExtension(p), bytes = new System.IO.FileInfo(p).Length, path = p }).ToArray() : (object)Array.Empty<object>(); break;
            case "read_log": result = BenchmarkLog.Read(ToolInput.RequiredString(input, "run_id"), Number(input, "offset", 0), Number(input, "limit", 20)); break;
            case "start":
                if (!input.TryGetValue("task_ids", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0) throw new ArgumentException("Provide nonempty task_ids; use tasks to list IDs.");
                var tasks = ids.EnumerateArray().Select(id => BenchmarkTasks.All.FirstOrDefault(t => t.Id == id.GetString()) ?? throw new ArgumentException("Unknown task ID: " + id)).ToArray();
                var execution = new BenchmarkExecution(Text(input, "backend") ?? "codex", Text(input, "model"), Text(input, "effort"));
                var judge = new BenchmarkExecution(Text(input, "judge_backend") ?? "codex", Text(input, "judge_model"), Text(input, "judge_effort"));
                _ = execution.Tag; _ = judge.Tag;
                var rounds = Number(input, "max_rounds", 24); var seconds = Number(input, "max_seconds", 300);
                if (rounds is < 1 or > 1000 || seconds is < 1 or > 36000) throw new ArgumentException("Use max_rounds 1..1000 and max_seconds 1..36000.");
                var doc = app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("Open a saved local scratch document.");
                if (doc.IsModified || doc.IsWorkshared || doc.IsModelInCloud || !System.IO.File.Exists(doc.PathName)) throw new InvalidOperationException("Save a local, non-workshared scratch RVT/RFA with no unsaved edits first.");
                if (BenchmarkRunner.IsRunning || Interlocked.CompareExchange(ref _pending, 1, 0) != 0) throw new InvalidOperationException("A benchmark is already running or queued.");
                _error = null;
                var queuedCancellation = new CancellationTokenSource();
                _queuedCancellation = queuedCancellation;
                // Start after this tool's dispatcher job completes; never block Revit's API thread.
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
                {
                    try { queuedCancellation.Token.ThrowIfCancellationRequested(); await BenchmarkRunner.RunAsync(execution, tasks, judge, DateTime.UtcNow.ToString("O"), true, rounds, seconds, _ => { }, _ => { }, queuedCancellation.Token); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _error = ex.ToString(); }
                    finally { _queuedCancellation = null; queuedCancellation.Dispose(); Interlocked.Exchange(ref _pending, 0); }
                }));
                result = new { accepted = true, task_ids = tasks.Select(t => t.Id), next = "Poll status for run_id and completion." }; break;
            default: throw new ArgumentException("Unknown benchmark action.");
        }
        return JsonSerializer.Serialize(result);
    }
}
