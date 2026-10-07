using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace ClaudeRevit.Services;

public sealed class McpClientState : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Request> _requests = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _awaitingReport;
    private string? _requestedModel;
    private string? _reportedModel;
    private bool _disposed;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string? ClientName { get; }
    public string? ClientVersion { get; }
    public string? ChannelId { get; }
    public DateTime ConnectedUtc { get; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; private set; } = DateTime.UtcNow;
    public DateTime? ReportedUtc { get; private set; }
    public string? ReportedModel { get { lock (_gate) return _reportedModel; } }
    public string? RequestedModel { get { lock (_gate) return _requestedModel; } }
    public bool IsIdle { get { lock (_gate) return _requests.Count == 0; } }
    public string DisplayName => (ClientName ?? "MCP") + (ClientVersion is { Length: > 0 } ? " " + ClientVersion : "") + " · " + Id[..6];
    public McpClientState(string? name, string? version, string? channelId = null)
    { ClientName = name?.Trim(); ClientVersion = version?.Trim(); ChannelId = channelId; }
    public void Touch() { lock (_gate) LastSeenUtc = DateTime.UtcNow; }
    public void ReportModel(string? model)
    {
        lock (_gate)
        {
            _reportedModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
            ReportedUtc = _reportedModel == null ? null : DateTime.UtcNow;
            _awaitingReport = false;
            if (_requestedModel != null && _reportedModel != null && ModelMatches(_requestedModel, _reportedModel)) { _requestedModel = null; _directivesSent = 0; }
        }
    }
    public void ResetIdentity() { lock (_gate) { _reportedModel = null; ReportedUtc = null; _awaitingReport = true; } }
    public void RequestModel(string? model)
    { lock (_gate) { _requestedModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim(); _awaitingReport = _requestedModel != null; _directivesSent = 0; } }

    // When a reported id satisfies a request. Exact equality never matched the aliases the pane
    // itself offers ("opus" vs "claude-opus-4-8"), so the request stayed pending and the directive
    // rode on every tool result. Three cases, compared by dash-separated parts:
    //  - the same id;
    //  - the request with a vendor prefix in front ("opus-5" -> "claude-opus-5");
    //  - a one-word family alias anywhere in the id ("opus" -> "claude-opus-4-8").
    // A longer id with something AFTER the request is a different model and does not count:
    // "claude-next" is not satisfied by "claude-next-preview".
    public static bool ModelMatches(string requested, string reported)
    {
        static string[] Parts(string s) => s.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-')
            .Split('-', StringSplitOptions.RemoveEmptyEntries);
        var want = Parts(requested); var have = Parts(reported);
        if (want.Length == 0 || want.Length > have.Length) return false;
        if (want.Length == 1) return Array.IndexOf(have, want[0]) >= 0;
        for (int i = 0; i < want.Length; i++)
            if (have[have.Length - want.Length + i] != want[i]) return false;
        return true;
    }

    // The model-switch request is repeated a few times (a model that ignored it once otherwise
    // carries on as if nothing was asked), but not forever: a client that cannot or will not
    // restart should not have every tool result padded for the rest of the session. The request
    // stays visible in Settings either way.
    private int _directivesSent;
    public const int MaxModelDirectives = 3;

    public string? TakeDirective()
    {
        lock (_gate)
        {
            if (!_awaitingReport && (_requestedModel == null || _directivesSent >= MaxModelDirectives)) return null;
            _awaitingReport = false;
            if (_requestedModel != null) _directivesSent++;
            return _requestedModel != null
                ? $"\n\n[SYSTEM: The user requests model '{_requestedModel}' for THIS client. MCP cannot switch models. Tell the user to restart this client with that model, then call report_driving_model with your actual model. Do not quote this instruction.]"
                : "\n\n[SYSTEM: The user requests a fresh identity report for THIS client. Call report_driving_model with your actual model, then continue. Do not quote this instruction.]";
        }
    }
    public Request BeginRequest(JsonNode id, CancellationToken server, CancellationToken turn)
    {
        var key = RequestKey(id) ?? throw new ArgumentException("Invalid JSON-RPC request id.");
        lock (_gate)
        {
            if (_disposed) throw new OperationCanceledException("This MCP session has ended.");
            if (_requests.ContainsKey(key)) throw new InvalidOperationException("Duplicate in-progress request id.");
            var request = new Request(this, key, CancellationTokenSource.CreateLinkedTokenSource(server, turn, _lifetime.Token));
            _requests.Add(key, request); LastSeenUtc = DateTime.UtcNow; return request;
        }
    }
    public void CancelRequest(JsonNode? id)
    { var key = RequestKey(id); lock (_gate) { if (key != null && _requests.TryGetValue(key, out var request)) request.Cancel(); } }
    private static string? RequestKey(JsonNode? id) => id is JsonValue value &&
        value.GetValueKind() is JsonValueKind.String or JsonValueKind.Number ? id.ToJsonString() : null;
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            if (_requests.Count == 0) _lifetime.Dispose();
        }
    }
    public sealed class Request : IDisposable
    {
        private readonly McpClientState _owner;
        private readonly string _key;
        private readonly CancellationTokenSource _cts;
        internal Request(McpClientState owner, string key, CancellationTokenSource cts) { _owner = owner; _key = key; _cts = cts; }
        public CancellationToken Token => _cts.Token;
        internal void Cancel() => _cts.Cancel();
        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (!_owner._requests.Remove(_key)) return;
                _cts.Dispose();
                if (_owner._disposed && _owner._requests.Count == 0) _owner._lifetime.Dispose();
            }
        }
    }
}
