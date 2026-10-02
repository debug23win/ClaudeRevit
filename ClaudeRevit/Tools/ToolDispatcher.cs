using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
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
    private BenchmarkFixture? _benchmarkFixture;
    private sealed record BenchmarkFixture(Document Document, string SeedPath, string Directory);
    private string _turnLabel = "Claude";

    // Dialog/failure suppression. Revit shows modal warning dialogs (and task dialogs) on
    // transaction commit — fine when a user is present, but they STALL unattended automation
    // (the benchmark) and interrupt long chat builds. The App-level FailuresProcessing and
    // DialogBoxShowing handlers auto-resolve them, but ONLY while ClaudeRevit is driving — this
    // flag gates that so a user's own manual edits still get their normal warnings.
    //   _suppressTurn — set for the span of a chat/benchmark turn (covers execute_csharp, which
    //                   runs its own transaction with no per-tool failure preprocessor).
    //   ForceSuppress — held true by the benchmark across the whole run (covers the gaps between
    //                   turns, e.g. the reset-between-tasks deletions).
    private static volatile bool _suppressTurn;
    public static volatile bool ForceSuppress;
    //   _suppressCount — scoped suppression for callers with no turn (the MCP server): Push before
    //                    a tool call, Pop after, so dialogs are auto-resolved for that call too.
    private static int _suppressCount;
    public static void PushSuppress() => System.Threading.Interlocked.Increment(ref _suppressCount);
    public static void PopSuppress() => System.Threading.Interlocked.Decrement(ref _suppressCount);
    public static bool Suppressing => _suppressTurn || ForceSuppress || _suppressCount > 0;

    private ToolDispatcher(ToolRegistry registry) => _registry = registry;

    public Task BeginTurnAsync(string label, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        _queue.Enqueue(new BeginTurnJob(label, tcs, ct, Services.DocumentSessions.CurrentDocumentKey));
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
        CancellationToken ct = default, string? documentKey = null)
    {
        var operation = new Services.QueuedOperation<string>(ct);
        // The token travels WITH the job: cancelling only the TCS would leave the job queued, and it
        // would still run on the next Idling — mutating the model after the user hit Stop.
        _queue.Enqueue(new ToolJob(name, input, operation,
            documentKey ?? Services.DocumentSessions.CurrentDocumentKey, Services.McpSession.Executing));
        _event.Raise();
        return operation.Task;
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

    // Both instances and types need a native filter before enumeration.
    public Task<List<long>> AllElementIdsAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<List<long>>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        _queue.Enqueue(new AllIdsJob(tcs));
        _event.Raise();
        return tcs.Task;
    }

    // A precise objective snapshot for the benchmark judge — element counts by the categories the
    // tasks actually care about (walls + their lengths, floors + areas, levels + elevations, grids,
    // columns, beams, rebar / area-rebar / path-rebar, doors, DirectShapes + their bounding boxes,
    // structural connections, materials). Far more discriminating than get_model_statistics, which
    // can't see rebar, DirectShapes or connections — the reason those tasks were mis-graded.
    public Task<string> BenchmarkProbeAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        _queue.Enqueue(new ProbeJob(tcs));
        _event.Raise();
        return tcs.Task;
    }

    public Task<string> BenchmarkScopeAsync(bool begin, string documentKey, CancellationToken ct)
    {
        // Copy/open/close are completed on the API thread before returning disposition.
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(new BenchmarkScopeJob(begin, documentKey, ct, tcs));
        _event.Raise();
        return tcs.Task;
    }

    public void Execute(UIApplication app)
    {
        try
        {
        while (_queue.TryDequeue(out var job))
        {
            switch (job)
            {
                case BeginTurnJob b: HandleBeginTurn(app, b); break;
                case EndTurnJob e: HandleEndTurn(e); break;
                case ToolJob t: HandleTool(app, t); break;
                case GetContextJob g when !g.Tcs.Task.IsCompleted: HandleGetContext(app, g); break;
                case FocusElementJob f when !f.Tcs.Task.IsCompleted: HandleFocusElement(app, f); break;
                case AllIdsJob a when !a.Tcs.Task.IsCompleted: HandleAllIds(app, a); break;
                case ProbeJob p when !p.Tcs.Task.IsCompleted: HandleProbe(app, p); break;
                case BenchmarkScopeJob b: HandleBenchmarkScope(app, b); break;
            }
        }
        }
        finally
        {
            // Revit requires every transaction/group to end before an event handler returns.
            // A turn may span multiple callbacks; only jobs drained in THIS callback share undo.
            CloseTurnGroup();
        }
    }

    private void HandleProbe(UIApplication app, ProbeJob job)
    {
        try
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) { job.Tcs.TrySetResult("{\"no_document\":true}"); return; }

            const double ftToM = 0.3048;
            int CountClass(Type t)
            {
                try { return new FilteredElementCollector(doc).OfClass(t).WhereElementIsNotElementType().GetElementCount(); }
                catch { return -1; }
            }
            int CountCat(BuiltInCategory c)
            {
                try { return new FilteredElementCollector(doc).OfCategory(c).WhereElementIsNotElementType().GetElementCount(); }
                catch { return -1; }
            }

            var wallLens = new List<double>();
            try
            {
                foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
                    if (w.Location is LocationCurve lc) wallLens.Add(Math.Round(lc.Curve.Length * ftToM, 2));
            }
            catch { }

            var levelEls = new List<double>();
            try
            {
                foreach (var l in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                    levelEls.Add(Math.Round(l.Elevation * ftToM, 2));
            }
            catch { }

            var floorAreas = new List<double>();
            try
            {
                foreach (var f in new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Element>())
                {
                    var a = f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0;
                    if (a > 0) floorAreas.Add(Math.Round(a * ftToM * ftToM, 1));
                }
            }
            catch { }

            // DirectShapes with bounding-box sizes (m) — lets the judge verify the barrel-vault etc.
            var directShapes = new List<object>();
            try
            {
                foreach (var ds in new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<Element>())
                {
                    var bb = ds.get_BoundingBox(null);
                    if (bb == null) { directShapes.Add(new { size_m = (object?)null }); continue; }
                    directShapes.Add(new
                    {
                        size_m = new[]
                        {
                            Math.Round((bb.Max.X - bb.Min.X) * ftToM, 1),
                            Math.Round((bb.Max.Y - bb.Min.Y) * ftToM, 1),
                            Math.Round((bb.Max.Z - bb.Min.Z) * ftToM, 1)
                        },
                        min_z_m = Math.Round(bb.Min.Z * ftToM, 1)
                    });
                }
            }
            catch { }

            var probe = new
            {
                document_key = Services.DocumentSessions.Key(doc),
                total = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount(),
                walls = wallLens.Count,
                wall_lengths_m = wallLens,
                floors = floorAreas.Count,
                floor_areas_m2 = floorAreas,
                roofs = CountCat(BuiltInCategory.OST_Roofs),
                levels = levelEls.Count,
                level_elevations_m = levelEls,
                grids = CountClass(typeof(Grid)),
                structural_columns = CountCat(BuiltInCategory.OST_StructuralColumns),
                structural_framing = CountCat(BuiltInCategory.OST_StructuralFraming),
                rebar = CountClass(typeof(Rebar)),
                area_reinforcement = CountClass(typeof(AreaReinforcement)),
                path_reinforcement = CountClass(typeof(PathReinforcement)),
                structural_connections = CountClass(typeof(StructuralConnectionHandler)),
                doors = CountCat(BuiltInCategory.OST_Doors),
                windows = CountCat(BuiltInCategory.OST_Windows),
                direct_shape_count = directShapes.Count,
                direct_shapes = directShapes,
                materials = CountClass(typeof(Material)),
                generic_models = CountCat(BuiltInCategory.OST_GenericModel)
            };
            var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(Services.Json.Serialize(probe))!;
            BenchmarkModelProbe.Append(doc, values);
            job.Tcs.TrySetResult(Services.Json.Serialize(values));
        }
        catch (Exception ex) { job.Tcs.TrySetResult(Services.Json.Serialize(new { probe_error = ex.Message })); }
    }

    private void HandleAllIds(UIApplication app, AllIdsJob job)
    {
        try
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) { job.Tcs.TrySetResult(new List<long>()); return; }
            var ids = BenchmarkModelProbe.AllElementIds(doc);
            job.Tcs.TrySetResult(ids);
        }
        catch (Exception ex) { job.Tcs.TrySetException(ex); }
    }

    private void HandleBenchmarkScope(UIApplication app, BenchmarkScopeJob job)
    {
        try
        {
            if (job.Begin)
            {
                job.Ct.ThrowIfCancellationRequested();
                if (_benchmarkFixture != null || _activeGroup != null || _suppressTurn) throw new InvalidOperationException("Another operation is running.");
                var doc = NativeToolUtil.Doc(app);
                if (Services.DocumentSessions.Key(doc) != job.DocumentKey) throw new InvalidOperationException("Benchmark document changed before copying the seed.");
                if (doc.IsModified || string.IsNullOrWhiteSpace(doc.PathName) || !System.IO.File.Exists(doc.PathName) || doc.IsWorkshared || doc.IsModelInCloud)
                    throw new InvalidOperationException("Save a local, non-workshared scratch RVT/RFA first (no unsaved edits). Reset runs each task in a fresh file copy.");
                var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClaudeRevit-benchmark", Guid.NewGuid().ToString("N"));
                if (System.IO.Directory.Exists(directory)) throw new InvalidOperationException("Benchmark scratch directory already exists.");
                System.IO.Directory.CreateDirectory(directory);
                var copy = System.IO.Path.Combine(directory, "Bench-" + Guid.NewGuid().ToString("N") + System.IO.Path.GetExtension(doc.PathName));
                try
                {
                    System.IO.File.Copy(doc.PathName, copy, false);
                    var fixture = app.OpenAndActivateDocument(copy).Document;
                    _benchmarkFixture = new(fixture, doc.PathName, directory);
                }
                catch { try { DeleteBenchmarkDirectory(directory); } catch { } throw; }
            }
            else
            {
                CloseTurnGroup();
                if (_benchmarkFixture is { } fixture)
                {
                    // A manually selected other document remains active. Otherwise restore the
                    // seed before closing the active temporary document (Revit forbids closing it).
                    if (ReferenceEquals(app.ActiveUIDocument?.Document, fixture.Document))
                        app.OpenAndActivateDocument(fixture.SeedPath);
                    if (fixture.Document.IsValidObject && !fixture.Document.Close(false))
                        throw new InvalidOperationException("Cannot close the benchmark scratch copy: " + fixture.Directory);
                    _benchmarkFixture = null;
                    try { DeleteBenchmarkDirectory(fixture.Directory); }
                    catch (Exception ex) { Services.Log.Error("Benchmark scratch file cleanup failed", ex); }
                }
                GetProjectCatalog.Invalidate();
            }
            Services.DocumentSessions.Update(app.ActiveUIDocument?.Document);
            job.Tcs.TrySetResult(Services.DocumentSessions.CurrentDocumentKey);
        }
        catch (Exception ex)
        {
            job.Tcs.TrySetException(ex);
        }
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
        if (job.Ct.IsCancellationRequested || job.Tcs.Task.IsCompleted) return;
        try
        {
            if (Services.DocumentSessions.Key(app.ActiveUIDocument?.Document) != job.DocumentKey)
                throw new InvalidOperationException("The active document changed before the turn started. Send the request again in the intended document.");
            _suppressTurn = true;
            _turnLabel = job.Label;
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
            CloseTurnGroup();
            job.Tcs.TrySetResult(true);
        }
        catch (Exception ex) { job.Tcs.TrySetException(ex); }
        finally
        {
            // Must run even if Assimilate threw. Otherwise the group leaks AND _suppressTurn stays
            // true for the rest of the session, silently swallowing Revit's own warnings to the
            // user — a failure they would have no way to notice.
            _suppressTurn = false;
        }
    }

    private static void DeleteBenchmarkDirectory(string directory)
    {
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClaudeRevit-benchmark"));
        var info = new System.IO.DirectoryInfo(System.IO.Path.GetFullPath(directory));
        if (!string.Equals(info.Parent?.FullName, root, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(info.Name, "N", out _) || info.LinkTarget != null)
            throw new InvalidOperationException("Unexpected benchmark cleanup path.");
        if (info.Exists) info.Delete(true);
    }

    private void CloseTurnGroup()
    {
        var group = _activeGroup; _activeGroup = null;
        if (group == null) return;
        try { if (group.GetStatus() == TransactionStatus.Started) group.Assimilate(); }
        finally { group.Dispose(); }
    }

    private void HandleTool(UIApplication app, ToolJob job)
    {
        // Cancelled while queued (user pressed Stop): drop it instead of editing the model for a
        // turn nobody is waiting for any more.
        if (!job.Operation.TryStart())
        {
            job.Operation.Dispose();
            Services.Log.Info($"tool ✗ {job.Name} — skipped, cancelled before it ran");
            return;
        }

        // Log before running so, if a tool corrupts the model and Revit crashes on the
        // next redraw, the log's last line names the culprit tool and its arguments.
        Services.Log.Info($"tool → {job.Name} {SafeArgs(job.Input)}");
        using var operation = job.Operation;
        using var sessionContext = Services.McpSession.Enter(job.Session);
        ToolContext.Set(job.Ct);
        string? completedResult = null;
        Exception? completedError = null;
        bool cancelled = false;
        try
        {
            if (Services.DocumentSessions.Key(app.ActiveUIDocument?.Document) != job.DocumentKey)
                throw new InvalidOperationException("The active document changed while this operation was queued. No changes were made; retry in the intended document.");
            if (_suppressTurn && job.Session != null)
                throw new InvalidOperationException("The chat pane is running a grouped API turn. Retry this MCP operation after it finishes.");
            var tool = _registry.Get(job.Name)
                ?? throw new InvalidOperationException($"Unknown tool: {job.Name}");
            if (_benchmarkFixture != null && job.Name is "open_family_editor" or "reload_family_into_document")
                throw new InvalidOperationException("Benchmark tasks must keep the scratch copy active; run family tasks from a saved RFA seed.");

            if (tool.RequiresNoTurnGroup && _activeGroup != null)
            {
                try { _activeGroup.Assimilate(); }
                finally { _activeGroup.Dispose(); _activeGroup = null; }
            }
            else if (!tool.RequiresNoTurnGroup && _suppressTurn && _activeGroup == null &&
                     (tool.RequiresTransaction || tool.IsScriptTool) && app.ActiveUIDocument?.Document is { } turnDoc)
            {
                _activeGroup = new TransactionGroup(turnDoc, _turnLabel);
                _activeGroup.Start();
            }

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
                    job.Ct.ThrowIfCancellationRequested();
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
                // Script tools manage their own transactions. An outer group can roll those
                // back when cancellation arrives while synchronous code is executing.
                var doc = app.ActiveUIDocument?.Document;
                using var group = tool.IsScriptTool && doc != null ? new TransactionGroup(doc, "Claude script") : null;
                group?.Start();
                try
                {
                    result = tool.Execute(job.Input, app);
                    job.Ct.ThrowIfCancellationRequested();
                    group?.Assimilate();
                }
                catch { if (group?.GetStatus() == TransactionStatus.Started) group.RollBack(); throw; }
            }
            // Script tools report failures as normal {"ok":false,...} results without
            // throwing — read the flag from the result, or the journal would advertise
            // broken snippets as proven.
            if (tool.IsScriptTool)
                Services.ScriptJournal.Complete(ResultLooksOk(result), result);

            // Only tools that can actually change the catalog, not every transaction: rebuilding
            // it is a dozen collector passes over the document, and moving a wall cannot add a
            // type.
            if (tool.InvalidatesCatalog)
                GetProjectCatalog.Invalidate();

            Services.Log.Info($"tool ✓ {job.Name}");
            completedResult = result;
        }
        catch (ToolInputException ex)
        {
            // The model's mistake, not a failure of the tool: report it as a normal result naming
            // the parameter. A stack trace here would only invite a retry of the identical call.
            Services.ScriptJournal.Complete(ok: false, ex.Message);
            Services.Log.Info($"tool ✗ {job.Name} — bad input: {ex.Message}");
            completedResult = Services.Json.Serialize(new { ok = false, error = ex.Message });
        }
        catch (System.OperationCanceledException)
        {
            Services.ScriptJournal.Complete(ok: false, "Cancelled");
            Services.Log.Info($"tool ✗ {job.Name} — cancelled mid-run");
            cancelled = true;
        }
        catch (Exception ex)
        {
            Services.ScriptJournal.Complete(ok: false, ex.Message);
            Services.Log.Error($"tool ✗ {job.Name}", ex);
            completedError = ex;
        }
        finally
        {
            // Tools may deliberately activate a family or another document. Follow that
            // controlled transition; a manual switch cancels the pane's turn instead.
            try
            {
                Services.DocumentSessions.Update(app.ActiveUIDocument?.Document);
                if (job.Session?.ChannelId is { } id && Services.McpTurnChannel.Find(id) is { } channel)
                    channel.DocumentKey = Services.DocumentSessions.CurrentDocumentKey;
            }
            catch (Exception ex) { Services.Log.Error("Document context update failed", ex); }
            finally { ToolContext.Clear(); }
        }
        if (cancelled) job.Tcs.TrySetCanceled(job.Ct);
        else if (completedError != null) job.Tcs.TrySetException(completedError);
        else job.Tcs.TrySetResult(completedResult ?? "");
    }

    private static string SafeArgs(IReadOnlyDictionary<string, JsonElement> input)
    {
        try { return Services.Json.Serialize(input); } catch { return "(unprintable)"; }
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
                standards = GetProjectStandards.ContextSummary(doc),
                project_notes = string.IsNullOrWhiteSpace(projectNotes) ? null : projectNotes
            };
            job.Tcs.TrySetResult(Services.Json.Serialize(info));
        }
        catch (Exception ex) { job.Tcs.TrySetException(ex); }
    }

    public string GetName() => "ClaudeRevit.ToolDispatcher";

    private abstract record Job;
    private sealed record BeginTurnJob(string Label, TaskCompletionSource<bool> Tcs, CancellationToken Ct, string DocumentKey) : Job;
    private sealed record EndTurnJob(TaskCompletionSource<bool> Tcs) : Job;
    private sealed record ToolJob(
        string Name,
        IReadOnlyDictionary<string, JsonElement> Input,
        Services.QueuedOperation<string> Operation, string DocumentKey, Services.McpClientState? Session) : Job
    {
        public TaskCompletionSource<string> Tcs => Operation.Completion;
        public CancellationToken Ct => Operation.Token;
    }
    private sealed record GetContextJob(TaskCompletionSource<string> Tcs) : Job;
    private sealed record FocusElementJob(long Id, TaskCompletionSource<bool> Tcs) : Job;
    private sealed record AllIdsJob(TaskCompletionSource<List<long>> Tcs) : Job;
    private sealed record ProbeJob(TaskCompletionSource<string> Tcs) : Job;
    private sealed record BenchmarkScopeJob(bool Begin, string DocumentKey, CancellationToken Ct, TaskCompletionSource<string> Tcs) : Job;

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
