using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeRevit.Services;
using ClaudeRevit.Tools;

internal static class Program
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
    private static async Task<int> Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClaudeRevit-McpTests-" + Guid.NewGuid().ToString("N"));
        using var workspace = ConversationWorkspace.Acquire(root, "test");
        DocumentSessions.CurrentWorkspace = workspace;
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start();
        SettingsStore.McpPort = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        try
        {
            McpServer.Start(); Check(McpServer.IsRunning, McpServer.LastError ?? "Server did not start");
            var a = await Initialize("Claude Code");
            var b = await Initialize("Codex");
            Check(a != b, "Clients received the same session id");
            var aSchema = await Rpc(a, 2, "tools/list");
            var bSchema = await Rpc(b, 2, "tools/list");
            Check(aSchema.Contains("\"minimum\":1"), "Claude lost its full schema after Codex connected");
            Check(!bSchema.Contains("\"minimum\":1"), "Codex did not receive portable schemas");
            McpSession.Select(b);
            var aProbe = await Rpc(a, 3, "tools/call", new { name = "probe", arguments = new { model = "claude-a" } });
            var bProbe = await Rpc(b, 3, "tools/call", new { name = "probe", arguments = new { model = "gpt-b" } });
            Check(aProbe.Contains("claude-a") && bProbe.Contains("gpt-b"), "Request identity followed the selected UI client");
            Check(McpSession.Find(a)?.ReportedModel == "claude-a", "Model report went to another session");
            McpSession.Find(a)!.RequestModel("claude-next");
            Check(!(await Rpc(b, 4, "tools/call", new { name = "probe", arguments = new { } })).Contains("claude-next"), "Directive leaked to another client");
            Check((await Rpc(a, 4, "tools/call", new { name = "probe", arguments = new { } })).Contains("claude-next"), "Target client lost its directive");
            using (var invalid = await Send("missing", RpcBody(1, "ping"))) Check(invalid.StatusCode == HttpStatusCode.NotFound, "Unknown session was accepted");
            using (var missing = await Send(null, RpcBody(1, "ping"))) Check(missing.StatusCode == HttpStatusCode.BadRequest, "Missing session was accepted");
            ToolDispatcher.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiting = Send(a, RpcBody(5, "tools/call", new { name = "wait", arguments = new { } }));
            await ToolDispatcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Notify(b, "notifications/cancelled", new { requestId = 5 });
            Check(!waiting.IsCompleted, "Another client's cancellation stopped this request");
            await Notify(a, "notifications/cancelled", new { requestId = 5 });
            using (var stopped = await waiting.WaitAsync(TimeSpan.FromSeconds(5))) Check(stopped.StatusCode == HttpStatusCode.Accepted, "Cancelled call returned a normal result");
            var channel = McpTurnChannel.Open(default, DocumentSessions.CurrentDocumentKey);
            var turnSession = await Initialize("Codex", channel.Id);
            ToolDispatcher.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var turnWaiting = Send(turnSession, RpcBody(8, "tools/call", new { name = "wait", arguments = new { } }), channel.Id);
            await ToolDispatcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await channel.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using (var stopped = await turnWaiting) Check(stopped.StatusCode == HttpStatusCode.Accepted, "Pane turn cancellation was lost");
            using (var late = await Send(turnSession, RpcBody(9, "ping"), channel.Id)) Check(late.StatusCode == HttpStatusCode.NotFound, "Late turn request escaped cancellation");
            Check((await Rpc(b, 6, "ping")).Contains("result"), "External client was cancelled with the pane");
            if (OperatingSystem.IsWindows()) await CheckBridge(root);
            using (var deleted = await Send(a, null, method: HttpMethod.Delete)) Check(deleted.IsSuccessStatusCode, "Session DELETE failed");
            Check(McpSession.Find(a) == null && McpSession.Find(b) != null, "Deleting a session affected another client");
            Console.WriteLine("MCP checks passed: two live HTTP clients, independent schemas/model reports/directives, scoped cancellation, turn draining, late-call rejection, session DELETE, concurrent stdio bridge.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { McpServer.Stop(); Http.Dispose(); workspace.Dispose(); Directory.Delete(root, true); }
    }
    private static async Task CheckBridge(string root)
    {
        var settings = Path.Combine(root, "settings.json");
        File.WriteAllText(settings, System.Text.Json.JsonSerializer.Serialize(new { McpEnabled = true, McpPort = SettingsStore.McpPort, McpToken = SettingsStore.McpToken }));
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.GetFullPath("scripts/revit-mcp-bridge.ps1"), "-SettingsPath", settings }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        process.StandardInput.AutoFlush = true;
        try
        {
            await process.StandardInput.WriteLineAsync(RpcBody(1, "initialize", new { protocolVersion = "2025-06-18", clientInfo = new { name = "bridge-test", version = "1" }, capabilities = new { } }));
            var initialized = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check(initialized?.Contains("serverInfo") == true, "Bridge initialize failed: " + initialized);
            ToolDispatcher.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await process.StandardInput.WriteLineAsync(RpcBody(2, "tools/call", new { name = "wait", arguments = new { } }));
            await ToolDispatcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await process.StandardInput.WriteLineAsync(RpcBody(null, "notifications/cancelled", new { requestId = 2 }));
            await process.StandardInput.WriteLineAsync(RpcBody(3, "ping"));
            var ping = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(JsonNode.Parse(ping!)?["id"]?.GetValue<int>() == 3, "Bridge blocked cancellation behind tools/call");
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(process.ExitCode == 0, "Bridge process failed: " + await errors);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Console.Error.WriteLine(await errors); throw;
        }
    }
    private static async Task<string> Initialize(string name, string? channel = null)
    {
        using var response = await Send(null, RpcBody(1, "initialize", new { protocolVersion = "2025-06-18", clientInfo = new { name, version = "1" }, capabilities = new { } }), channel);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Mcp-Session-Id").Single();
    }
    private static string RpcBody(int? id, string method, object? parameters = null)
    {
        var rpc = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = System.Text.Json.JsonSerializer.SerializeToNode(parameters ?? new { }) };
        if (id.HasValue) rpc["id"] = id.Value; return rpc.ToJsonString();
    }
    private static async Task<string> Rpc(string session, int id, string method, object? parameters = null)
    { using var response = await Send(session, RpcBody(id, method, parameters)); response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync(); }
    private static async Task Notify(string session, string method, object parameters)
    { using var response = await Send(session, RpcBody(null, method, parameters)); Check(response.StatusCode == HttpStatusCode.Accepted, "Notification was rejected"); }
    private static Task<HttpResponseMessage> Send(string? session, string? body, string? channel = null, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Post, McpServer.Url + (channel == null ? "" : "?channel=" + channel));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SettingsStore.McpToken);
        if (session != null) request.Headers.Add("Mcp-Session-Id", session);
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return Http.SendAsync(request);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
