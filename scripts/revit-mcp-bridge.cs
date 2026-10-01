// Windows PowerShell 5.1 helper. Concurrent HTTP requests let cancellation notifications
// pass a pending tools/call. No credentials are printed or passed on the command line.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

public sealed class ClaudeRevitMcpBridge
{
    private readonly string _settingsPath;
    private readonly object _outputGate = new object();
    private readonly object _sessionGate = new object();
    private string _session;
    private readonly HttpClient _client;
    private ClaudeRevitMcpBridge(string settingsPath)
    {
        _settingsPath = settingsPath;
        _client = new HttpClient(new HttpClientHandler { UseProxy = false });
        _client.Timeout = TimeSpan.FromMinutes(11);
    }
    public static void Run(string settingsPath)
    {
        var bridge = new ClaudeRevitMcpBridge(settingsPath);
        try { bridge.RunAsync().GetAwaiter().GetResult(); }
        finally { bridge._client.Dispose(); }
    }
    private async Task RunAsync()
    {
        var pending = new List<Task>();
        string line;
        while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            if (String.IsNullOrWhiteSpace(line)) continue;
            Dictionary<string, object> rpc;
            try { rpc = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(line); }
            catch { continue; }
            if (rpc == null) continue;
            object method;
            if (rpc.TryGetValue("method", out method) && Convert.ToString(method) == "initialize")
                await SendAsync(line, rpc).ConfigureAwait(false);
            else pending.Add(SendAsync(line, rpc));
            pending.RemoveAll(t => t.IsCompleted);
        }
        // EOF terminates this client's server session, cancelling its queued/running calls.
        try
        {
            using (var request = MakeRequest(HttpMethod.Delete, null))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using (var response = await _client.SendAsync(request, timeout.Token).ConfigureAwait(false)) { }
        }
        catch { }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }
    private HttpRequestMessage MakeRequest(HttpMethod method, string line)
    {
        var settings = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(_settingsPath));
        var port = Convert.ToInt32(settings["McpPort"]);
        var token = Convert.ToString(settings["McpToken"]);
        if (!Convert.ToBoolean(settings["McpEnabled"]) || String.IsNullOrEmpty(token) || port < 1 || port > 65535)
            throw new InvalidOperationException("MCP disabled or invalid local configuration.");
        var request = new HttpRequestMessage(method, "http://127.0.0.1:" + port + "/mcp");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        lock (_sessionGate) if (_session != null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");
        if (line != null) request.Content = new StringContent(line, Encoding.UTF8, "application/json");
        return request;
    }
    private async Task SendAsync(string line, Dictionary<string, object> rpc)
    {
        object id;
        rpc.TryGetValue("id", out id);
        try
        {
            using (var request = MakeRequest(HttpMethod.Post, line))
            using (var response = await _client.SendAsync(request).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                IEnumerable<string> sessionHeaders;
                if (response.Headers.TryGetValues("Mcp-Session-Id", out sessionHeaders))
                    foreach (var session in sessionHeaders) { lock (_sessionGate) _session = session; break; }
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (id != null && !String.IsNullOrWhiteSpace(body)) Write(body);
            }
        }
        catch
        {
            if (id != null) Write(new JavaScriptSerializer().Serialize(new {
                jsonrpc = "2.0", id = id, error = new { code = -32603,
                    message = "Revit MCP unavailable. Start Revit with ClaudeRevit and MCP enabled; initialize this client again." }
            }));
        }
    }
    private void Write(string line) { lock (_outputGate) Console.Out.WriteLine(line); }
}
