using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ClaudeRevit.Tools;

namespace ClaudeRevit.Services;

// EXPERIMENTAL: exposes ClaudeRevit's Revit tools over a local MCP (Model Context Protocol)
// server, so a user can drive Revit from Claude Code / Claude Desktop — which authenticate with a
// Claude Pro/Max SUBSCRIPTION — instead of paying per-token API for the in-Revit chat pane. Using
// a consumer subscription OAuth token directly in a third-party API call is prohibited by
// Anthropic; routing through the client (Claude Code) over MCP is the sanctioned path, and puts
// the token cost on the subscription.
//
// Transport: minimal Streamable-HTTP MCP over System.Net.HttpListener (no ASP.NET Core dependency,
// so nothing extra is pulled into Revit's runtime). Bound to 127.0.0.1 and gated by a bearer
// token, because the exposed tools include model edits (and, if code execution is enabled,
// arbitrary C#). Tool execution is marshalled to Revit's UI thread by the existing ToolDispatcher.
public static class McpServer
{
    private static HttpListener? _listener;
    private static CancellationTokenSource? _cts;
    private static readonly object Gate = new();

    public static bool IsRunning { get { lock (Gate) return _listener?.IsListening == true; } }
    public static string? LastError { get; private set; }

    // Guidance handed to the driving model (Claude Code) via the MCP handshake — it has no access
    // to the in-Revit chat pane's system prompt, so the key rules for working Revit efficiently and
    // correctly go here. Distilled from real field runs.
    private const string Instructions =
        "You are a senior BIM engineer and Revit-API expert driving a LIVE Autodesk Revit model through " +
        "these tools. Work precisely and safely.\n\n" +
        "WORKFLOW — read before you act. Gather context first: get_project_catalog (levels, family types, " +
        "view templates and the rebar catalogue in one call), get_active_view_info, get_selection, " +
        "query_elements / filter_elements, get_model_statistics. NEVER invent element IDs, family/type " +
        "names, or levels — use only values returned by tools. For a non-trivial task, state a 2–4 step " +
        "plan first, then execute. Work in small steps: prove an operation on ONE element, then scale to " +
        "the floor/building — don't run a large batch before verifying one.\n\n" +
        "UNITS — all spatial inputs are in FEET (Revit's internal unit). Convert metric first: 1 m ≈ " +
        "3.28084 ft, 1 mm ≈ 0.00328084 ft. Always confirm the target level and view; state the conversion " +
        "you used.\n\n" +
        "TOOL CHOICE — prefer a dedicated tool when one exists (the full tool index is included below; " +
        "native tools cover walls, floors, roofs, levels, grids, doors, columns, framing, rebar & " +
        "reinforcement, steel connections, family authoring, views, sheets, schedules, annotation, " +
        "filters and export). Use filter_elements for \"find all X where Y\" (unit-aware predicates + " +
        "count/sum/avg aggregate) instead of scanning. Use run_batch to repeat one tool over many items " +
        "in a single transaction. execute_csharp / run_dynamo_python are the escape hatch for what no tool " +
        "covers — only if code execution is enabled (if they aren't offered, it's off); make code " +
        "idempotent, null-checked, and in one transaction.\n\n" +
        "EFFICIENCY — the MCP round-trip is the main cost, so batch aggressively; for heavy multi-step " +
        "work write ONE execute_csharp instead of many tool calls. Creating elements is cheap (~2000/sec) " +
        "but doc.Regenerate() is SUPER-LINEAR — call it ONCE at the end of a batch, never in a loop.\n\n" +
        "REVIT API — on 2024+ use ElementId.Value (long); IntegerValue was removed. Don't call " +
        "RequestViewChange inside a transaction — use the set_active_view tool.\n\n" +
        "SAFETY & ERRORS — every change is one undo step (Ctrl+Z). Do destructive actions (delete, mass " +
        "edits, arbitrary code) on the smallest possible set, and confirm intent when the request is " +
        "broad. If a request is ambiguous (missing level, type or units), ask ONE clarifying question " +
        "instead of guessing. If a tool errors, report it verbatim, explain the likely cause, and fix the " +
        "input — never blindly repeat the same call.\n\n" +
        "ANSWERS — be concise. After acting, say what changed, which IDs/types were affected, and what to " +
        "check. Take numbers (areas, volumes, counts) from tools — never estimate.";

    // The URL and header a user pastes into their Claude Code / Desktop MCP config.
    public static string Url => $"http://127.0.0.1:{SettingsStore.McpPort}/mcp";
    public static string AuthHeader => $"Authorization: Bearer {SettingsStore.McpToken}";

    private static string AppDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeRevit");

    // Writes an .mcp.json pointing at this server (url + bearer token) and returns its path — for
    // launching `claude --mcp-config <path>` (in-pane mode / the MCP benchmark).
    public static string WriteClientConfig()
    {
        Directory.CreateDirectory(AppDir);
        var path = Path.Combine(AppDir, "mcp-client.json");
        var json = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["clauderevit"] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = Url,
                    ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {SettingsStore.McpToken}" }
                }
            }
        }.ToJsonString();
        File.WriteAllText(path, json);
        return path;
    }

    // A working directory for the `claude` subprocess (no auto-discovery of unrelated project files).
    public static string ClientWorkDir()
    {
        var dir = Path.Combine(AppDir, "ccwork");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Start or stop to match the current settings. Safe to call repeatedly (idempotent).
    public static void ApplyFromSettings()
    {
        try
        {
            if (SettingsStore.McpEnabled) Start();
            else Stop();
        }
        catch (Exception ex) { LastError = ex.Message; Log.Error("MCP applyFromSettings failed", ex); }
    }

    public static void Start()
    {
        lock (Gate)
        {
            if (_listener?.IsListening == true) return;
            Stop_NoLock();
            try
            {
                var listener = new HttpListener();
                // A specific loopback IP (not '+') usually needs no URL ACL for the current user.
                listener.Prefixes.Add($"http://127.0.0.1:{SettingsStore.McpPort}/");
                listener.Start();
                _listener = listener;
                _cts = new CancellationTokenSource();
                LastError = null;
                _ = Task.Run(() => AcceptLoop(listener, _cts.Token));
                Log.Info($"MCP server listening at {Url}");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Error("MCP server failed to start (a URL ACL or firewall prompt may be needed)", ex);
                Stop_NoLock();
            }
        }
    }

    public static void Stop()
    {
        lock (Gate) Stop_NoLock();
    }

    private static void Stop_NoLock()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
        _cts = null;
    }

    private static async Task AcceptLoop(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; } // listener stopped
            _ = Task.Run(() => HandleRequest(ctx, ct));
        }
    }

    private static async Task HandleRequest(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            // Bearer-token auth (skip only if no token is configured).
            var token = SettingsStore.McpToken;
            if (!string.IsNullOrEmpty(token))
            {
                var auth = ctx.Request.Headers["Authorization"] ?? "";
                if (auth != $"Bearer {token}") { Write(ctx, 401, "{\"error\":\"unauthorized\"}"); return; }
            }

            if (ctx.Request.HttpMethod == "GET")
            {
                // Unauthenticated health check — lets the user verify with a browser/curl that the
                // server actually came up (the usual failure is a Windows URL-ACL, silent otherwise).
                // The MCP protocol itself (POST) still requires the bearer token.
                var toolCount = ToolRegistry.Instance.All.Count(t =>
                    !t.RequiresCodeExecutionOptIn || SettingsStore.AllowCodeExecution);
                Write(ctx, 200, new JsonObject
                {
                    ["status"] = "ok",
                    ["server"] = "ClaudeRevit MCP",
                    ["tools"] = toolCount,
                    ["code_execution"] = SettingsStore.AllowCodeExecution
                }.ToJsonString());
                return;
            }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            JsonNode? req;
            try { req = JsonNode.Parse(body); }
            catch { Write(ctx, 400, RpcError(null, -32700, "Parse error")); return; }

            // A JSON-RPC notification (no "id") gets an empty 202 — nothing to return.
            var idNode = req?["id"];
            var method = req?["method"]?.GetValue<string>();
            if (idNode == null)
            {
                ctx.Response.StatusCode = 202;
                ctx.Response.Close();
                return;
            }

            var result = await Dispatch(method, req!["params"], ct);
            var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode.DeepClone() };
            if (result.error != null) response["error"] = result.error;
            else response["result"] = result.value ?? new JsonObject();
            Write(ctx, 200, response.ToJsonString());
        }
        catch (Exception ex)
        {
            Log.Error("MCP request failed", ex);
            try { Write(ctx, 500, "{\"error\":\"internal\"}"); } catch { }
        }
    }

    // The static driving rules PLUS the user's saved memory (project standards) and the proven-script
    // digest — so a subscription/MCP session gets the same accumulated knowledge the API path injects
    // into its system prompt. Instructions are sent once at initialize, so memory saved mid-session
    // appears on the next reconnect.
    private static string BuildInstructions()
    {
        var sb = new StringBuilder(Instructions);
        var memory = MemoryStore.Load();
        if (!string.IsNullOrWhiteSpace(memory))
            sb.Append("\n\nSAVED MEMORY — user preferences and project standards; apply them:\n")
              .Append(memory.Trim());
        var experience = ExperienceStore.Digest();
        if (!string.IsNullOrWhiteSpace(experience))
            sb.Append("\n\n").Append(experience!.Trim());
        // Full tool index in the handshake so the driving model knows every tool up front and can
        // call the right one directly — no discovery round-trips even when the client defers the
        // (180) tool schemas. Generated once, cached, and mirrored to a settings .md for the user.
        sb.Append("\n\n").Append(ToolIndexMarkdown());
        return sb.ToString();
    }

    private static string? _toolIndexCache;
    private static string ToolCatalogPath => Path.Combine(AppDir, "tools-catalog.md");

    private static string ToolIndexMarkdown()
    {
        if (_toolIndexCache != null) return _toolIndexCache;

        var allowCode = SettingsStore.AllowCodeExecution;
        var byCat = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var t in ToolRegistry.Instance.All)
        {
            if (t.RequiresCodeExecutionOptIn && !allowCode) continue;
            var cat = ClaudeRevit.Tools.ToolCatalog.CategoryOf(t);
            if (!byCat.TryGetValue(cat, out var list)) byCat[cat] = list = new List<string>();
            list.Add($"- `{t.Name}` — {FirstSentence(t.Description)}");
        }

        var sb = new StringBuilder();
        sb.Append("AVAILABLE TOOLS — the full set is listed here so you can call the right tool by its " +
                  "exact name without searching first. Schemas load on first use.\n");
        foreach (var kv in byCat)
        {
            sb.Append("\n**").Append(kv.Key).Append("**\n");
            kv.Value.Sort(StringComparer.Ordinal);
            foreach (var line in kv.Value) sb.Append(line).Append('\n');
        }
        _toolIndexCache = sb.ToString();

        try { Directory.CreateDirectory(AppDir); File.WriteAllText(ToolCatalogPath, _toolIndexCache); }
        catch { /* the md mirror is a convenience, not required */ }
        return _toolIndexCache;
    }

    // First sentence of a tool description, trimmed to keep the index compact.
    private static string FirstSentence(string desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return "";
        var s = desc.Replace('\n', ' ').Trim();
        var dot = s.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 0) s = s.Substring(0, dot);
        return s.Length > 140 ? s.Substring(0, 140).TrimEnd() + "…" : s;
    }

    private static async Task<(JsonNode? value, JsonObject? error)> Dispatch(string? method, JsonNode? prms, CancellationToken ct)
    {
        switch (method)
        {
            case "initialize":
                var clientVer = prms?["protocolVersion"]?.GetValue<string>();
                return (new JsonObject
                {
                    ["protocolVersion"] = clientVer ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "ClaudeRevit", ["version"] = "1.0" },
                    // Surfaced to the model by the client — the hard-won rules for driving Revit well,
                    // plus the user's saved standards and proven-script digest (parity with the API path,
                    // whose system prompt carries the same). Built at session start.
                    ["instructions"] = BuildInstructions()
                }, null);

            case "ping":
                return (new JsonObject(), null);

            case "tools/list":
                return (new JsonObject { ["tools"] = BuildToolList() }, null);

            case "tools/call":
                return await CallTool(prms, ct);

            default:
                return (null, ErrObj(-32601, $"Method not found: {method}"));
        }
    }

    private static JsonArray BuildToolList()
    {
        var allowCode = SettingsStore.AllowCodeExecution;
        var arr = new JsonArray();
        foreach (var t in ToolRegistry.Instance.All)
        {
            if (t.RequiresCodeExecutionOptIn && !allowCode) continue; // hidden unless opted in
            var props = new JsonObject();
            foreach (var kv in t.InputSchema.Properties ?? new Dictionary<string, JsonElement>())
                props[kv.Key] = JsonSerializer.SerializeToNode(kv.Value);
            var required = new JsonArray();
            foreach (var r in t.InputSchema.Required ?? Array.Empty<string>())
                required.Add(r);

            arr.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = props,
                    ["required"] = required
                }
            });
        }
        return arr;
    }

    private static async Task<(JsonNode? value, JsonObject? error)> CallTool(JsonNode? prms, CancellationToken ct)
    {
        var name = prms?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name)) return (null, ErrObj(-32602, "Missing tool name"));

        var args = new Dictionary<string, JsonElement>();
        if (prms?["arguments"] is JsonObject argObj)
            foreach (var kv in argObj)
            {
                using var doc = JsonDocument.Parse(kv.Value?.ToJsonString() ?? "null");
                args[kv.Key] = doc.RootElement.Clone();
            }

        // Auto-resolve Revit warning/error dialogs for the span of this call — the MCP client
        // (Claude Code) drives unattended, so a modal would otherwise stall the whole session.
        ToolDispatcher.PushSuppress();
        try
        {
            var text = await ToolDispatcher.Instance.ExecuteAsync(name!, args, ct);
            return (ToolResult(text, false), null);
        }
        catch (Exception ex)
        {
            // MCP convention: tool failures are a normal result with isError=true, not a protocol error.
            return (ToolResult("Error: " + ex.Message, true), null);
        }
        finally { ToolDispatcher.PopSuppress(); }
    }

    private static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
        ["isError"] = isError
    };

    private static JsonObject ErrObj(int code, string message) =>
        new() { ["code"] = code, ["message"] = message };

    private static string RpcError(JsonNode? id, int code, string message) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = ErrObj(code, message)
        }.ToJsonString();

    private static void Write(HttpListenerContext ctx, int status, string json)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }
        catch { /* client hung up */ }
    }
}
