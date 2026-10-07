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
    // The client's own initialize request, kept so the bridge can re-establish a session the
    // server has forgotten (idle eviction, Revit restarted) instead of failing every later call
    // until the client itself is restarted.
    private string _initializeLine;
    private readonly HttpClient _client;
    private static JavaScriptSerializer Json()
    {
        // The default 2 MB limit silently dropped larger requests (a long execute_csharp, a big
        // run_batch) and the client then waited forever for an answer that was never coming.
        return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    }
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
            try { rpc = Json().Deserialize<Dictionary<string, object>>(line); }
            catch
            {
                // Answer instead of dropping it: JSON-RPC's parse error, so the client stops waiting.
                Write(Json().Serialize(new { jsonrpc = "2.0", id = (object)null, error = new { code = -32700, message = "Parse error: the bridge could not read this request." } }));
                continue;
            }
            if (rpc == null) continue;
            object method;
            if (rpc.TryGetValue("method", out method) && Convert.ToString(method) == "initialize")
            {
                _initializeLine = line;
                await SendAsync(line, rpc).ConfigureAwait(false);
            }
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
        var settings = Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(_settingsPath));
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
            for (var attempt = 0; ; attempt++)
            using (var request = MakeRequest(HttpMethod.Post, line))
            using (var response = await _client.SendAsync(request).ConfigureAwait(false))
            {
                // 404 = the server no longer knows this session. Re-initialize once with the
                // client's original handshake and retry the call on the new session.
                if ((int)response.StatusCode == 404 && attempt == 0 && _initializeLine != null && !IsInitialize(rpc))
                {
                    lock (_sessionGate) _session = null;
                    await ReinitializeAsync().ConfigureAwait(false);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                IEnumerable<string> sessionHeaders;
                if (response.Headers.TryGetValues("Mcp-Session-Id", out sessionHeaders))
                    foreach (var session in sessionHeaders) { lock (_sessionGate) _session = session; break; }
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (id != null && !String.IsNullOrWhiteSpace(body)) Write(body);
                return;
            }
        }
        catch
        {
            if (id != null) Write(Json().Serialize(new {
                jsonrpc = "2.0", id = id, error = new { code = -32603,
                    message = "Revit MCP unavailable. Start Revit with ClaudeRevit and MCP enabled; initialize this client again." }
            }));
        }
    }
    private static bool IsInitialize(Dictionary<string, object> rpc)
    {
        object method;
        return rpc.TryGetValue("method", out method) && Convert.ToString(method) == "initialize";
    }
    // Replays the handshake silently: its response belongs to the bridge, not the client, which
    // already completed its own initialize long ago.
    private async Task ReinitializeAsync()
    {
        using (var request = MakeRequest(HttpMethod.Post, _initializeLine))
        using (var response = await _client.SendAsync(request).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            IEnumerable<string> sessionHeaders;
            if (response.Headers.TryGetValues("Mcp-Session-Id", out sessionHeaders))
                foreach (var session in sessionHeaders) { lock (_sessionGate) _session = session; break; }
        }
        using (var request = MakeRequest(HttpMethod.Post, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"))
        using (var response = await _client.SendAsync(request).ConfigureAwait(false)) { }
    }
    private void Write(string line) { lock (_outputGate) Console.Out.WriteLine(line); }
}
