using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeRevit.Services;

// Per-turn endpoint binding: late CLI calls cannot outlive Stop or cancel other clients.
public sealed class McpTurnChannel
{
    private static readonly ConcurrentDictionary<string, McpTurnChannel> Channels = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts;
    private TaskCompletionSource _idle = Completed();
    private int _active;
    private bool _closed;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string DocumentKey { get; set; }
    public CancellationToken Token { get; }
    public string Url(string baseUrl) => baseUrl + "?channel=" + Id;
    public static McpTurnChannel Open(CancellationToken ct, string documentKey)
    { var channel = new McpTurnChannel(ct, documentKey); Channels[channel.Id] = channel; return channel; }
    private McpTurnChannel(CancellationToken ct, string key)
    { _cts = CancellationTokenSource.CreateLinkedTokenSource(ct); Token = _cts.Token; DocumentKey = key; }
    public static McpTurnChannel? Find(string id) => Channels.TryGetValue(id, out var channel) ? channel : null;
    private static TaskCompletionSource Completed() { var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); tcs.SetResult(); return tcs; }
    public IDisposable EnterCall()
    {
        lock (_gate)
        {
            if (_closed) throw new OperationCanceledException(Token);
            Token.ThrowIfCancellationRequested();
            if (_active++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new Call(this);
        }
    }
    public async Task CloseAsync()
    {
        Task idle;
        lock (_gate) { _closed = true; Channels.TryRemove(Id, out _); _cts.Cancel(); idle = _idle.Task; }
        await idle.ConfigureAwait(false);
        McpSession.RemoveChannel(Id); _cts.Dispose();
    }
    private sealed class Call(McpTurnChannel owner) : IDisposable
    {
        private McpTurnChannel? _owner = owner;
        public void Dispose() { var current = Interlocked.Exchange(ref _owner, null); if (current != null) lock (current._gate) { if (--current._active == 0) current._idle.TrySetResult(); } }
    }
}
