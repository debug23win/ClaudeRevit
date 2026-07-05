using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace ClaudeRevit.Services;

// Learning mode for the code escape hatches: every execute_csharp / run_dynamo_python
// call is journaled together with the MODEL DELTA it produced (added/modified/deleted
// elements, grouped by category, captured from Revit's DocumentChanged event, which
// fires on every transaction commit). Recurring script patterns in this journal are the
// blueprint for the next dedicated tool — the delta records exactly what a scriptless
// implementation must create — and get_script_journal lets the assistant reuse snippets
// that are already proven to work in this environment.
public static class ScriptJournal
{
    private const long MaxFileBytes = 2_000_000; // keep the journal bounded
    private const int MaxKeptEntriesOnTrim = 200;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeRevit", "script_journal.jsonl");

    private static bool _recording;
    private static string? _tool;
    private static string? _code;
    private static string? _engine;
    private static string? _document;
    private static DateTime _startedUtc;
    private static readonly Dictionary<string, int> Added = new();
    private static readonly Dictionary<string, int> Modified = new();
    private static readonly List<long> AddedIds = new();
    private static int _deleted;

    // Wired once at add-in startup: ControlledApplication.DocumentChanged fires after
    // every committed transaction, including the ones a Python script manages itself.
    public static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (!_recording) return;
        try
        {
            var doc = e.GetDocument();
            foreach (var id in e.GetAddedElementIds())
            {
                var cat = doc.GetElement(id)?.Category?.Name ?? "(no category)";
                Added[cat] = Added.GetValueOrDefault(cat) + 1;
                if (AddedIds.Count < 50) AddedIds.Add(id.Value);
            }
            foreach (var id in e.GetModifiedElementIds())
            {
                var cat = doc.GetElement(id)?.Category?.Name ?? "(no category)";
                Modified[cat] = Modified.GetValueOrDefault(cat) + 1;
            }
            _deleted += e.GetDeletedElementIds().Count;
        }
        catch { /* journaling must never break a tool call */ }
    }

    public static void Begin(string tool, string code, string? engine, string? document)
    {
        // Single recording slot: correct because ToolDispatcher's ExternalEvent serializes
        // tool execution — at most one tool runs at a time. If journaling is ever extended
        // to re-entrant callers, this must become a stack.
        _tool = tool;
        _code = code;
        _engine = engine;
        _document = document;
        _startedUtc = DateTime.UtcNow;
        Added.Clear();
        Modified.Clear();
        AddedIds.Clear();
        _deleted = 0;
        _recording = true;
    }

    // Lets the tool report the engine that ACTUALLY ran (auto-detected engines would
    // otherwise journal as null, hiding the one variable that matters for replay).
    public static void SetEngine(string engine)
    {
        if (_recording) _engine = engine;
    }

    public static void Complete(bool ok, string? resultOrError)
    {
        if (!_recording) return;
        _recording = false;
        try
        {
            var entry = JsonSerializer.Serialize(new
            {
                ts = _startedUtc.ToString("o"),
                duration_ms = (int)(DateTime.UtcNow - _startedUtc).TotalMilliseconds,
                tool = _tool,
                engine = _engine,
                document = _document,
                ok,
                result = Truncate(resultOrError, 1000),
                code = Truncate(_code, 4000),
                changes = new
                {
                    added_by_category = Added.Count > 0 ? new Dictionary<string, int>(Added) : null,
                    modified_by_category = Modified.Count > 0 ? new Dictionary<string, int>(Modified) : null,
                    deleted_count = _deleted,
                    added_ids = AddedIds.Count > 0 ? AddedIds.ToList() : null
                }
            });

            // File work (append + possible 2MB trim rewrite) has no Revit dependency —
            // keep it off the UI thread so the tool result isn't held up by bookkeeping.
            // A serialized continuation CHAIN (not bare Task.Run): entries land in
            // completion order, and ReadRecent can await the chain's tail for
            // read-your-writes semantics.
            lock (QueueGate)
            {
                _writeQueue = _writeQueue.ContinueWith(
                    _ => AppendEntry(entry),
                    System.Threading.CancellationToken.None,
                    System.Threading.Tasks.TaskContinuationOptions.None,
                    System.Threading.Tasks.TaskScheduler.Default);
            }
        }
        catch { /* best-effort */ }
    }

    private static void AppendEntry(string entry)
    {
        try
        {
            lock (FileGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, entry + "\n");
                TrimIfOversized();
            }
        }
        catch { /* best-effort */ }
    }

    private static readonly object FileGate = new();
    private static readonly object QueueGate = new();
    private static System.Threading.Tasks.Task _writeQueue = System.Threading.Tasks.Task.CompletedTask;

    public static List<JsonElement> ReadRecent(int limit)
    {
        // Wait for queued writes so a get_script_journal call right after a script run
        // sees that run (appends are milliseconds; the timeout only guards a stuck disk).
        System.Threading.Tasks.Task pending;
        lock (QueueGate) pending = _writeQueue;
        try { pending.Wait(TimeSpan.FromSeconds(3)); } catch { /* read what's there */ }

        try
        {
            // FileGate keeps the read out of the middle of a trim rewrite.
            lock (FileGate)
            {
                if (!File.Exists(FilePath)) return [];
                return ReadEntries(limit);
            }
        }
        catch
        {
            return [];
        }
    }

    private static List<JsonElement> ReadEntries(int limit) =>
        File.ReadAllLines(FilePath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Reverse()
                .Take(limit)
                .Select(l =>
                {
                    try { using var d = JsonDocument.Parse(l); return (JsonElement?)d.RootElement.Clone(); }
                    catch { return null; }
                })
                .Where(e => e != null)
                .Select(e => e!.Value)
                .ToList();

    private static void TrimIfOversized()
    {
        try
        {
            if (new FileInfo(FilePath).Length <= MaxFileBytes) return;
            var lines = File.ReadAllLines(FilePath);
            File.WriteAllLines(FilePath, lines.TakeLast(MaxKeptEntriesOnTrim));
        }
        catch { /* best-effort */ }
    }

    private static string? Truncate(string? s, int max) => TextUtil.TruncateOrNull(s, max);
}
