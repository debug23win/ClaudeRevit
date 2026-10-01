using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ClaudeRevit.Services;

// Requests use their own session; Settings acts on an explicitly selected client.
// ExternalEvent jobs explicitly restore this context on Revit's API thread.
public static class McpSession
{
    private static readonly ConcurrentDictionary<string, McpClientState> Clients = new();
    private static readonly AsyncLocal<McpClientState?> Ambient = new();
    private static string? _selected;
    public static McpClientState? Executing => Ambient.Value;
    public static McpClientState? Selected => _selected != null && Clients.TryGetValue(_selected, out var state) ? state : null;
    private static McpClientState? Current => Executing ?? Selected;
    public static string? ClientName => Current?.ClientName;
    public static string? ReportedModel => Current?.ReportedModel;
    public static string? RequestedModel => Selected?.RequestedModel;
    public static IReadOnlyList<McpClientState> All => Clients.Values.OrderBy(s => s.ConnectedUtc).ToList();
    public static event Action? Changed;
    public static McpClientState Create(string? name, string? version, string? channelId)
    {
        foreach (var stale in Clients.Values.Where(s => s.IsIdle && s.LastSeenUtc < DateTime.UtcNow.AddHours(-2))) Remove(stale.Id);
        if (Clients.Count >= 256) throw new InvalidOperationException("Too many MCP sessions; disconnect unused clients.");
        var state = new McpClientState(name, version, channelId); Clients[state.Id] = state;
        Interlocked.CompareExchange(ref _selected, state.Id, null); Raise(); return state;
    }
    public static McpClientState? Find(string id) => Clients.TryGetValue(id, out var state) ? state : null;
    public static void Select(string? id) { _selected = id != null && Clients.ContainsKey(id) ? id : null; Raise(); }
    public static IDisposable Enter(McpClientState? state) { var previous = Ambient.Value; Ambient.Value = state; return new Restore(previous); }
    public static void ReportModel(string? model) { Executing?.ReportModel(model); Raise(); }
    public static void ResetIdentity() { Selected?.ResetIdentity(); Raise(); }
    public static void RequestModel(string? model) { Selected?.RequestModel(model); Raise(); }
    public static string? TakeDirective() => Executing?.TakeDirective();
    public static void Remove(string id)
    {
        if (Clients.TryRemove(id, out var state)) state.Dispose();
        if (_selected == id) _selected = Clients.Keys.FirstOrDefault(); Raise();
    }
    public static void RemoveChannel(string id) { foreach (var state in Clients.Values.Where(s => s.ChannelId == id)) Remove(state.Id); }
    public static void Clear() { foreach (var id in Clients.Keys) Remove(id); }
    public static string Describe(bool russian = false)
    {
        var state = Selected;
        if (state == null) return russian ? "клиент не подключён" : "no client connected";
        var pending = state.RequestedModel is { } model ? (russian ? $" · запрошена модель {model}" : $" · model {model} requested") : "";
        return state.ReportedModel is { } reported
            ? (russian ? $"клиент: {state.DisplayName} · модель: {reported} (со слов модели){pending}" : $"client: {state.DisplayName} · model: {reported} (self-reported){pending}")
            : (russian ? $"клиент: {state.DisplayName} · модель не сообщена{pending}" : $"client: {state.DisplayName} · model not reported{pending}");
    }
    private static void Raise() { try { Changed?.Invoke(); } catch { } }
    private sealed class Restore(McpClientState? previous) : IDisposable { public void Dispose() => Ambient.Value = previous; }
}
