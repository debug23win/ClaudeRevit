namespace ClaudeRevit.Services;

public sealed record ChatRequest(string Text, IReadOnlyList<ChatAttachment> Attachments)
{
    public string Prompt => Text + AttachmentStore.Manifest(Attachments);
}

// One service/document owns an inbox. Reading an update is an atomic hand-off to the
// model; updates that arrive after its last tool are executed as the next CLI/API turn.
public sealed class ChatRequestInbox
{
    private readonly object _gate = new();
    private readonly Queue<ChatRequest> _pending = new();
    private bool _active;
    public void Begin() { lock (_gate) { if (_active) throw new InvalidOperationException("A chat turn is already running."); _active = true; } }
    public bool Add(ChatRequest request) { lock (_gate) { if (!_active) return false; _pending.Enqueue(request); return true; } }
    public ChatRequest? Take()
    {
        lock (_gate)
        {
            if (_pending.Count == 0) return null;
            var items = _pending.ToArray(); _pending.Clear();
            return new(string.Join("\n\n", items.Select(i => i.Text)), items.SelectMany(i => i.Attachments).ToArray());
        }
    }
    public bool FinishIfEmpty() { lock (_gate) { if (_pending.Count > 0) return false; _active = false; return true; } }
    // Stop does not silently erase updates that the model has not seen. They are
    // included with the next explicit user request, never run automatically after Stop.
    public void Stop() { lock (_gate) _active = false; }
    public void Clear() { lock (_gate) { if (_active) throw new InvalidOperationException("Stop the chat first."); _pending.Clear(); } }
}
