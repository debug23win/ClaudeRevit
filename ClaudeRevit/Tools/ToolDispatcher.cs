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
    private BenchmarkFixture? _benchmarkFixture;
    private sealed record BenchmarkFixture(Document Document, string SeedPath, string Directory);

    // Dialog/failure suppression. Revit shows modal warning dialogs (and task dialogs) on
    // transaction commit — fine when a user is present, but they STALL unattended automation
    // (the benchmark) and interrupt long chat builds. The App-level FailuresProcessing and
    // DialogBoxShowing handlers auto-resolve them, but ONLY while ClaudeRevit is driving — this
    // flag gates that so a user's own manual edits still get their normal warnings.
    //   _suppressTurn — set for the span of a chat/benchmark turn (covers execute_csharp, which
    //                   runs its own transaction with no per-tool failure preprocessor).
    //   ForceSuppress — held true by the benchmark across the whole run (covers the gaps between
    //                   turns, e.g. the reset-between-tasks deletions).
    public static volatile bool ForceSuppress;
    //   _suppressCount — scoped suppression for callers with no turn (the MCP server): Push before
    //                    a tool call, Pop after, so dialogs are auto-resolved for that call too.
    private static int _suppressCount;
    public static void PushSuppress() => System.Threading.Interlocked.Increment(ref _suppressCount);
    public static void PopSuppress() => System.Threading.Interlocked.Decrement(ref _suppressCount);
    public static bool Suppressing => ForceSuppress || _suppressCount > 0;

    private ToolDispatcher(ToolRegistry registry) => _registry = registry;
    public static event Action<string, string>? ProgressChanged;
    private static void ReportProgress(string documentKey, string stage, Services.McpTurnChannel? channel)
    { if (channel?.Progress is { } progress) progress(stage); else ProgressChanged?.Invoke(documentKey, stage); }

    public Task BeginTurnAsync(string label, CancellationToken ct = default, string? documentKey = null)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        _queue.Enqueue(new BeginTurnJob(label, tcs, ct, documentKey ?? Services.DocumentSessions.CurrentDocumentKey));
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
        if (name == "read_attachment" && _registry.Get(name) is ReadAttachment attachmentTool)
        {
            if (Services.SettingsStore.DisabledToolGroups.Contains(ToolCatalog.CategoryOf(attachmentTool), StringComparer.OrdinalIgnoreCase))
                return Task.FromResult(Services.ToolResult.Failure("tool_disabled", "Attachment reading is disabled in Settings."));
            var channel = Services.McpSession.Executing?.ChannelId is { } channelId ? Services.McpTurnChannel.Find(channelId) : null;
            var scope = channel?.AttachmentScope ?? Services.AttachmentStore.CurrentScope;
            var boundKey = documentKey ?? Services.DocumentSessions.CurrentDocumentKey;
            return Task.Run(async () =>
            {
                ct.ThrowIfCancellationRequested();
                var raw = await Services.AttachmentStore.ReadAsync(scope, input, ct);
                var value = System.Text.Json.Nodes.JsonNode.Parse(raw)!.AsObject();
                if (value["kind"]?.GetValue<string>() == "image")
                {
                    var bytes = Services.AttachmentImage.EncodePng(value["local_path"]!.GetValue<string>());
                    value["image_id"] = Services.ViewImageStore.Register(bytes, boundKey, channel?.Id);
                    value["mime_type"] = "image/png";
                }
                return Services.ToolResult.Complete(value.ToJsonString(Services.ToolResult.Options));
            }, ct);
        }
        var boundDocumentKey = documentKey ?? Services.DocumentSessions.CurrentDocumentKey;
        if (name == "validate_csharp" && _registry.Get(name) is ValidateCSharp validator)
        {
            return ValidateScriptAsync(input,ct,boundDocumentKey);
        }
        if (name == "execute_csharp" && _registry.Get(name) is ExecuteCSharp scriptTool)
            return PrepareScriptAsync(scriptTool, input, ct, boundDocumentKey);
        return QueueTool(name, input, ct, boundDocumentKey);
    }
    private async Task<string> ValidateScriptAsync(IReadOnlyDictionary<string,JsonElement> input,CancellationToken ct,string documentKey)
    {
        var channel=Services.McpSession.Executing?.ChannelId is { } cid?Services.McpTurnChannel.Find(cid):null;
        var id=channel?.TaskId??Services.TaskJournal.CurrentId;var watch=System.Diagnostics.Stopwatch.StartNew();string? result=null;Exception? error=null;
        try { result=await Task.Run(()=>ValidateCSharp.Validate(input,ct),ct).ConfigureAwait(false);return result; }
        catch(Exception ex){error=ex;throw;}
        finally
        {
            Services.TaskJournal.RecordWorker(id,watch.Elapsed.TotalSeconds);
            Services.TaskJournal.Append(new {kind="tool",utc=DateTime.UtcNow,task_id=id,document_key=documentKey,tool="validate_csharp",phase="worker",worker_seconds=watch.Elapsed.TotalSeconds,queue_seconds=0,revit_seconds=0,ok=result!=null&&ResultLooksOk(result),cancelled=error is OperationCanceledException,error=error?.Message??Services.ToolResult.ErrorMessage(result)});
        }
    }
    private async Task<string> PrepareScriptAsync(ExecuteCSharp tool, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct, string documentKey)
    {
        if (!tool.RequiresCodeExecutionOptIn && Services.SettingsStore.DisabledToolGroups.Contains(ToolCatalog.CategoryOf(tool), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Code tools are disabled in Settings.");
        var channel = Services.McpSession.Executing?.ChannelId is { } id ? Services.McpTurnChannel.Find(id) : null;
        ReportProgress(documentKey, "compiling C# on a worker…", channel);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        byte[] bytes;Exception? compileError=null;
        try { bytes=await Task.Run(() => ExecuteCSharp.Prepare(input["code"].GetString() ?? "", ct), ct).ConfigureAwait(false); }
        catch(Exception ex){compileError=ex;throw;}
        finally
        {
            var taskId=channel?.TaskId??Services.TaskJournal.CurrentId;
            Services.TaskJournal.RecordWorker(taskId,watch.Elapsed.TotalSeconds);
            Services.TaskJournal.Append(new {kind="script_compiled",utc=DateTime.UtcNow,task_id=taskId,tool=tool.Name,document_key=documentKey,worker_seconds=watch.Elapsed.TotalSeconds,ok=compileError==null,error=compileError?.Message,cancelled=compileError is OperationCanceledException});
        }
        ct.ThrowIfCancellationRequested();
        ReportProgress(documentKey, "running C# in Revit…", channel);
        return await QueueTool(tool.Name, input, ct, documentKey, tool, bytes).ConfigureAwait(false);
    }
    private Task<string> QueueTool(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct, string documentKey, ExecuteCSharp? script = null, byte[]? bytes = null)
    {
        var operation = new Services.QueuedOperation<string>(ct);
        // The token travels WITH the job: cancelling only the TCS would leave the job queued, and it
        // would still run on the next Idling — mutating the model after the user hit Stop.
        _queue.Enqueue(new ToolJob(name, input, operation,
            documentKey, Services.McpSession.Executing, script, bytes));
        _event.Raise();
        return operation.Task;
    }

    public Task<string> GetProjectContextAsync(CancellationToken ct = default, string? documentKey = null)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        _queue.Enqueue(new GetContextJob(tcs, documentKey ?? Services.DocumentSessions.CurrentDocumentKey));
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
    public Task<string> BenchmarkProbeAsync(CancellationToken ct = default, bool eligibilityOnly = false)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        _queue.Enqueue(new ProbeJob(tcs, eligibilityOnly));
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

    private void HandleProbe(UIApplication app, ProbeJob job)
    {
        try
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) { job.Tcs.TrySetResult("{\"no_document\":true}"); return; }

            if (job.EligibilityOnly)
            {
                var nested = doc.IsFamilyDocument ? new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .Where(s => s.Family.IsEditable && !s.Family.IsInPlace).Take(200).Select(s => s.Id.Value).ToArray() : Array.Empty<long>();
                job.Tcs.TrySetResult(Services.Json.Serialize(new { is_family_document = doc.IsFamilyDocument, nested_seed_types = nested, resources=BenchmarkModelProbe.Resources(doc) }));
                return;
            }

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
                if (_benchmarkFixture != null) throw new InvalidOperationException("Another operation is running.");
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
                if (_benchmarkFixture is { } fixture)
                {
                    // A manually selected other document remains active. Otherwise restore the
                    // seed before closing the active temporary document (Revit forbids closing it).
                    if (Services.DocumentSessions.Same(app.ActiveUIDocument?.Document, fixture.Document))
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
            if (Services.DocumentSessions.Find(job.DocumentKey)==null) throw new InvalidOperationException("The bound document was closed.");
            job.Tcs.TrySetResult(true);
        }
        catch (Exception ex) { job.Tcs.TrySetException(ex); }
    }

    private void HandleEndTurn(EndTurnJob job) => job.Tcs.TrySetResult(true);

    private static void DeleteBenchmarkDirectory(string directory)
    {
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClaudeRevit-benchmark"));
        var info = new System.IO.DirectoryInfo(System.IO.Path.GetFullPath(directory));
        if (!string.Equals(info.Parent?.FullName, root, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(info.Name, "N", out _) || info.LinkTarget != null)
            throw new InvalidOperationException("Unexpected benchmark cleanup path.");
        if (info.Exists) info.Delete(true);
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

        var queueTime = System.Diagnostics.Stopwatch.GetElapsedTime(job.QueuedAt);
        var executionWatch = System.Diagnostics.Stopwatch.StartNew();

        // Log before running so, if a tool corrupts the model and Revit crashes on the
        // next redraw, the log's last line names the culprit tool and its arguments.
        Services.Log.Info($"tool → {job.Name} {SafeArgs(job.Input)}");
        using var operation = job.Operation;
        using var sessionContext = Services.McpSession.Enter(job.Session);
        using var attachmentScope = job.AttachmentScope == null ? null : Services.AttachmentStore.EnterScope(job.AttachmentScope);
        var boundDocument = Services.DocumentSessions.Find(job.DocumentKey);
        ToolContext.Set(job.Ct, boundDocument, stage => ReportProgress(job.DocumentKey, stage, job.Channel));
        var warnings = new List<string>();
        ModelChangeCapture? changes = null;
        ToolDispatcher.PushSuppress();
        string? completedResult = null;
        Exception? completedError = null;
        bool cancelled = false;
        try
        {
            if (boundDocument == null) throw new InvalidOperationException("The bound document was closed. No changes were made.");
            var tool = _registry.Get(job.Name) ?? throw new InvalidOperationException($"Unknown tool: {job.Name}");
            var activeOnly = new HashSet<string> { "OpenFamilyEditor","ReloadFamilyIntoDocument","BimStarterPluginTools","ColorElementsByParameter","GetMaterialQuantities","GetSelection","PickPointInView","SelectSimilar","SetActiveView" };
            if ((activeOnly.Contains(tool.GetType().Name) || tool.IsScriptTool && job.Input.TryGetValue("code",out var script) && script.GetString()?.Contains("uiapp.ActiveUIDocument") == true) &&
                !Services.DocumentSessions.Same(boundDocument,app.ActiveUIDocument?.Document))
                throw new InvalidOperationException("This UI operation requires its document tab to be active. Activate the intended tab and retry.");
            if(tool.Name.StartsWith("create_",StringComparison.Ordinal)||tool.Name=="rename_element")
                foreach(var key in new[]{"name","new_name"})if(job.Input.TryGetValue(key,out var value)&&value.ValueKind==JsonValueKind.String)Services.GeometryPreflight.Name(value.GetString()??"");
            if (job.PreparedScript != null && !ReferenceEquals(tool, job.PreparedScript))
                throw new InvalidOperationException("Script tool changed during compilation. Retry the call.");
            if (job.PreparedBytes == null) tool.Preflight(job.Input,app);
            changes = new ModelChangeCapture(boundDocument,job.DocumentKey);
            if (_benchmarkFixture != null && job.Name is "open_family_editor" or "reload_family_into_document")
                throw new InvalidOperationException("Benchmark tasks must keep the scratch copy active; run family tasks from a saved RFA seed.");

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
                    boundDocument?.Title);
            }

            string result;
            if (tool.RequiresTransaction)
            {
                var doc = boundDocument;
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
                    result = job.PreparedScript != null ? job.PreparedScript.ExecutePrepared(job.Input, app, job.PreparedBytes!) : tool.Execute(job.Input, app);
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
                        warnings.AddRange(failures.Messages);
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
                var doc = boundDocument;
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
            completedResult = Services.ToolResult.Complete(result,warnings);
        }
        catch (ToolInputException ex)
        {
            // The model's mistake, not a failure of the tool: report it as a normal result naming
            // the parameter. A stack trace here would only invite a retry of the identical call.
            Services.ScriptJournal.Complete(ok: false, ex.Message);
            Services.Log.Info($"tool ✗ {job.Name} — bad input: {ex.Message}");
            completedResult = Services.ToolResult.Failure("validation",ex.Message);
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
            // controlled transition; ordinary tab switches keep the bound document.
            try
            {
                Services.DocumentSessions.Update(app.ActiveUIDocument?.Document);
                if ((Services.DocumentSessions.Same(boundDocument,app.ActiveUIDocument?.Document) || job.Name is "open_family_editor" or "reload_family_into_document") && job.Session?.ChannelId is { } id && Services.McpTurnChannel.Find(id) is { } channel)
                    channel.DocumentKey = Services.DocumentSessions.CurrentDocumentKey;
            }
            catch (Exception ex) { Services.Log.Error("Document context update failed", ex); }
            finally { ToolContext.Clear(); }
            job.Channel?.RecordExecution(queueTime, executionWatch.Elapsed);
            var rolledBack=cancelled || completedError != null || completedResult == null;
            if (completedResult != null)
            {
                try { using var json=JsonDocument.Parse(completedResult); if(json.RootElement.TryGetProperty("preview",out var preview)&&preview.ValueKind==JsonValueKind.True)rolledBack=true; } catch(JsonException) { }
            }
            try
            {
            Services.TaskJournal.RecordTiming(job.TaskId, queueTime.TotalSeconds, executionWatch.Elapsed.TotalSeconds);
            Services.TaskJournal.Append(new { kind="tool",utc=DateTime.UtcNow,task_id=job.TaskId,channel_id=job.Session?.ChannelId,
                document_key=job.DocumentKey,tool=job.Name,queue_seconds=queueTime.TotalSeconds,revit_seconds=executionWatch.Elapsed.TotalSeconds,
                ok=!cancelled&&completedError==null&&completedResult!=null&&ResultLooksOk(completedResult),cancelled,error=completedError?.Message??Services.ToolResult.ErrorMessage(completedResult),
                changes=changes?.Complete(rolledBack) });
            }
            catch (Exception ex) { Services.Log.Error("Tool diagnostics failed",ex); }
            finally { ToolDispatcher.PopSuppress(); }
            Services.Log.Info($"tool timing {job.Name}: queue={queueTime.TotalSeconds:0.000}s execution={executionWatch.Elapsed.TotalSeconds:0.000}s");
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
            var doc = Services.DocumentSessions.Find(job.DocumentKey);
            if (doc == null)
            {
                job.Tcs.TrySetResult("(No document is currently open.)");
                return;
            }


            var allLevels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).ToList();
            // This context trails EVERY request uncached — keep it small. A tower with hundreds of
            // levels would otherwise re-bill the whole list every round; cap it and let the model
            // call get_levels for the full set when it actually needs them.
            var levels = allLevels.Take(40).Select(l => new { id = l.Id.Value, name = l.Name, elevation_ft = l.Elevation, elevation_mm = l.Elevation * Units.MmPerFoot }).ToArray();

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
                is_family_document = doc.IsFamilyDocument,
                active_view = doc.ActiveView?.Name,
                active_view_id = doc.ActiveView?.Id.Value,
                length_units = units,
                level_count = allLevels.Count,
                levels,
                levels_truncated = allLevels.Count > levels.Length,
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
        Services.QueuedOperation<string> Operation, string DocumentKey, Services.McpClientState? Session,
        ExecuteCSharp? PreparedScript = null, byte[]? PreparedBytes = null) : Job
    {
        public string? TaskId => Channel?.TaskId ?? _taskId;
        private readonly string? _taskId = Services.TaskJournal.CurrentId;
        public long QueuedAt { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
        public Services.McpTurnChannel? Channel { get; } = Session?.ChannelId is { } id ? Services.McpTurnChannel.Find(id) : null;
        public string? AttachmentScope { get; } = (Session?.ChannelId is { } scopeChannel ? Services.McpTurnChannel.Find(scopeChannel)?.AttachmentScope : null) ?? Services.AttachmentStore.CurrentScope;
        public TaskCompletionSource<string> Tcs => Operation.Completion;
        public CancellationToken Ct => Operation.Token;
    }
    private sealed record GetContextJob(TaskCompletionSource<string> Tcs, string DocumentKey) : Job;
    private sealed record FocusElementJob(long Id, TaskCompletionSource<bool> Tcs) : Job;
    private sealed record AllIdsJob(TaskCompletionSource<List<long>> Tcs) : Job;
    private sealed record ProbeJob(TaskCompletionSource<string> Tcs, bool EligibilityOnly) : Job;
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
