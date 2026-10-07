using System.Collections.Concurrent;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace ClaudeRevit.Tools;

// What the agent changed, per document, as Revit undo steps: one entry per tool call that left
// changes in the model (every dispatcher-run call is exactly one undo step — one transaction, or
// one assimilated transaction group). Grouped by action: the task (user request / MCP turn) that
// made it. Used by undo_last to roll back a whole action in one call.
internal static class ActionHistory
{
    internal sealed record Entry(string? ActionId, string Tool, DateTime Utc, int Added, int Modified, int Deleted, IReadOnlyList<long> AddedSample);
    private static readonly ConcurrentDictionary<string, List<Entry>> ByDocument = new();
    private const int Limit = 300;

    public static void Record(string documentKey, string? actionId, string tool, ModelChangeCapture changes)
    {
        if (changes.AddedCount + changes.ModifiedCount + changes.DeletedCount == 0) return;
        WritePlans.Bump(documentKey);
        var list = ByDocument.GetOrAdd(documentKey, _ => new());
        lock (list)
        {
            list.Add(new(actionId, tool, DateTime.UtcNow, changes.AddedCount, changes.ModifiedCount, changes.DeletedCount, changes.AddedSample));
            if (list.Count > Limit) list.RemoveRange(0, list.Count - Limit);
        }
    }

    public static List<Entry> Snapshot(string documentKey)
    {
        if (!ByDocument.TryGetValue(documentKey, out var list)) return new();
        lock (list) return list.ToList();
    }

    public static void Pop(string documentKey, int count)
    {
        if (!ByDocument.TryGetValue(documentKey, out var list)) return;
        lock (list) list.RemoveRange(Math.Max(0, list.Count - count), Math.Min(count, list.Count));
    }

    // The trailing entries of the most recent action (or the last n entries).
    public static List<Entry> Tail(string documentKey, int? steps)
    {
        var all = Snapshot(documentKey);
        if (all.Count == 0) return all;
        if (steps is { } n) return all.Skip(Math.Max(0, all.Count - n)).ToList();
        var action = all[^1].ActionId;
        var tail = new List<Entry>();
        for (int i = all.Count - 1; i >= 0 && all[i].ActionId == action; i--) tail.Insert(0, all[i]);
        return tail;
    }
}

public sealed class UndoLast : IRevitTool
{
    public string Name => "undo_last";
    public string Description =>
        "Undo the agent's last action in this document as one step: every model change made while answering the most " +
        "recent request (or the last `steps` tool calls) is rolled back with Revit's own Undo, newest first. Each undone " +
        "step is verified to be the agent's (Claude transaction); if a manual edit by the user is on top of the undo " +
        "stack, it is restored (Redo) and nothing more is undone. list=true shows the recent actions without undoing. " +
        "The document must be the active tab. Revit's Redo brings undone steps back.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["steps"] = NativeToolUtil.Field("integer", "Undo the last N tool calls instead of the whole last action."),
        ["list"] = NativeToolUtil.Field("boolean", "Only list recent actions and their steps.")
    });
    // Runs through UndoSequencer (the dispatcher hands it the job); Execute serves list=true and
    // explains when called outside the dispatcher.
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var key = Services.DocumentSessions.ExistingKey(NativeToolUtil.Doc(app)) ?? Services.DocumentSessions.CurrentDocumentKey;
        return Services.Json.Serialize(new
        {
            actions = ActionHistory.Snapshot(key).AsEnumerable().Reverse().GroupBy(e => e.ActionId).Take(10).Select(g => new
            {
                action = g.Key, steps = g.Count(), tools = g.Select(e => e.Tool), added = g.Sum(e => e.Added), modified = g.Sum(e => e.Modified), deleted = g.Sum(e => e.Deleted),
                last_utc = g.Max(e => e.Utc)
            })
        });
    }
}

// Drives Revit's Undo command step by step: post Undo, wait for the DocumentChanged that reports
// the undone transaction, check it is the agent's, post the next. PostCommand only runs once the
// API call returns, so the sequence advances on Idling; the dispatcher refuses other tool calls
// while it runs so nothing interleaves with the undo stack.
internal sealed class UndoSequencer
{
    private static UndoSequencer? _active;
    public static bool Active => _active != null;

    private readonly UIApplication _app; private readonly Document _doc; private readonly string _key;
    private readonly List<ActionHistory.Entry> _entries; private readonly TaskCompletionSource<string> _tcs;
    private readonly List<string> _undone = new();
    private int _done; private bool _waiting; private string? _foreign; private bool _redoPosted; private DateTime _deadline;

    private UndoSequencer(UIApplication app, Document doc, string key, List<ActionHistory.Entry> entries, TaskCompletionSource<string> tcs)
    { _app = app; _doc = doc; _key = key; _entries = entries; _tcs = tcs; }

    public static void Start(UIApplication app, Document doc, string key, int? steps, TaskCompletionSource<string> tcs)
    {
        if (_active != null) { tcs.TrySetResult(Services.ToolResult.Failure("validation", "An undo is already running.")); return; }
        if (!Services.DocumentSessions.Same(doc, app.ActiveUIDocument?.Document))
        { tcs.TrySetResult(Services.ToolResult.Failure("validation", "Undo works on the active tab: activate this document and retry. Nothing was undone.")); return; }
        var entries = ActionHistory.Tail(key, steps);
        if (entries.Count == 0) { tcs.TrySetResult(Services.ToolResult.Failure("validation", "No agent changes are recorded for this document in this session. Nothing was undone.")); return; }
        var s = new UndoSequencer(app, doc, key, entries, tcs);
        _active = s;
        app.Idling += s.OnIdling;
        s.PostUndo();
    }

    private void PostUndo()
    {
        _waiting = true; _deadline = DateTime.UtcNow.AddSeconds(20);
        _app.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.Undo));
    }

    // Called from the application's DocumentChanged handler.
    public static void Changed(DocumentChangedEventArgs e)
    {
        var s = _active;
        if (s == null || !s._waiting || !Services.DocumentSessions.Same(s._doc, e.GetDocument())) return;
        if (e.Operation != UndoOperation.TransactionUndone) return;
        var names = e.GetTransactionNames();
        var theirs = names.FirstOrDefault(n => !n.StartsWith("Claude", StringComparison.Ordinal));
        if (theirs != null) s._foreign = theirs;
        else { s._done++; s._undone.AddRange(names); }
        s._waiting = false;
    }

    private void OnIdling(object? sender, IdlingEventArgs e)
    {
        try
        {
            if (_waiting)
            {
                if (DateTime.UtcNow > _deadline) Finish("Revit did not perform Undo (a dialog may be open, or the document is not active).");
                return;
            }
            if (_foreign != null)
            {
                if (!_redoPosted) { _redoPosted = true; _app.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.Redo)); return; }
                Finish($"Stopped: the next undo step was not the agent's ('{_foreign}') — it was restored with Redo.");
                return;
            }
            if (_done < _entries.Count) { PostUndo(); return; }
            Finish(null);
        }
        catch (Exception ex) { Finish("Undo failed: " + ex.Message); }
    }

    private void Finish(string? problem)
    {
        _app.Idling -= OnIdling;
        _active = null;
        ActionHistory.Pop(_key, _done);
        var undoneEntries = _entries.Skip(_entries.Count - _done).ToList();
        var leftovers = _doc.IsValidObject ? undoneEntries.SelectMany(x => x.AddedSample).Count(id => _doc.GetElement(new ElementId(id)) != null) : 0;
        var result = Services.Json.Serialize(new
        {
            undone_steps = _done, requested_steps = _entries.Count, tools = undoneEntries.Select(x => x.Tool), transactions = _undone,
            added_elements_still_present = leftovers,
            note = problem ?? (leftovers > 0 ? "Some elements the action created still exist — another step may sit between; call undo_last steps=1 to continue." : "Done. Revit's Redo brings it back."),
        });
        _tcs.TrySetResult(problem != null && _done == 0 ? Services.ToolResult.Failure("undo", problem) : result);
    }
}
