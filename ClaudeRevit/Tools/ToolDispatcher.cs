using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class ToolDispatcher : IExternalEventHandler
{
    private static ToolDispatcher? _instance;

    public static ToolDispatcher Instance =>
        _instance ?? throw new InvalidOperationException("ToolDispatcher.Initialize must be called first.");

    public static void Initialize(ToolRegistry registry)
    {
        if (_instance != null) return;
        _instance = new ToolDispatcher(registry);
        _instance._event = ExternalEvent.Create(_instance);
    }

    private readonly ToolRegistry _registry;
    private ExternalEvent _event = null!;
    private readonly ConcurrentQueue<Job> _queue = new();
    private TransactionGroup? _activeGroup;

    private ToolDispatcher(ToolRegistry registry) => _registry = registry;

    public Task BeginTurnAsync(string label, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        _queue.Enqueue(new BeginTurnJob(label, tcs));
        _event.Raise();
        return tcs.Task;
    }

    public Task EndTurnAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        _queue.Enqueue(new EndTurnJob(tcs));
        _event.Raise();
        return tcs.Task;
    }

    public Task<string> ExecuteAsync(
        string name,
        IReadOnlyDictionary<string, JsonElement> input,
        CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        _queue.Enqueue(new ToolJob(name, input, tcs));
        _event.Raise();
        return tcs.Task;
    }

    public Task<string> GetProjectContextAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        _queue.Enqueue(new GetContextJob(tcs));
        _event.Raise();
        return tcs.Task;
    }

    public Task<bool> FocusElementAsync(long elementId, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        _queue.Enqueue(new FocusElementJob(elementId, tcs));
        _event.Raise();
        return tcs.Task;
    }

    // Snapshot of every non-type element id in the active document. Used by the benchmark to
    // reset the model to a baseline between tasks (delete whatever a task added), so each task
    // starts from the same clean state and can't contaminate the next.
    public Task<List<long>> AllElementIdsAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<List<long>>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        _queue.Enqueue(new AllIdsJob(tcs));
        _event.Raise();
        return tcs.Task;
    }

    public void Execute(UIApplication app)
    {
        while (_queue.TryDequeue(out var job))
        {
            switch (job)
            {
                case BeginTurnJob b: HandleBeginTurn(app, b); break;
                case EndTurnJob e: HandleEndTurn(e); break;
                case ToolJob t: HandleTool(app, t); break;
                case GetContextJob g: HandleGetContext(app, g); break;
                case FocusElementJob f: HandleFocusElement(app, f); break;
                case AllIdsJob a: HandleAllIds(app, a); break;
            }
        }
    }

    private void HandleAllIds(UIApplication app, AllIdsJob job)
    {
        try
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) { job.Tcs.TrySetResult(new List<long>()); return; }
            var ids = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .ToElementIds().Select(id => id.Value).ToList();
            job.Tcs.TrySetResult(ids);
        }
        catch (Exception ex) { job.Tcs.TrySetException(ex); }
    }

    private void HandleFocusElement(UIApplication app, FocusElementJob job)
    {
        try
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) { job.Tcs.TrySetResult(false); return; }
            var id = new ElementId(job.Id);
            var element = uidoc.Document.GetElement(id);
            if (element == null) { job.Tcs.TrySetResult(false); return; }
            uidoc.Selection.SetElementIds(new[] { id });
            uidoc.ShowElements(new[] { id });
            job.Tcs.TrySetResult(true);
        }
        catch (Exception ex) { job.Tcs.TrySetException(ex); }
    }

    private void HandleBeginTurn(UIApplication app, BeginTurnJob job)
    {
        try
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc != null && _activeGroup == null)
            {
                _activeGroup = new TransactionGroup(doc, job.Label);
                _activeGroup.Start();
            }
            job.Tcs.TrySetResult(true);
        }
        catch (Exception ex) { job.Tcs.TrySetException(ex); }
    }

    private void HandleEndTurn(EndTurnJob job)
    {
        try
        {
            if (_activeGroup != null)
            {
                if (_activeGroup.HasStarted() && !_activeGroup.HasEnded())
                    _activeGroup.Assimilate();
                _activeGroup.Dispose();
                _activeGroup = null;
            }
            job.Tcs.TrySetResult(true);
        }
        catch (Exception ex) { job.Tcs.TrySetException(ex); }
    }

    private void HandleTool(UIApplication app, ToolJob job)
    {
        // Log before running so, if a tool corrupts the model and Revit crashes on the
        // next redraw, the log's last line names the culprit tool and its arguments.
        Services.Log.Info($"tool → {job.Name} {SafeArgs(job.Input)}");
        try
        {
            var tool = _registry.Get(job.Name)
                ?? throw new InvalidOperationException($"Unknown tool: {job.Name}");

            // Defence in depth: even if a gated tool is somehow requested while the setting
            // is off, refuse rather than run arbitrary code.
            if (tool.RequiresCodeExecutionOptIn && !Services.SettingsStore.AllowCodeExecution)
                throw new InvalidOperationException(
                    "Code execution is disabled. The user must tick 'Allow Claude to run code' in " +
                    "the settings (gear icon) before this tool can run.");

            // Learning mode: script escape hatches are journaled together with the model
            // delta they produce (via DocumentChanged), so proven snippets can be reused
            // and recurring patterns promoted into dedicated tools.
            if (tool.IsScriptTool)
            {
                Services.ScriptJournal.Begin(
                    job.Name,
                    job.Input.TryGetValue("code", out var codeEl) ? codeEl.GetString() ?? "" : "",
                    job.Input.TryGetValue("engine", out var engEl) && engEl.ValueKind == JsonValueKind.String
                        ? engEl.GetString() : null,
                    app.ActiveUIDocument?.Document?.Title);
            }

            string result;
            if (tool.RequiresTransaction)
            {
                var doc = app.ActiveUIDocument?.Document
                    ?? throw new InvalidOperationException("No active document.");
                using var tx = new Transaction(doc, $"Claude: {tool.Name}");
                tx.Start();

                // Capture Revit's failure messages (the "could not cut instance out of wall"
                // popups) instead of blocking on a modal dialog. Without this the dialog
                // appears, Revit silently rolls the change back, and the model — never told —
                // marches on to the next step thinking it succeeded.
                var failures = new CapturingFailuresPreprocessor();
                var opts = tx.GetFailureHandlingOptions();
                opts.SetFailuresPreprocessor(failures);
                opts.SetForcedModalHandling(false);
                opts.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(opts);

                try
                {
                    result = tool.Execute(job.Input, app);
                    var status = tx.Commit();

                    // An unresolved error rolls the transaction back WITHOUT throwing — surface
                    // it as a tool error so the model knows this step did NOT take effect.
                    if (status != TransactionStatus.Committed)
                        throw new InvalidOperationException(
                            "Revit rolled this operation back — it did NOT take effect" +
                            (failures.Messages.Count > 0
                                ? ": " + string.Join("; ", failures.Messages)
                                : " (a Revit failure could not be resolved). Re-check the model before continuing."));

                    // Committed, but Revit reported warnings — pass them along so the model
                    // can verify the result rather than assume it was clean.
                    if (failures.Messages.Count > 0)
                        result += "\n\n[Revit reported during this operation: "
                                  + string.Join("; ", failures.Messages) + "]";
                }
                catch
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    throw;
                }
            }
            else
            {
                result = tool.Execute(job.Input, app);
            }
            // Script tools report failures as normal {"ok":false,...} results without
            // throwing — read the flag from the result, or the journal would advertise
            // broken snippets as proven.
            if (tool.IsScriptTool)
                Services.ScriptJournal.Complete(ResultLooksOk(result), result);

            // Anything that may have created/renamed types or loaded families makes the
            // cached project catalog stale.
            if (tool.RequiresTransaction || tool.MutatesWithoutTransaction)
                GetProjectCatalog.Invalidate();

            Services.Log.Info($"tool ✓ {job.Name}");
            job.Tcs.TrySetResult(result);
        }
        catch (Exception ex)
        {
            Services.ScriptJournal.Complete(ok: false, ex.Message);
            Services.Log.Error($"tool ✗ {job.Name}", ex);
            job.Tcs.TrySetException(ex);
        }
    }

    private static string SafeArgs(IReadOnlyDictionary<string, JsonElement> input)
    {
        try { return JsonSerializer.Serialize(input); } catch { return "(unprintable)"; }
    }

    // Script tool results are our own JSON with a top-level "ok"; absence means success.
    private static bool ResultLooksOk(string result)
    {
        try
        {
            using var d = JsonDocument.Parse(result);
            return !(d.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.False);
        }
        catch
        {
            return true;
        }
    }

    private void HandleGetContext(UIApplication app, GetContextJob job)
    {
        try
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
            {
                job.Tcs.TrySetResult("(No document is currently open.)");
                return;
            }

            var allLevels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).Select(l => l.Name).ToList();
            // This context trails EVERY request uncached — keep it small. A tower with hundreds of
            // levels would otherwise re-bill the whole list every round; cap it and let the model
            // call get_levels for the full set when it actually needs them.
            var levels = allLevels.Count > 40
                ? allLevels.Take(40).Append($"… +{allLevels.Count - 40} more (call get_levels)").ToList()
                : allLevels;

            string units;
            try
            {
                var fmt = doc.GetUnits().GetFormatOptions(SpecTypeId.Length);
                units = fmt.GetUnitTypeId().TypeId;
            }
            catch { units = "(unknown)"; }

            var projectNotes = Services.MemoryStore.LoadProject(doc.Title, doc.PathName);

            var info = new
            {
                title = doc.Title,
                active_view = doc.ActiveView?.Name,
                length_units = units,
                level_count = allLevels.Count,
                levels,
                project_notes = string.IsNullOrWhiteSpace(projectNotes) ? null : projectNotes
            };
            job.Tcs.TrySetResult(JsonSerializer.Serialize(info));
        }
        catch (Exception ex) { job.Tcs.TrySetException(ex); }
    }

    public string GetName() => "ClaudeRevit.ToolDispatcher";

    private abstract record Job;
    private sealed record BeginTurnJob(string Label, TaskCompletionSource<bool> Tcs) : Job;
    private sealed record EndTurnJob(TaskCompletionSource<bool> Tcs) : Job;
    private sealed record ToolJob(
        string Name,
        IReadOnlyDictionary<string, JsonElement> Input,
        TaskCompletionSource<string> Tcs) : Job;
    private sealed record GetContextJob(TaskCompletionSource<string> Tcs) : Job;
    private sealed record FocusElementJob(long Id, TaskCompletionSource<bool> Tcs) : Job;
    private sealed record AllIdsJob(TaskCompletionSource<List<long>> Tcs) : Job;

    // Collects the text of Revit's failure messages during a transaction commit so they can
    // be reported to the model, and lets Revit resolve them non-interactively (no modal
    // dialog). Returning Continue means an unresolved error still rolls the transaction back —
    // which the caller detects from the commit status.
    private sealed class CapturingFailuresPreprocessor : IFailuresPreprocessor
    {
        public readonly List<string> Messages = new();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            try
            {
                foreach (var f in a.GetFailureMessages())
                {
                    try { Messages.Add($"[{f.GetSeverity()}] {f.GetDescriptionText()}"); }
                    catch { /* skip an unreadable message */ }
                }
            }
            catch { /* never let failure capture itself throw */ }
            return FailureProcessingResult.Continue;
        }
    }
}
