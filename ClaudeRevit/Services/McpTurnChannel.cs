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
    public string? TaskId { get; } = TaskJournal.CurrentId;
    public string DocumentKey { get; set; }
    public CancellationToken Token { get; }
    public bool CompactTools { get; }
    public Action<string>? Progress { get; set; }
    public string? AttachmentScope { get; set; }
    public Func<string?>? TakeUserUpdate { get; set; }
    private long _toolWaitTicks;
    private long _queueTicks, _executionTicks;
    public double ToolWaitSeconds => TimeSpan.FromTicks(Interlocked.Read(ref _toolWaitTicks)).TotalSeconds;
    public void RecordToolWait(TimeSpan elapsed) => Interlocked.Add(ref _toolWaitTicks, elapsed.Ticks);
    public double QueueSeconds => TimeSpan.FromTicks(Interlocked.Read(ref _queueTicks)).TotalSeconds;
    public double ExecutionSeconds => TimeSpan.FromTicks(Interlocked.Read(ref _executionTicks)).TotalSeconds;
    public void RecordExecution(TimeSpan queue, TimeSpan execution)
    { Interlocked.Add(ref _queueTicks, queue.Ticks); Interlocked.Add(ref _executionTicks, execution.Ticks); }
    public string Url(string baseUrl) => baseUrl + "?channel=" + Id;
    public static McpTurnChannel Open(CancellationToken ct, string documentKey, bool compactTools = false)
    { var channel = new McpTurnChannel(ct, documentKey, compactTools); Channels[channel.Id] = channel; return channel; }
    private McpTurnChannel(CancellationToken ct, string key, bool compactTools)
    { _cts = CancellationTokenSource.CreateLinkedTokenSource(ct); Token = _cts.Token; DocumentKey = key; CompactTools = compactTools; }
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
