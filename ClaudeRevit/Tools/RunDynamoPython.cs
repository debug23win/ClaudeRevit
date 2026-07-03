using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// Preferred full-API escape hatch: runs a Python snippet through Dynamo for Revit
// instead of raw C#. Dynamo hosts the code in its own engine and manages its own
// transactions, so — unlike execute_csharp — it does not block Revit's API thread
// with an open transaction (the pattern that repeatedly crashed Revit).
//
// Everything is late-bound via reflection so the build has no Dynamo dependency, and
// EVERY failure path returns an error string — this tool must never crash Revit. If
// Dynamo is not installed it says so and suggests enabling execute_csharp instead.
//
// Execution flow: one journal call boots a headless (dynShowUI=False — no WebView2
// splash) Dynamo model bound to the current document, which also loads Dynamo's Python
// engine extensions. The snippet is then evaluated DIRECTLY through the engine object in
// Dynamo.PythonServices.PythonEngineManager — a synchronous call on this thread, immune
// to graph-schema, run-mode and model-lifecycle quirks. Only when that manager doesn't
// exist (pre-PythonServices Dynamo) does the tool fall back to a journal-driven graph run
// (open a generated .dyn; in automation mode opening runs it synchronously).
//
// Execution feedback: the harness returns a run report (OUT, traceback, document
// identity) — directly as the evaluation result on the primary path, via a temp file on
// the graph fallback. A missing report is surfaced as an honest error instead of a fake
// success — and the tool never re-runs the snippet by itself, because a missing report
// cannot prove the code did not execute (partial commits may have happened).
public class RunDynamoPython : IRevitTool
{
    public string Name => "run_dynamo_python";

    public string Description =>
        "Runs a Python snippet through Dynamo for Revit's Python engine. The snippet runs " +
        "with the standard Dynamo-Revit Python environment: 'clr' is imported and you have " +
        "access to RevitServices (DocumentManager.Instance.CurrentDBDocument for the document, " +
        "TransactionManager.Instance to manage transactions) and the Revit API " +
        "(Autodesk.Revit.DB). Wrap model changes in an EXPLICIT Autodesk.Revit.DB.Transaction " +
        "(t = Transaction(doc, 'name'); t.Start(); …; t.Commit()) — that is the reliable " +
        "pattern here. TransactionManager.Instance.EnsureInTransaction also works (the tool " +
        "force-commits it after the script), but explicit transactions give you control over " +
        "what commits when. Put the result into the Dynamo output " +
        "variable 'OUT': it is serialized and returned in the tool result ('output'), and if " +
        "the script raises, the full traceback is returned. LEAVE 'engine' UNSET — it is " +
        "auto-detected from the running Dynamo (Dynamo 4.x / Revit 2027 ships ONLY PythonNet3; " +
        "CPython3/IronPython2 exist only in older versions or as separately installed packages). " +
        "Revit 2027 API note: use ElementId.Value (long) — ElementId.IntegerValue was removed. " +
        "Requires Dynamo for Revit to be installed. The user must approve each run.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["code"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Python code for a Dynamo Python Script node. Assign the result to OUT."
            }),
            ["engine"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                @enum = new[] { "PythonNet3", "CPython3", "IronPython2" },
                description = "Python engine. Default: auto-detected from the running Dynamo " +
                              "(Dynamo 4.x ships PythonNet3 and no longer supports CPython3; older " +
                              "versions ship CPython3). Only set this explicitly if auto-detection " +
                              "picked wrong. The tool never retries by itself."
            })
        },
        Required = ["code"]
    };

    // Dynamo runs its own transactions — we must NOT wrap it in one of ours.
    public bool RequiresTransaction => false;
    public bool RequiresConfirmation => true;
    public bool RequiresCodeExecutionOptIn => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var code = input["code"].GetString();
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("code is empty.");

        try
        {
            var dynamoRevit = FindDynamoRevitType();
            if (dynamoRevit == null)
                return Fail("Dynamo for Revit was not found in this session. Install Dynamo for " +
                            "Revit, or ask the user to enable execute_csharp in settings as a fallback.");

            // null = auto-detect after the Dynamo model has booted (its Python extension
            // assemblies are loaded by then, so we can see which engine actually exists).
            string? engine = null;
            if (input.TryGetValue("engine", out var e) && e.ValueKind == JsonValueKind.String)
            {
                var wanted = e.GetString();
                engine = KnownEngines.FirstOrDefault(k => string.Equals(k, wanted, StringComparison.OrdinalIgnoreCase));
                if (engine == null)
                    return Fail($"Unknown engine '{wanted}'. Valid values: {string.Join(", ", KnownEngines)}.");
            }

            // The headless journal path only executes when Dynamo is NOT showing its UI —
            // with the Dynamo window open, ExecuteCommand routes the graph through the UI
            // view-model and the node never executes headlessly. Fail fast with a clear
            // action instead of a silent no-op.
            var modelState = GetDynamoState(dynamoRevit);
            if (modelState is "StartedUI" or "StartedUIThread")
                return Fail("The Dynamo window is currently open in Revit, which blocks headless " +
                            "script execution. Ask the user to close the Dynamo window, then retry.");

            // Boot (or reset) the headless Dynamo model bound to the CURRENT document.
            // This also loads Dynamo's Python engine extensions into the process.
            BootDynamo(dynamoRevit, app);

            // Primary path: call the Python engine DIRECTLY via PythonEngineManager —
            // synchronous, on this thread, no .dyn file, no graph open/run semantics, no
            // race with Dynamo's model lifecycle. Returns null only when the manager or
            // engines are unavailable (older Dynamo) — then the journal graph path runs.
            var direct = TryDirectEvaluate(app, code!, engine);
            if (direct != null)
                return direct;

            var (report, parseError, keptDynPath, usedEngine) = RunGraph(dynamoRevit, app, code!, engine);
            engine = usedEngine;

            if (parseError != null)
                return Fail("The Python node ran but its run report could not be parsed (" + parseError +
                            "). The MODEL STATE IS UNKNOWN — the script may have committed changes. " +
                            "Verify with query tools (query_elements / get_element_parameters) before " +
                            "re-running anything.");

            if (report == null)
            {
                // Self-diagnose instead of guessing: Dynamo writes its startup/run problems
                // (node deserialization errors, missing Python engine, graph-open failures)
                // to its own session log — return the tail so the cause is visible directly.
                var dynamoLog = ReadDynamoLogTail();
                return Fail(
                    $"The Python node wrote no run report on engine {engine}, so it most likely never " +
                    "executed — but this cannot be fully guaranteed, so VERIFY with query tools before " +
                    "re-running a model-mutating script. If the log below shows an engine problem, an " +
                    $"explicit 'engine' value may help (valid: {string.Join(", ", KnownEngines)}). " +
                    $"Dynamo state after the run: '{GetDynamoState(dynamoRevit) ?? "(unknown)"}'. " +
                    $"The generated graph was kept at {keptDynPath} — the user can open it in Dynamo " +
                    "manually to see the failure. " +
                    (dynamoLog != null
                        ? "Tail of Dynamo's own session log:\n" + dynamoLog
                        : "Dynamo's session log was not found under %AppData%\\Dynamo\\Dynamo Revit\\<version>\\Logs."));
            }

            return BuildResponse(report.Value, engine, app);
        }
        catch (Exception ex)
        {
            // Never rethrow — a crash here is exactly what we're avoiding.
            Log.Error("run_dynamo_python failed", ex);
            return Fail("Dynamo execution failed: " + (ex.InnerException?.Message ?? ex.Message) +
                        ". If the script may have started, verify the model state with query tools " +
                        "before re-running it.");
        }
    }

    // Engine names in preference order; used both for input validation and detection.
    private static readonly string[] KnownEngines = ["PythonNet3", "CPython3", "IronPython2"];

    // Reset+boot: if a UIless model is alive (possibly bound to a previously active
    // document), dynModelShutDown makes ExecuteCommand shut it down and boot a fresh one
    // bound to the CURRENT document. This call never opens a graph.
    private static void BootDynamo(System.Type dynamoRevit, UIApplication app)
    {
        InvokeExecuteCommand(dynamoRevit, app, new Dictionary<string, string>
        {
            ["dynShowUI"] = "False",
            ["dynAutomation"] = "True",
            ["dynModelShutDown"] = "True"
        });
    }

    // Primary execution path: evaluate the snippet through the Python engine object that
    // the booted Dynamo registered in Dynamo.PythonServices.PythonEngineManager — a plain
    // synchronous method call on this thread. Returns the complete tool response, or null
    // when no engine manager / engines exist (pre-PythonServices Dynamo) so the caller can
    // fall back to the journal-driven graph run.
    private static string? TryDirectEvaluate(UIApplication app, string code, string? requestedEngine)
    {
        object? manager;
        try
        {
            var managerType = FindType("Dynamo.PythonServices.PythonEngineManager");
            manager = managerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
        }
        catch
        {
            return null;
        }
        if (manager == null) return null;

        // AvailableEngines is a public field on current Dynamo; tolerate a property too.
        var enginesMember =
            (object?)manager.GetType().GetField("AvailableEngines", BindingFlags.Public | BindingFlags.Instance)?.GetValue(manager)
            ?? manager.GetType().GetProperty("AvailableEngines", BindingFlags.Public | BindingFlags.Instance)?.GetValue(manager);
        if (enginesMember is not System.Collections.IEnumerable engines) return null;

        var byName = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var engineObj in engines)
        {
            if (engineObj == null) continue;
            var name = engineObj.GetType().GetProperty("Name")?.GetValue(engineObj)?.ToString();
            if (!string.IsNullOrEmpty(name)) byName[name!] = engineObj;
        }
        if (byName.Count == 0) return null;

        object? engineInstance;
        string engineName;
        if (requestedEngine != null)
        {
            if (!byName.TryGetValue(requestedEngine, out engineInstance))
                return Fail($"Python engine '{requestedEngine}' is not loaded in this Dynamo. " +
                            $"Available engines: {string.Join(", ", byName.Keys)}.");
            engineName = requestedEngine;
        }
        else
        {
            engineName = KnownEngines.FirstOrDefault(byName.ContainsKey) ?? byName.Keys.First();
            engineInstance = byName[engineName];
        }

        var evaluate = engineInstance!.GetType().GetMethod(
            "Evaluate", BindingFlags.Public | BindingFlags.Instance,
            [typeof(string), typeof(System.Collections.IList), typeof(System.Collections.IList)]);
        if (evaluate == null) return null;

        object? result;
        try
        {
            result = evaluate.Invoke(engineInstance,
                [WrapForDirectEvaluation(code), new System.Collections.ArrayList(), new System.Collections.ArrayList()]);
        }
        catch (Exception ex)
        {
            var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
            // Engine exists but evaluation blew up below the harness (engine init, marshal…).
            // Do NOT fall back to the graph path: the snippet may have partially executed,
            // and running it again could double-commit model changes.
            return Fail($"Python engine {engineName} failed to evaluate the script: {inner.Message}. " +
                        "If the script may have started, verify the model state with query tools " +
                        "before re-running it.");
        }
        finally
        {
            // A script using TransactionManager.Instance.EnsureInTransaction relies on
            // Dynamo's graph-run lifecycle to commit at the end of the run. There is no
            // run here, so an open RevitServices transaction would be silently rolled
            // back by the next model reset — close (commit) it the way Dynamo's own
            // evaluation loop does. No-op when the script used explicit Transactions.
            ForceCloseDynamoTransaction();
        }

        var reportJson = result?.ToString();
        if (string.IsNullOrWhiteSpace(reportJson))
            return Fail($"Python engine {engineName} returned no report. The MODEL STATE IS UNKNOWN — " +
                        "verify with query tools before re-running anything.");
        try
        {
            using var doc = JsonDocument.Parse(reportJson!);
            return BuildResponse(doc.RootElement.Clone(), engineName, app);
        }
        catch (Exception ex)
        {
            return Fail($"Python engine {engineName} ran but its report could not be parsed ({ex.Message}). " +
                        "The MODEL STATE IS UNKNOWN — verify with query tools before re-running anything.");
        }
    }

    // Same harness as the graph path, but the report comes back as the evaluation result
    // instead of going through a temp file.
    private static string WrapForDirectEvaluation(string userCode)
    {
        var codeB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(userCode));
        return $$"""
import base64 as __cr_b64, json as __cr_json, traceback as __cr_tb, sys as __cr_sys
__cr_report = {'python': __cr_sys.version.split()[0]}
try:
    exec(compile(__cr_b64.b64decode('{{codeB64}}').decode('utf-8'), '<claude_snippet>', 'exec'), globals())
    if 'OUT' in globals():
        try:
            __cr_report['out'] = __cr_json.dumps(globals()['OUT'], default=repr)
        except Exception:
            __cr_report['out'] = repr(globals()['OUT'])
except BaseException:
    __cr_report['error'] = __cr_tb.format_exc()
try:
    import clr
    clr.AddReference('RevitServices')
    from RevitServices.Persistence import DocumentManager as __cr_dm
    __cr_doc = __cr_dm.Instance.CurrentDBDocument
    __cr_report['document'] = {'title': __cr_doc.Title, 'path': __cr_doc.PathName}
except Exception:
    pass
OUT = __cr_json.dumps(__cr_report)
""";
    }

    // Fallback for Dynamo versions without PythonEngineManager: a full journal-driven
    // graph run. Returns the parsed run report (null = no report file → the node almost
    // certainly never ran), a parse-error message when a report exists but is unreadable,
    // the graph path (kept on disk when there was no report, for manual inspection) and
    // the engine actually used.
    private static (JsonElement? Report, string? ParseError, string? DynPath, string Engine) RunGraph(
        System.Type dynamoRevit, UIApplication app, string code, string? engine)
    {
        string? dynPath = null;
        var succeeded = false;
        var reportPath = Path.Combine(Path.GetTempPath(),
            "ClaudeRevit_" + Guid.NewGuid().ToString("N") + ".report.json");
        try
        {
            // The graph is written AFTER the boot so auto-detection can look at the Python
            // engine assemblies the booted model loaded.
            engine ??= DetectEngine();
            dynPath = WriteGraph(WrapInHarness(code, reportPath), engine);

            // Call 2 — run: with the model StartedUIless and NO shutdown key, ExecuteCommand
            // routes to TryOpenAndExecuteWorkspaceInCommandData, which opens the graph; in
            // automation mode opening runs it synchronously on this thread ("the model will
            // run anyway ... regardless of the DynPathExecuteKey"). dynPathExecute is kept
            // for older DynamoRevit versions where the explicit Run() branch needs it.
            InvokeExecuteCommand(dynamoRevit, app, new Dictionary<string, string>
            {
                ["dynShowUI"] = "False",
                ["dynAutomation"] = "True",
                ["dynPath"] = dynPath,
                ["dynPathExecute"] = "True"
            });

            if (!File.Exists(reportPath))
                return (null, null, dynPath, engine);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
                succeeded = true;
                return (doc.RootElement.Clone(), null, dynPath, engine);
            }
            catch (Exception ex)
            {
                // A present-but-unreadable report means the node DID run; never treat this
                // as "did not execute".
                return (null, ex.Message, dynPath, engine);
            }
        }
        finally
        {
            // Keep the graph on failure so it can be opened manually for diagnosis.
            try { if (succeeded && dynPath != null && File.Exists(dynPath)) File.Delete(dynPath); } catch { }
            try { if (File.Exists(reportPath)) File.Delete(reportPath); } catch { }
        }
    }

    // Tail of the newest Dynamo session log (%AppData%\Dynamo\Dynamo Revit\<ver>\Logs\
    // dynamoLog_*.txt) — where Dynamo records graph-open failures, node deserialization
    // problems and missing-engine errors that never surface through ExecuteCommand.
    private static string? ReadDynamoLogTail()
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Dynamo", "Dynamo Revit");
            if (!Directory.Exists(root)) return null;

            var newest = Directory.GetDirectories(root)
                .Select(v => Path.Combine(v, "Logs"))
                .Where(Directory.Exists)
                .SelectMany(logs => Directory.GetFiles(logs, "dynamoLog_*.txt"))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest == null) return null;

            // Share-friendly read: Dynamo may still hold the file open.
            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            const int tail = 3000;
            var slice = text.Length <= tail ? text : text[^tail..];
            return $"[{newest.FullName}]\n" + slice;
        }
        catch
        {
            return null;
        }
    }

    // Picks the Python engine the booted Dynamo actually ships, by looking for its engine
    // assemblies in the AppDomain (they load as Dynamo extensions during model start —
    // e.g. DSPythonNet3Extension in Dynamo 4.x, DSCPython in 2.5–3.x, DSIronPython in 2.x).
    // Dynamo 4.x dropped CPython3, so a hardcoded default cannot work across versions.
    private static string DetectEngine()
    {
        var names = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => { try { return a.GetName().Name ?? ""; } catch { return ""; } })
            .ToList();

        if (names.Any(n => n.Contains("PythonNet3", StringComparison.OrdinalIgnoreCase)))
            return "PythonNet3";
        if (names.Any(n => n.Contains("DSCPython", StringComparison.OrdinalIgnoreCase)))
            return "CPython3";
        if (names.Any(n => n.Contains("DSIronPython", StringComparison.OrdinalIgnoreCase)))
            return "IronPython2";

        // Nothing loaded (engines can load lazily on some versions) — assume the newest.
        return "PythonNet3";
    }

    // DynamoRevit.ExecuteCommand(DynamoRevitCommandData) driven via journal data. Built
    // entirely by reflection so the build has no Dynamo dependency; any shape mismatch
    // throws and is caught by Execute's outer handler.
    private static void InvokeExecuteCommand(
        System.Type dynamoRevit, UIApplication app, Dictionary<string, string> journalData)
    {
        var cmdDataType = FindType("Dynamo.Applications.DynamoRevitCommandData")
                          ?? throw new InvalidOperationException("DynamoRevitCommandData type not found.");
        var cmdData = Activator.CreateInstance(cmdDataType)
                      ?? throw new InvalidOperationException("Could not create DynamoRevitCommandData.");
        SetMember(cmdData, "Application", app);
        SetMember(cmdData, "JournalData", journalData);

        var instance = Activator.CreateInstance(dynamoRevit)
                       ?? throw new InvalidOperationException("Could not create DynamoRevit.");
        var exec = dynamoRevit.GetMethod("ExecuteCommand", BindingFlags.Public | BindingFlags.Instance)
                   ?? throw new InvalidOperationException("DynamoRevit.ExecuteCommand not found.");
        exec.Invoke(instance, new[] { cmdData });
    }

    // Current DynamoRevit keeps the state on the static RevitDynamoModel instance
    // (State: NotStarted / StartedUIless / StartedUI); very old versions exposed a static
    // ModelState property instead. Null when neither exists or nothing started yet.
    // EnsureInTransaction opens a transaction that Dynamo normally commits at the end of
    // a graph run; direct evaluation has no run lifecycle, so commit it explicitly.
    private static void ForceCloseDynamoTransaction()
    {
        try
        {
            var tmType = FindType("RevitServices.Transactions.TransactionManager");
            var instance = tmType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            instance?.GetType()
                .GetMethod("ForceCloseTransaction", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(instance, null);
        }
        catch { /* best-effort */ }
    }

    private static string? GetDynamoState(System.Type dynamoRevit)
    {
        try
        {
            var modelProp = dynamoRevit.GetProperty("RevitDynamoModel", BindingFlags.Public | BindingFlags.Static);
            var model = modelProp?.GetValue(null);
            var state = model?.GetType().GetProperty("State")?.GetValue(model)?.ToString();
            if (state != null) return state;

            var legacy = dynamoRevit.GetProperty("ModelState", BindingFlags.Public | BindingFlags.Static);
            return legacy?.GetValue(null)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    // Turns the run report written by the harness into the tool result. The report file is
    // the only reliable execution signal: DynamoRevit's own return value only says the graph
    // was opened.
    private static string BuildResponse(JsonElement report, string engine, UIApplication app)
    {
        var error = report.TryGetProperty("error", out var e) ? e.GetString() : null;
        var output = report.TryGetProperty("out", out var o) ? o.GetString() : null;
        var pythonVersion = report.TryGetProperty("python", out var pv) ? pv.GetString() : null;
        if (output is { Length: > 20000 })
            output = output[..20000] + " …(truncated)";

        string? dynDocTitle = null, dynDocPath = null;
        if (report.TryGetProperty("document", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            dynDocTitle = d.TryGetProperty("title", out var t) ? t.GetString() : null;
            dynDocPath = d.TryGetProperty("path", out var p) ? p.GetString() : null;
        }

        // The script runs against DocumentManager.Instance.CurrentDBDocument; if that is not
        // the document the other tools read from, say so instead of leaving both sides to
        // silently disagree.
        string? warning = null;
        var activeDoc = app.ActiveUIDocument?.Document;
        if (activeDoc != null && dynDocTitle != null &&
            !string.Equals(dynDocTitle, activeDoc.Title, StringComparison.Ordinal))
        {
            warning = $"The script ran against document '{dynDocTitle}' ({dynDocPath}) but the " +
                      $"active document is '{activeDoc.Title}' — changes went to a different document.";
        }

        if (error != null)
        {
            Log.Info("run_dynamo_python: script raised an exception.");
            return JsonSerializer.Serialize(new
            {
                ok = false,
                engine,
                python = pythonVersion,
                error = "The Python script raised an exception. IMPORTANT: transactions the script " +
                        "COMMITTED before the exception persist in the model — verify what was " +
                        "actually changed before re-running:\n" + error,
                document = dynDocTitle,
                warning
            });
        }

        Log.Info("run_dynamo_python executed a graph.");
        return JsonSerializer.Serialize(new
        {
            ok = true,
            engine,
            python = pythonVersion,
            result = "Script executed to completion.",
            output,
            document = dynDocTitle,
            warning
        });
    }

    // Wraps the user's snippet so we get real execution feedback. The snippet itself is
    // base64-encoded (no escaping pitfalls) and exec'd against the node's globals, so it
    // behaves exactly as if it were the node's own code (IN/OUT/clr all visible). Whatever
    // happens — success, OUT value, exception — is written to the report file; the file's
    // very existence is the proof that the node actually ran.
    //
    // Deliberately Python-2/3 compatible (io.open in binary mode, ASCII-only JSON) so the
    // same harness runs on both the CPython3 and IronPython2 engines.
    private static string WrapInHarness(string userCode, string reportPath)
    {
        var codeB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(userCode));
        var pathLiteral = JsonSerializer.Serialize(reportPath); // JSON string == valid Python literal
        return $$"""
import base64 as __cr_b64, json as __cr_json, traceback as __cr_tb, io as __cr_io, sys as __cr_sys
__cr_report = {'python': __cr_sys.version.split()[0]}
try:
    exec(compile(__cr_b64.b64decode('{{codeB64}}').decode('utf-8'), '<claude_snippet>', 'exec'), globals())
    if 'OUT' in globals():
        try:
            __cr_report['out'] = __cr_json.dumps(globals()['OUT'], default=repr)
        except Exception:
            __cr_report['out'] = repr(globals()['OUT'])
except BaseException:
    __cr_report['error'] = __cr_tb.format_exc()
try:
    import clr
    clr.AddReference('RevitServices')
    from RevitServices.Persistence import DocumentManager as __cr_dm
    __cr_doc = __cr_dm.Instance.CurrentDBDocument
    __cr_report['document'] = {'title': __cr_doc.Title, 'path': __cr_doc.PathName}
except Exception:
    pass
try:
    __cr_payload = __cr_json.dumps(__cr_report)
except Exception:
    __cr_payload = '{"error": "run report serialization failed"}'
try:
    __cr_f = __cr_io.open({{pathLiteral}}, 'wb')
    try:
        __cr_f.write(__cr_payload.encode('ascii'))
    finally:
        __cr_f.close()
except Exception:
    pass
OUT = 0
""";
    }

    private static string Fail(string message) =>
        JsonSerializer.Serialize(new { ok = false, engine = "dynamo", error = message });

    // A Dynamo graph (.dyn JSON) with a single Python Script node containing the code.
    // Mirrors, field for field, a .dyn that Dynamo itself saves (reference: the python.dyn
    // test file in the DynamoDS/Dynamo repo): DynamoModel.OpenJsonFileFromPath throws
    // NullReferenceException on files missing the standard blocks (ElementResolver,
    // Bindings, the full View section with Camera/NodeViews), so a "minimal" graph is not
    // an option. RunType=Automatic + HasRunWithoutCrash=true are what make the graph run
    // on open (files without them are forced into Manual run mode and never evaluate).
    private static string WriteGraph(string code, string engine)
    {
        var nodeId = Guid.NewGuid().ToString("N");
        var graph = new
        {
            Uuid = Guid.NewGuid().ToString("D"),
            IsCustomNode = false,
            Description = "",
            Name = "ClaudeRevit",
            ElementResolver = new { ResolutionMap = new { } },
            Inputs = Array.Empty<object>(),
            Outputs = Array.Empty<object>(),
            Nodes = new object[]
            {
                new
                {
                    ConcreteType = "PythonNodeModels.PythonNode, PythonNodeModels",
                    NodeType = "PythonScriptNode",
                    Code = code,
                    // Older Dynamo deserializes "Engine", newer builds "EngineName"; unknown
                    // JSON properties are ignored, so write both.
                    Engine = engine,
                    EngineName = engine,
                    VariableInputPorts = true,
                    Id = nodeId,
                    Inputs = new object[]
                    {
                        new
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Name = "IN[0]",
                            Description = "Input #0",
                            UsingDefaultValue = false,
                            Level = 2,
                            UseLevels = false,
                            KeepListStructure = false
                        }
                    },
                    Outputs = new object[]
                    {
                        new
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Name = "OUT",
                            Description = "Result of the python script",
                            UsingDefaultValue = false,
                            Level = 2,
                            UseLevels = false,
                            KeepListStructure = false
                        }
                    },
                    Replication = "Disabled",
                    Description = "Runs an embedded Python script."
                }
            },
            Connectors = Array.Empty<object>(),
            Dependencies = Array.Empty<object>(),
            NodeLibraryDependencies = Array.Empty<object>(),
            Bindings = Array.Empty<object>(),
            View = new
            {
                // No Version field on purpose: DynamoModel treats a null workspace version
                // as "current Dynamo version", which is always right — a hardcoded number
                // (Revit 2027 ships Dynamo 27.x, not 3.x) risks migration-path surprises.
                Dynamo = new
                {
                    ScaleFactor = 1.0,
                    HasRunWithoutCrash = true,
                    IsVisibleInDynamoLibrary = true,
                    RunType = "Automatic",
                    RunPeriod = "1000"
                },
                Camera = new
                {
                    Name = "Background Preview",
                    EyeX = -17.0,
                    EyeY = 24.0,
                    EyeZ = 50.0,
                    LookX = 12.0,
                    LookY = -13.0,
                    LookZ = -58.0,
                    UpX = 0.0,
                    UpY = 1.0,
                    UpZ = 0.0
                },
                NodeViews = new object[]
                {
                    new
                    {
                        ShowGeometry = true,
                        Name = "Python Script",
                        Id = nodeId,
                        IsSetAsInput = false,
                        IsSetAsOutput = false,
                        Excluded = false,
                        X = 259.0,
                        Y = 148.5
                    }
                },
                Annotations = Array.Empty<object>(),
                X = 0.0,
                Y = 0.0,
                Zoom = 1.0
            }
        };

        var path = Path.Combine(Path.GetTempPath(), "ClaudeRevit_" + Guid.NewGuid().ToString("N") + ".dyn");
        File.WriteAllText(path, JsonSerializer.Serialize(graph), Encoding.UTF8);
        return path;
    }

    private static System.Type? FindDynamoRevitType() =>
        FindType("Dynamo.Applications.DynamoRevit");

    private static System.Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            System.Type? t;
            try { t = asm.GetType(fullName, throwOnError: false); }
            catch { t = null; }
            if (t != null) return t;
        }
        return null;
    }

    private static void SetMember(object target, string name, object value)
    {
        var t = target.GetType();
        var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop != null && prop.CanWrite) { prop.SetValue(target, value); return; }
        var field = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (field != null) { field.SetValue(target, value); return; }
        throw new InvalidOperationException($"DynamoRevitCommandData has no writable '{name}'.");
    }
}
