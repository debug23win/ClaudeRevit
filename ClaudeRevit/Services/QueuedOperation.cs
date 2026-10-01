using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeRevit.Services;

// Cancellation can complete a queued job immediately. Once it starts, its task must wait
// for the Revit thread to settle the transaction; otherwise Stop falsely reports completion.
internal sealed class QueuedOperation<T> : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenRegistration _registration;
    private bool _started;
    public CancellationToken Token { get; }
    public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<T> Task => Completion.Task;

    public QueuedOperation(CancellationToken token)
    {
        Token = token;
        _registration = token.Register(() => { lock (_gate) { if (!_started) Completion.TrySetCanceled(token); } });
    }

    public bool TryStart()
    {
        lock (_gate)
        {
            if (Token.IsCancellationRequested || Completion.Task.IsCompleted)
            { Completion.TrySetCanceled(Token); return false; }
            _started = true;
            return true;
        }
    }
    public void Dispose() => _registration.Dispose();
}
