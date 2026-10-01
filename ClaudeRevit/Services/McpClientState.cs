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
            if (_requestedModel != null && _reportedModel != null && _reportedModel.Equals(_requestedModel, StringComparison.OrdinalIgnoreCase)) _requestedModel = null;
        }
    }
    public void ResetIdentity() { lock (_gate) { _reportedModel = null; ReportedUtc = null; _awaitingReport = true; } }
    public void RequestModel(string? model)
    { lock (_gate) { _requestedModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim(); _awaitingReport = _requestedModel != null; } }
    public string? TakeDirective()
    {
        lock (_gate)
        {
            if (!_awaitingReport && _requestedModel == null) return null;
            _awaitingReport = false;
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
