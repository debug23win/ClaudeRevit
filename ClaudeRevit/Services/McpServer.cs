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

    // The URL and header a user pastes into their Claude Code / Desktop MCP config.
    public static string Url => $"http://127.0.0.1:{SettingsStore.McpPort}/mcp";
    public static string AuthHeader => $"Authorization: Bearer {SettingsStore.McpToken}";

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
                // We don't push server-initiated messages; a request/response POST is all that's used.
                Write(ctx, 405, "{\"error\":\"use POST\"}");
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
                    ["serverInfo"] = new JsonObject { ["name"] = "ClaudeRevit", ["version"] = "1.0" }
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
