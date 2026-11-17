using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeRevit.Services;

public sealed record SubscriptionProviderOptions(string Agent, string Executable, string WorkingDirectory,
    string? Model = null, string? Effort = null, string? McpUrl = null, string? McpToken = null,
    IReadOnlyCollection<string>? AllowedTools = null);
public sealed record SubscriptionModel(string Id, string DisplayName, bool IsDefault, IReadOnlyList<string>? Efforts = null);
public sealed record SubscriptionImage(string MimeType, string Base64Data);
internal enum SubscriptionCli { Qwen, Kimi }
internal sealed record SubscriptionEvent(string Kind, string Text, string? Tool = null, string? Phase = null);

/// <summary>
/// Official Qwen/Kimi CLI subscription sessions over ACP. The model route is verified before every
/// prompt, including restored sessions. No paid API fallback or automatic prompt retry is performed.
/// </summary>
public sealed class SubscriptionAcpBackend
{
    private readonly SubscriptionProviderOptions options;
    private readonly SubscriptionCli cli;
    private readonly string kimiHome;
    private int sending;
    private string Name => cli == SubscriptionCli.Qwen ? "Qwen Code" : "Kimi Code";
    public string? SessionId { get; set; }
    internal TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(90);
    internal TimeSpan EventSilenceTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public sealed record Result(string Text, string? SessionId, bool Completed, string? Model);

    public SubscriptionAcpBackend(SubscriptionProviderOptions options) : this(options, KimiSubscriptionConfiguration.Home) { }
    internal SubscriptionAcpBackend(SubscriptionProviderOptions options, string kimiHome)
    {
        this.options = options;
        cli = options.Agent switch { "qwen" => SubscriptionCli.Qwen, "kimi" => SubscriptionCli.Kimi,
            _ => throw new ArgumentException("Unsupported subscription provider: " + options.Agent, nameof(options)) };
        this.kimiHome = kimiHome;
    }

    // Qwen's --bare excludes project customization but does not disable built-in tools.
    // --exclude-tools is exact-name matching ("*" is not a wildcard here). Keep only MCP
    // discovery/dispatch infrastructure; ACP approvals additionally check each concrete Revit tool.
    // Names verified against @qwen-code/qwen-code 0.28.0 packages/core/src/tools/tool-names.ts.
    private static readonly string[] QwenExcludedTools =
    [
        "exec", "edit", "write_file", "read_file", "zoom_image", "grep_search", "glob", "run_shell_command",
        "todo_write", "save_memory", "manage_memory", "search_memory", "agent", "skill", "exit_plan_mode",
        "enter_plan_mode", "web_fetch", "web_search", "image_gen", "list_directory", "lsp", "ask_user_question",
        "cron_create", "cron_list", "cron_delete", "autofix_pr", "loop_wakeup", "create_sub_session", "list_agents",
        "task_stop", "task_create", "task_update", "task_list", "team_create", "team_delete", "team_plan_approval",
        "request_shutdown", "send_message", "structured_output", "monitor", "notebook_edit", "read_mcp_resource",
        "advisor", "enter_worktree", "exit_worktree", "workflow", "artifact", "record_artifact", "record_source",
        "report_findings", "get_goal", "update_goal", "propose_goal", "display_image", "omni_downsample_image",
        "omni_downscale_video", "omni_downsample_audio", "omni_extract_keyframes", "omni_extract_audio", "omni_clip_video",
        "omni_convert_image", "omni_transcribe_audio", "omni_clip_image", "omni_clip_audio", "omni_caption_image",
        "omni_caption_audio", "omni_ocr_image", "omni_understand_video_segments", "omni_recall_media_memory"
    ];

    internal string[] Arguments() => cli == SubscriptionCli.Qwen
        ? ["--acp", "--bare", "--auth-type", "qwen-subscription", "--approval-mode", "default",
            "--allowed-mcp-server-names", "clauderevit", "--exclude-tools", .. QwenExcludedTools]
        : ["acp"];

    internal ProcessStartInfo StartInfo(bool login = false)
    {
        var launch = options;
        if (cli == SubscriptionCli.Kimi)
            launch = options with { WorkingDirectory = KimiSubscriptionConfiguration.Prepare(kimiHome, options.WorkingDirectory) };
        else Directory.CreateDirectory(launch.WorkingDirectory);
        var args = login ? (cli == SubscriptionCli.Qwen ? new[] { "--auth-type", "qwen-subscription" } : new[] { "login" }) : Arguments();
        var info = SubscriptionProcess.Create(launch, args,
            cli == SubscriptionCli.Qwen ? "@qwen-code/qwen-code" : "@moonshot-ai/kimi-code",
            cli == SubscriptionCli.Qwen ? "qwen" : "kimi", interactive: login);
        if (cli == SubscriptionCli.Kimi) KimiSubscriptionConfiguration.ApplyEnvironment(info, kimiHome);
        return info;
    }

    public static ProcessStartInfo CreateLoginStartInfo(SubscriptionProviderOptions options) => new SubscriptionAcpBackend(options).StartInfo(login: true);
    public static Task<IReadOnlyList<SubscriptionModel>> ReadModelsAsync(SubscriptionProviderOptions options, CancellationToken ct) =>
        new SubscriptionAcpBackend(options).ReadModelsCoreAsync(ct);
    public static async Task<string> CheckConnectionAsync(SubscriptionProviderOptions options, CancellationToken ct)
    {
        var models = await ReadModelsAsync(options, ct).ConfigureAwait(false);
        var current = models.FirstOrDefault(model => model.IsDefault);
        if (current == null) throw new IOException("The CLI did not confirm a current subscription model. Select a subscription model before sending a prompt.");
        return (options.Agent == "qwen" ? "Qwen Code" : "Kimi Code") + ": subscription route confirmed; " + models.Count +
            " models available. Current model: " + current.DisplayName + ". No model request was made.";
    }

    public async Task<Result> RunAsync(string prompt, Action<string> onText, Action<string> onTool, CancellationToken ct,
        IReadOnlyList<SubscriptionImage>? images = null, Action<string>? onStatus = null)
    {
        if (Interlocked.Exchange(ref sending, 1) != 0) throw new InvalidOperationException("A turn is already running for this provider.");
        try
        {
            ct.ThrowIfCancellationRequested();
            ValidateMcp();
            var text = new StringBuilder();
            string? model = null;
            Task Emit(SubscriptionEvent item)
            {
                if (item.Kind == "text")
                {
                    if (text.Length + item.Text.Length > 4 * 1024 * 1024) throw new IOException("The CLI response exceeded the supported text size.");
                    text.Append(item.Text); onText(item.Text);
                }
                else if (item.Kind == "step" && item.Tool != null) onTool(item.Tool);
                else if (item.Kind == "model") model = item.Text;
                else if (item.Kind is "status" or "problem") onStatus?.Invoke(item.Text);
                return Task.CompletedTask;
            }
            var start = StartInfo();
            await using var connection = new SubscriptionAcpConnection(start, Name, cli, Emit, StartupTimeout,
                EventSilenceTimeout, options.AllowedTools ?? Array.Empty<string>(), options.McpToken);
            var initialization = await InitializeAsync(connection, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(options.McpUrl) &&
                SubscriptionJson.Child(SubscriptionJson.Child(SubscriptionJson.Child(initialization, "agentCapabilities"), "mcpCapabilities"), "http").ValueKind != JsonValueKind.True)
                throw new IOException(Name + " does not advertise HTTP MCP support. Update the official CLI; no model request was made.");
            images ??= Array.Empty<SubscriptionImage>();
            if (images.Count > 0 &&
                SubscriptionJson.Child(SubscriptionJson.Child(SubscriptionJson.Child(initialization, "agentCapabilities"), "promptCapabilities"), "image").ValueKind != JsonValueKind.True)
                throw new IOException(Name + " does not advertise image support through ACP; attachments were not sent.");
            var blocks = PromptBlocks(prompt, images);
            string? saved = SessionId;
            if (saved is not null && SubscriptionJson.Child(SubscriptionJson.Child(initialization, "agentCapabilities"), "loadSession").ValueKind != JsonValueKind.True)
                throw new IOException(Name + " cannot restore this conversation through ACP. Update the official CLI.");
            // Replayed history is ignored until session/load and model checks have both completed.
            var session = await connection.RequestAsync(saved is null ? "session/new" : "session/load",
                SessionParameters(start.WorkingDirectory, includeMcp: true, saved), ct).ConfigureAwait(false);
            string id = saved ?? SubscriptionJson.Text(session, "sessionId") ?? throw new IOException(Name + " did not return a session ID.");
            if (saved is not null && SubscriptionJson.Text(session, "sessionId") is { } returned && returned != saved)
                throw new IOException(Name + " restored a different session; the prompt was not sent.");
            SessionId = id;
            connection.SessionId = id;
            await SetManualModeAsync(connection, session, ct).ConfigureAwait(false);
            session = await SelectModelAsync(connection, session, ct).ConfigureAwait(false);
            model = SubscriptionAcpProtocol.CurrentModel(session);
            if (!string.IsNullOrWhiteSpace(options.Effort))
                await SelectEffortAsync(connection, session, options.Effort, ct).ConfigureAwait(false);
            connection.DeliverUpdates = true;
            connection.PromptActive = true;
            var response = await connection.RequestAsync("session/prompt", new { sessionId = id, prompt = blocks }, ct, prompt: true).ConfigureAwait(false);
            connection.PromptActive = false;
            string? stop = SubscriptionJson.Text(response, "stopReason");
            if (stop != "end_turn")
            {
                ct.ThrowIfCancellationRequested();
                throw new IOException(Name + " did not finish the turn: " + (stop ?? "missing stopReason") + ". Check Revit before retrying; no automatic retry was made.");
            }
            ct.ThrowIfCancellationRequested();
            return new Result(text.ToString(), id, true, model);
        }
        finally { Volatile.Write(ref sending, 0); }
    }

    private void ValidateMcp()
    {
        if (string.IsNullOrWhiteSpace(options.McpUrl))
        {
            if (!string.IsNullOrWhiteSpace(options.McpToken) || options.AllowedTools?.Count > 0)
                throw new ArgumentException("MCP URL is required when supplying tools or a bearer token.");
            return;
        }
        if (!Uri.TryCreate(options.McpUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != Uri.UriSchemeHttp ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || string.IsNullOrWhiteSpace(options.McpToken))
            throw new ArgumentException("The Revit MCP endpoint must be a loopback HTTP URL with a bearer token.");
    }

    private async Task<JsonElement> InitializeAsync(SubscriptionAcpConnection connection, CancellationToken ct)
    {
        var result = await connection.RequestAsync("initialize", new
        {
            protocolVersion = 1,
            clientInfo = new { name = "ClaudeRevit", version = "1" },
            clientCapabilities = new { fs = new { readTextFile = false, writeTextFile = false }, terminal = false }
        }, ct).ConfigureAwait(false);
        if (SubscriptionJson.Child(result, "protocolVersion") is not { ValueKind: JsonValueKind.Number } version || !version.TryGetInt32(out int n) || n != 1)
            throw new IOException(Name + " returned an unsupported ACP version. Update the official CLI.");
        if (cli == SubscriptionCli.Kimi && SubscriptionJson.Text(SubscriptionJson.Child(result, "agentInfo"), "name") != "Kimi Code CLI")
            throw new IOException("The current Node.js Kimi Code CLI from @moonshot-ai/kimi-code is required. Legacy Python kimi-cli is not supported.");
        return result;
    }

    private object SessionParameters(string cwd, bool includeMcp, string? saved = null)
    {
        object[] servers = includeMcp && !string.IsNullOrWhiteSpace(options.McpUrl) ? [new
        {
            type = "http", name = "clauderevit", url = options.McpUrl,
            headers = new[] { new { name = "Authorization", value = "Bearer " + options.McpToken } }
        }] : [];
        if (saved is null) return new { cwd, mcpServers = servers };
        return new { sessionId = saved, cwd, mcpServers = servers };
    }

    private async Task SetManualModeAsync(SubscriptionAcpConnection connection, JsonElement session, CancellationToken ct)
    {
        var modes = SubscriptionJson.Child(SubscriptionJson.Child(session, "modes"), "availableModes");
        if (modes.ValueKind != JsonValueKind.Array || !modes.EnumerateArray().Any(mode => SubscriptionJson.Text(mode, "id") == "default"))
            throw new IOException(Name + " did not advertise the manual ACP permission mode. The prompt was not sent.");
        await connection.RequestAsync("session/set_mode", new { sessionId = connection.SessionId, modeId = "default" }, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> SelectModelAsync(SubscriptionAcpConnection connection, JsonElement session, CancellationToken ct)
    {
        var models = SubscriptionAcpProtocol.Models(session, cli);
        string? selected = options.Model;
        if (string.IsNullOrWhiteSpace(selected))
        {
            string? current = SubscriptionAcpProtocol.CurrentModel(session);
            if (current is null || !models.Any(m => m.Id == current))
                throw new IOException(Name + " did not confirm that the current model uses the subscription. Select a subscription model; no model request was made.");
            return session;
        }
        if (!models.Any(model => model.Id == selected))
            throw new IOException(Name + ": selected model is not available through the subscription: " + selected);
        if (SubscriptionAcpProtocol.FindOption(session, "model") is { } option)
        {
            var changed = await connection.RequestAsync("session/set_config_option", new { sessionId = connection.SessionId,
                configId = SubscriptionJson.Text(option, "id"), value = selected }, ct).ConfigureAwait(false);
            if (SubscriptionAcpProtocol.CurrentModel(changed) != selected)
                throw new IOException(Name + " did not confirm the requested model.");
            // ACP set_config_option may omit legacy metadata. The selected exact opaque ID was
            // already authenticated by this session's catalog; retain that catalog for effort UI.
            var merged = MergeModelSelection(session, changed);
            if (!SubscriptionAcpProtocol.Models(merged, cli).Any(candidate => candidate.Id == selected))
                throw new IOException(Name + " changed the selected model authentication route. The prompt was not sent.");
            return merged;
        }
        await connection.RequestAsync("session/set_model", new { sessionId = connection.SessionId, modelId = selected }, ct).ConfigureAwait(false);
        var node = System.Text.Json.Nodes.JsonNode.Parse(session.GetRawText())!;
        node["models"]!["currentModelId"] = selected;
        return JsonSerializer.SerializeToElement(node);
    }

    private static JsonElement MergeModelSelection(JsonElement prior, JsonElement changed)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(prior.GetRawText())!;
        foreach (var property in changed.EnumerateObject()) node[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
        return JsonSerializer.SerializeToElement(node);
    }

    private async Task SelectEffortAsync(SubscriptionAcpConnection connection, JsonElement session, string effort, CancellationToken ct)
    {
        var option = SubscriptionAcpProtocol.FindOption(session, "thought_level");
        if (option is null || !SubscriptionAcpProtocol.OptionValues(option.Value).Any(v => v.Id == effort))
            throw new IOException(Name + " did not advertise the requested reasoning effort: " + effort);
        await connection.RequestAsync("session/set_config_option", new { sessionId = connection.SessionId,
            configId = SubscriptionJson.Text(option.Value, "id"), value = effort }, ct).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<SubscriptionModel>> ReadModelsCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = StartInfo();
        await using var connection = new SubscriptionAcpConnection(start, Name, cli, _ => Task.CompletedTask, StartupTimeout,
            EventSilenceTimeout, Array.Empty<string>(), options.McpToken);
        await InitializeAsync(connection, ct).ConfigureAwait(false);
        // Authenticated metadata only: no model inference or Revit tools are requested.
        var session = await connection.RequestAsync("session/new", SessionParameters(start.WorkingDirectory, includeMcp: false), ct).ConfigureAwait(false);
        connection.SessionId = SubscriptionJson.Text(session, "sessionId");
        var models = SubscriptionAcpProtocol.Models(session, cli);
        if (models.Count == 0) throw new IOException(Name + " returned no verified subscription models. Sign in with the official CLI.");
        return models;
    }

    internal static IReadOnlyList<object> PromptBlocks(string prompt, IReadOnlyList<SubscriptionImage> images)
    {
        var result = new List<object> { new { type = "text", text = prompt } };
        long total = 0;
        foreach (var image in images)
        {
            if (image.MimeType is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif"))
                throw new ArgumentException("Unsupported image media type.");
            total += image.Base64Data.Length;
            if (total > 28 * 1024 * 1024) throw new ArgumentException("Image attachments exceed the supported total size.");
            // Validate, but never read a model-supplied file path from the local filesystem.
            _ = Convert.FromBase64String(image.Base64Data);
            result.Add(new { type = "image", mimeType = image.MimeType, data = image.Base64Data });
        }
        return result;
    }
}

internal static class SubscriptionAcpProtocol
{
    private const string RevitPrefix = "mcp__clauderevit__";

    internal static string? CurrentModel(JsonElement session)
    {
        var option = FindOption(session, "model");
        return option is null ? SubscriptionJson.Text(SubscriptionJson.Child(session, "models"), "currentModelId") : SubscriptionJson.Text(option.Value, "currentValue");
    }

    internal static JsonElement? FindOption(JsonElement session, string category)
    {
        var options = SubscriptionJson.Child(session, "configOptions");
        if (options.ValueKind != JsonValueKind.Array) return null;
        foreach (var option in options.EnumerateArray())
            if (SubscriptionJson.Text(option, "type") == "select" && (SubscriptionJson.Text(option, "category") == category || SubscriptionJson.Text(option, "id") == category)) return option;
        return null;
    }

    internal static IReadOnlyList<SubscriptionModel> Models(JsonElement session, SubscriptionCli cli)
    {
        string? current = CurrentModel(session);
        var option = FindOption(session, "model");
        var available = SubscriptionJson.Child(SubscriptionJson.Child(session, "models"), "availableModels");
        // Qwen advertises ALL configured providers even when started with --auth-type
        // qwen-subscription; choosing an API-key route switches authentication. Its model picker
        // options carry opaque IDs, while the accompanying models table supplies trusted authType.
        // Join by that exact ID, failing closed when the route has no subscription metadata.
        var qwenSubscriptions = new HashSet<string>(StringComparer.Ordinal);
        if (cli == SubscriptionCli.Qwen && available.ValueKind == JsonValueKind.Array)
            foreach (var group in available.EnumerateArray().Where(m => SubscriptionJson.Text(m, "modelId") is { Length: > 0 })
                .GroupBy(m => SubscriptionJson.Text(m, "modelId")!, StringComparer.Ordinal))
                if (group.All(m => SubscriptionJson.Text(SubscriptionJson.Child(m, "_meta"), "authType") == "qwen-subscription"))
                    qwenSubscriptions.Add(group.Key);
        IEnumerable<SubscriptionModel> models;
        if (option is not null) models = OptionValues(option.Value);
        else
        {
            models = available.ValueKind == JsonValueKind.Array ? available.EnumerateArray()
                .Where(m => SubscriptionJson.Text(m, "modelId") is { Length: > 0 })
                .Select(m => new SubscriptionModel(SubscriptionJson.Text(m, "modelId")!, SubscriptionJson.Text(m, "name") ?? SubscriptionJson.Text(m, "modelId")!, false)) : [];
        }
        var effortOption = FindOption(session, "thought_level");
        var efforts = effortOption is null ? Array.Empty<string>() : OptionValues(effortOption.Value).Select(m => m.Id).ToArray();
        return models.Where(m => cli == SubscriptionCli.Qwen ? qwenSubscriptions.Contains(m.Id) : m.Id.StartsWith("kimi-code/", StringComparison.Ordinal))
            .DistinctBy(m => m.Id).Select(m => m with { IsDefault = m.Id == current, Efforts = efforts }).ToArray();
    }

    internal static IEnumerable<SubscriptionModel> OptionValues(JsonElement option)
    {
        var values = SubscriptionJson.Child(option, "options");
        if (values.ValueKind != JsonValueKind.Array) yield break;
        foreach (var value in values.EnumerateArray())
        {
            if (SubscriptionJson.Text(value, "value") is { Length: > 0 } id)
                yield return new(id, SubscriptionJson.Text(value, "name") ?? id, false);
            else foreach (var nested in OptionValues(value)) yield return nested;
        }
    }

    internal static string? PermissionTool(JsonElement toolCall, SubscriptionCli cli, IReadOnlySet<string> allowedTools)
    {
        var meta = SubscriptionJson.Child(toolCall, "_meta");
        string? name = SubscriptionJson.Text(meta, "toolName");
        // Qwen supplies canonical identity in _meta.toolName; its title is model-facing prose.
        // Kimi's current official ACP approval.ts sets title directly to req.toolName and supplies
        // no identity metadata. This narrowly scoped fallback is only for that checked CLI dialect.
        if (cli == SubscriptionCli.Kimi)
        {
            if (SubscriptionJson.Text(toolCall, "kind") is { } kind && kind != "other") return null;
            string? title = SubscriptionJson.Text(toolCall, "title");
            if (name is not null && name != title) return null;
            name = title;
        }
        if (name is null || !name.StartsWith(RevitPrefix, StringComparison.Ordinal)) return null;
        string tool = name[RevitPrefix.Length..];
        if (!allowedTools.Contains(tool)) return null;
        if (SubscriptionJson.Text(meta, "serverId") is { } server && server != "clauderevit") return null;
        if (SubscriptionJson.Text(meta, "provenance") is { } provenance && provenance != "mcp") return null;
        return tool;
    }

    internal static object PermissionResponse(JsonElement parameters, SubscriptionCli cli, string? activeSession, bool prompting, IReadOnlySet<string> allowedTools)
    {
        bool allow = prompting && activeSession is not null && SubscriptionJson.Text(parameters, "sessionId") == activeSession &&
            PermissionTool(SubscriptionJson.Child(parameters, "toolCall"), cli, allowedTools) is not null;
        var options = SubscriptionJson.Child(parameters, "options");
        if (options.ValueKind == JsonValueKind.Array)
        {
            string kind = allow ? "allow_once" : "reject_once";
            foreach (var option in options.EnumerateArray())
                if (SubscriptionJson.Text(option, "kind") == kind && SubscriptionJson.Text(option, "optionId") is { Length: > 0 } id)
                    return new { outcome = new { outcome = "selected", optionId = id } };
        }
        return new { outcome = new { outcome = "cancelled" } };
    }
}

/// <summary>One JSONL ACP connection. Reverse RPC is restricted to per-call Revit approvals.</summary>
internal sealed class SubscriptionAcpConnection : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> stderr;
    private readonly SubscriptionLines lines;
    private readonly string name;
    private readonly IReadOnlySet<string> allowedTools;
    private readonly string? secret;
    private readonly SubscriptionCli cli;
    private readonly Func<SubscriptionEvent, Task> emit;
    private readonly TimeSpan startupTimeout, silenceTimeout;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly HashSet<string> reportedTools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> toolNames = new(StringComparer.Ordinal);
    private readonly Task reading;
    private Exception? readFailure;
    private int next;
    private int promptRequestId;
    internal string? SessionId { get; set; }
    internal bool DeliverUpdates { get; set; }
    internal bool PromptActive { get; set; }

    internal SubscriptionAcpConnection(ProcessStartInfo start, string name, SubscriptionCli cli, Func<SubscriptionEvent, Task> emit,
        TimeSpan startupTimeout, TimeSpan silenceTimeout, IEnumerable<string> allowedTools, string? secret)
    {
        this.allowedTools = allowedTools.ToHashSet(StringComparer.Ordinal); this.secret = secret;
        this.name = name; this.cli = cli; this.emit = emit; this.startupTimeout = startupTimeout; this.silenceTimeout = silenceTimeout;
        process = Process.Start(start) ?? throw new IOException("Не удалось запустить " + name);
        stderr = SubscriptionProcess.DrainErrors(process);
        lines = new SubscriptionLines(process.StandardOutput.BaseStream);
        reading = ReadLoopAsync();
    }

    internal async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct, bool prompt = false)
    {
        int id = Interlocked.Increment(ref next);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        if (prompt) Volatile.Write(ref promptRequestId, id);
        try
        {
            if (reading.IsCompleted) throw readFailure ?? new IOException(name + " закрыл соединение ACP.");
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, ct);
            async Task<JsonElement> Reply()
            {
                await Task.WhenAny(completion.Task, reading);
                if (completion.Task.IsCompleted) return await completion.Task;
                throw readFailure ?? new IOException(name + " закрыл соединение ACP.");
            }
            return prompt ? await Reply().WaitAsync(ct) : await Reply().WaitAsync(startupTimeout, ct);
        }
        catch (TimeoutException error) { throw new IOException(name + " не ответил на запрос ACP вовремя. Автоматического повтора нет.", error); }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task WriteAsync(object value, CancellationToken ct)
    {
        await writes.WaitAsync(ct);
        try { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), ct); await process.StandardInput.FlushAsync(ct); }
        finally { writes.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                // Handshake requests have their own startup deadline in RequestAsync. A read may
                // begin between those replies and the prompt, so never capture the shorter startup
                // budget here: the first model token can legitimately take longer than CLI startup.
                deadline.CancelAfter(silenceTimeout);
                JsonDocument? json;
                try { json = await lines.ReadAsync(deadline.Token); }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                { throw new IOException(name + " слишком долго не передавал события. Проверьте квитанции Revit; автоматического повтора нет."); }
                if (json is null) { await ThrowClosedAsync(); break; }
                using (json)
                {
                    var root = json.RootElement;
                    string? method = SubscriptionJson.Text(root, "method");
                    var parameters = SubscriptionJson.Child(root, "params");
                    if (method is not null)
                    {
                        if (SubscriptionJson.Child(root, "id") is { ValueKind: JsonValueKind.Number or JsonValueKind.String } requestId)
                            await HandleRequestAsync(requestId, method, parameters);
                        else if (method == "session/update" && DeliverUpdates && SubscriptionJson.Text(parameters, "sessionId") == SessionId)
                            await UpdateAsync(SubscriptionJson.Child(parameters, "update"));
                        continue;
                    }
                    if (SubscriptionJson.Id(root) is { } id && pending.TryRemove(id, out var completion))
                    {
                        if (id == Volatile.Read(ref promptRequestId))
                        {
                            // Freeze the final text before waking RunAsync; later notifications cannot
                            // race its result snapshot or add work after the terminal reply.
                            PromptActive = false;
                            DeliverUpdates = false;
                        }
                        if (SubscriptionJson.Child(root, "error") is { ValueKind: JsonValueKind.Object } error)
                        {
                            string text = SubscriptionJson.Text(error, "message") ?? "ACP error";
                            if (SubscriptionJson.Child(error, "code").TryGetInt32Safe() is -32000 or 401)
                                text += cli == SubscriptionCli.Kimi ? ". Выполните вход Kimi Code из ClaudeRevit; требуется подписка Kimi." :
                                    ". Выполните /auth в Qwen Code и выберите Qwen Account with Subscription.";
                            completion.TrySetException(new IOException(name + ": " + Redact(text)));
                        }
                        else if (SubscriptionJson.Child(root, "result") is { ValueKind: not JsonValueKind.Undefined } result)
                            completion.TrySetResult(result.Clone());
                        else completion.TrySetException(new IOException(name + " вернул некорректный ответ ACP."));
                    }
                }
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            failure ??= new IOException(name + " закрыл соединение ACP до ответа. Автоматического повтора нет.");
            readFailure = failure;
            foreach (var item in pending.ToArray()) if (pending.TryRemove(item.Key, out var completion)) completion.TrySetException(failure);
        }
    }

    private async Task HandleRequestAsync(JsonElement id, string method, JsonElement parameters)
    {
        if (method == "session/request_permission")
        {
            string? tool = SubscriptionAcpProtocol.PermissionTool(SubscriptionJson.Child(parameters, "toolCall"), cli, allowedTools);
            object response = SubscriptionAcpProtocol.PermissionResponse(parameters, cli, SessionId, PromptActive, allowedTools);
            await WriteAsync(new { jsonrpc = "2.0", id, result = response }, lifetime.Token);
            if (PromptActive && SubscriptionJson.Text(parameters, "sessionId") == SessionId && tool is not null &&
                SubscriptionJson.Text(SubscriptionJson.Child(parameters, "toolCall"), "toolCallId") is { } callId)
                toolNames[callId] = tool;
            if (PromptActive && SubscriptionJson.Text(parameters, "sessionId") == SessionId &&
                SubscriptionAcpProtocol.PermissionTool(SubscriptionJson.Child(parameters, "toolCall"), cli, allowedTools) is null)
                await emit(new("problem", name + ": запрос встроенного или постороннего инструмента отклонён. В этом чате доступны только инструменты ClaudeRevit."));
            return;
        }
        await WriteAsync(new { jsonrpc = "2.0", id, error = new { code = -32601,
            message = "ClaudeRevit does not provide filesystem, terminal, or other client execution methods." } }, lifetime.Token);
    }

    private async Task UpdateAsync(JsonElement update)
    {
        string? type = SubscriptionJson.Text(update, "sessionUpdate");
        if (type == "agent_message_chunk")
        {
            var content = SubscriptionJson.Child(update, "content");
            if (SubscriptionJson.Text(content, "type") == "text" && SubscriptionJson.Text(content, "text") is { } text) await emit(new("text", text));
        }
        else if (type == "agent_thought_chunk")
        {
            // Do not display or persist raw provider reasoning; a liveness signal is sufficient.
            await emit(new("heartbeat", ""));
        }
        else if (type is "tool_call" or "tool_call_update")
        {
            string? id = SubscriptionJson.Text(update, "toolCallId");
            string? tool = SubscriptionAcpProtocol.PermissionTool(update, cli, allowedTools);
            if (id is not null && tool is not null) toolNames[id] = tool;
            if (tool is null && id is not null) toolNames.TryGetValue(id, out tool);
            if (tool is null) { await emit(new("heartbeat", "")); return; }
            string? status = SubscriptionJson.Text(update, "status");
            string phase = status is "completed" or "failed" ? status : "running";
            if (id is not null && reportedTools.Add(id)) await emit(new("step", tool + " (" + phase + ")", Tool: tool, Phase: phase));
            if (status == "failed") await emit(new("problem", name + ": ошибка инструмента " + tool + ". " + Redact(FailureText(update))));
        }
        else if (type == "config_option_update" && SubscriptionAcpProtocol.CurrentModel(update) is { } model)
            await emit(new("model", model));
        else if (type == "usage_update")
        {
            // ACP usage_update reports occupied context, not billable input/output tokens.
            var used = SubscriptionJson.Child(update, "used");
            var size = SubscriptionJson.Child(update, "size");
            if (used.ValueKind == JsonValueKind.Number && size.ValueKind == JsonValueKind.Number)
                await emit(new("status", name + ": контекст " + used + " / " + size));
        }
        else await emit(new("heartbeat", ""));
    }

    private static string FailureText(JsonElement update)
    {
        var content = SubscriptionJson.Child(update, "content");
        if (content.ValueKind != JsonValueKind.Array) return "";
        string text = string.Join("\n", content.EnumerateArray().Select(item => SubscriptionJson.Text(SubscriptionJson.Child(item, "content"), "text")).OfType<string>());
        return text.Length <= 2000 ? text : text[..2000];
    }

    private async Task ThrowClosedAsync()
    {
        string errors = stderr.IsCompleted ? await stderr : "";
        throw new IOException(name + " закрыл поток ACP до завершения запроса. Автоматического повтора нет." +
            (errors.Length == 0 ? "" : "\n" + Redact(errors)) + (lines.Skipped.Length == 0 ? "" : "\n" + Redact(lines.Skipped)));
    }

    private string Redact(string text) => string.IsNullOrEmpty(secret) ? text : text.Replace(secret, "[redacted]", StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        if (PromptActive && SessionId is not null)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await WriteAsync(new { jsonrpc = "2.0", method = "session/cancel", @params = new { sessionId = SessionId } }, cancel.Token); }
            catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException) { }
        }
        lifetime.Cancel();
        try { process.StandardInput.Close(); } catch (IOException) { }
        // EOF lets the official CLI flush its own session history before the process-tree fallback.
        using (var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            try { await process.WaitForExitAsync(grace.Token); }
            catch (OperationCanceledException) { }
        }
        await SubscriptionProcess.FinishAsync(process, stderr);
        await reading;
        process.Dispose();
        lifetime.Dispose();
        writes.Dispose();
    }
}

internal static class SubscriptionJsonExtensions
{
    internal static int? TryGetInt32Safe(this JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : null;
}

internal static class SubscriptionJson
{
    internal static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString() : null;
    internal static JsonElement Child(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child) ? child : default;
    internal static int? Id(JsonElement value) => Child(value, "id") is { ValueKind: JsonValueKind.Number } id && id.TryGetInt32(out var number) ? number : null;
}
