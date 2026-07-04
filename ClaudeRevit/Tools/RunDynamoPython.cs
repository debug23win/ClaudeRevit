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

// Secondary (Python) escape hatch — execute_csharp is the default one. Kept for proven
// Python snippets, Dynamo-community code and users who ask for Python.
//
// Everything is late-bound via reflection so the build has no Dynamo dependency, and
// EVERY failure path returns an error string — this tool must never crash Revit. If
// Dynamo is not installed it says so and suggests enabling execute_csharp instead.
//
// Execution flow: one journal call boots a headless (dynShowUI=False — no WebView2
// splash) Dynamo model bound to the current document, which also loads Dynamo's Python
// engine extensions. The snippet is then evaluated DIRECTLY through the engine object in
// Dynamo.PythonServices.PythonEngineManager — a synchronous call on this thread, immune
// to graph-schema, run-mode and model-lifecycle quirks. (The old journal-driven .dyn
// graph run was removed: it only served pre-PythonEngineManager Dynamo, which cannot
// ship with the Revit 2027 this add-in targets, and it was the most failure-prone code
// in the project — see the v1.9–v1.12 history.)
//
// Execution feedback: the harness returns a run report (OUT, traceback, document
// identity) as the evaluation result. Errors are honest — the tool never re-runs the
// snippet by itself, because a failed report cannot prove the code did not execute
// (partial commits may have happened).
public class RunDynamoPython : IRevitTool
{
    public string Name => "run_dynamo_python";

    public string Description =>
        "Runs a Python snippet through Dynamo for Revit's Python engine. PREFER execute_csharp " +
        "for new code — it is faster (no Dynamo boot) and its transaction rolls back " +
        "automatically on error; use this tool when Python is specifically better: a proven " +
        "snippet from get_script_journal, code adapted from the Dynamo community, or the user " +
        "asked for Python. The snippet runs " +
        "with the standard Dynamo-Revit Python environment: 'clr' is imported and you have " +
        "access to RevitServices (DocumentManager.Instance.CurrentDBDocument for the document, " +
        "TransactionManager.Instance to manage transactions) and the Revit API " +
        "(Autodesk.Revit.DB). Wrap model changes in an EXPLICIT Autodesk.Revit.DB.Transaction " +
        "(t = Transaction(doc, 'name'); t.Start(); …; t.Commit()) — that is the reliable " +
        "pattern here. TransactionManager.Instance.EnsureInTransaction also works: the tool " +
        "commits it after a successful script and ROLLS IT BACK when the script raises. " +
        "Put the result into the Dynamo output " +
        "variable 'OUT': it is serialized and returned in the tool result ('output'), and if " +
        "the script raises, the full traceback is returned. LEAVE 'engine' UNSET — it is " +
        "auto-detected from the running Dynamo (Dynamo 4.x / Revit 2027 ships ONLY PythonNet3; " +
        "CPython3/IronPython2 exist only in older versions or as separately installed packages). " +
        "Revit 2027 API note: use ElementId.Value (long) — ElementId.IntegerValue was removed. " +
        "Requires Dynamo for Revit and the user's code-execution opt-in (plus per-run " +
        "confirmation if the user enabled it in settings).";

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

    // The script manages its own transactions (and the tool closes leftovers) — we must
    // NOT wrap the call in a dispatcher transaction.
    public bool RequiresTransaction => false;
    public bool RequiresConfirmation => true;
    public bool RequiresCodeExecutionOptIn => true;
    public bool IsScriptTool => true;
    public bool MutatesWithoutTransaction => true;

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

            // The direct evaluation only works headlessly — with the Dynamo window open,
            // the model belongs to the user's UI session. Fail fast with a clear action
            // instead of a silent no-op.
            var modelState = GetDynamoState(dynamoRevit);
            if (modelState is "StartedUI" or "StartedUIThread")
                return Fail("The Dynamo window is currently open in Revit, which blocks headless " +
                            "script execution. Ask the user to close the Dynamo window, then retry.");

            // A full Dynamo model reboot costs ~15s of frozen UI, and its only purpose is
            // rebinding to the CURRENT document — skip it when a healthy UIless model is
            // already bound to this very document. Fail open (reboot) in every doubtful
            // case, and always reboot after a failed run: a crashed script may have left
            // engine state dirty.
            var activeDoc = app.ActiveUIDocument?.Document;
            var docKey = activeDoc == null
                ? null
                : activeDoc.Title + "|" + activeDoc.PathName + "|" + activeDoc.GetHashCode();
            if (docKey == null || docKey != _lastBootDocKey || modelState != "StartedUIless")
            {
                // Boot (or reset) the headless Dynamo model bound to the current document.
                // This also loads Dynamo's Python engine extensions into the process.
                BootDynamo(dynamoRevit, app);
            }
            _lastBootDocKey = null; // re-armed below only after a clean success

            // Single execution path: call the Python engine DIRECTLY via
            // PythonEngineManager — synchronous, on this thread, no .dyn file, no graph
            // open/run semantics, no race with Dynamo's model lifecycle.
            var response = TryDirectEvaluate(app, code!, engine);
            if (IsOkResponse(response))
                _lastBootDocKey = docKey;
            return response;
        }
        catch (Exception ex)
        {
            // Never rethrow — a crash here is exactly what we're avoiding.
            Log.Error("run_dynamo_python failed", ex);
            return Fail("Dynamo execution failed: " + (ex.InnerException?.Message ?? ex.Message) +
                        ". If the script may have started, " + VerifyGuidance);
        }
    }

    // Engine names in preference order; used both for input validation and detection.
    private static readonly string[] KnownEngines = ["PythonNet3", "CPython3", "IronPython2"];

    // Document the last (successful) boot bound the UIless model to — see Execute.
    private static string? _lastBootDocKey;

    // Load-bearing prompt engineering: this is what stops the model from blindly
    // re-running a script that may have half-committed transactions. One copy only.
    private const string VerifyGuidance =
        "verify the actual model state with query tools (query_elements / " +
        "get_element_parameters / get_model_statistics) before re-running anything that " +
        "mutates the model.";

    private static bool IsOkResponse(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

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

    // Single execution path: evaluate the snippet through the Python engine object that
    // the booted Dynamo registered in Dynamo.PythonServices.PythonEngineManager — a plain
    // synchronous method call on this thread. Always returns a complete tool response,
    // with a SPECIFIC diagnosis per failure point (an engine-API drift must not read as
    // "no engines installed").
    private static string TryDirectEvaluate(UIApplication app, string code, string? requestedEngine)
    {
        object? manager;
        try
        {
            var managerType = FindType("Dynamo.PythonServices.PythonEngineManager");
            if (managerType == null)
                return Fail("Dynamo.PythonServices.PythonEngineManager was not found — this Dynamo " +
                            "predates the direct Python API this tool requires. Use execute_csharp instead.");
            manager = managerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
        }
        catch (Exception ex)
        {
            return Fail("Reading PythonEngineManager failed: " + ex.Message + ". Use execute_csharp instead.");
        }
        if (manager == null)
            return Fail("PythonEngineManager.Instance returned null — Dynamo's Python services did " +
                        "not initialize. Use execute_csharp instead.");

        // AvailableEngines is a public field on current Dynamo; tolerate a property too.
        var enginesMember =
            (object?)manager.GetType().GetField("AvailableEngines", BindingFlags.Public | BindingFlags.Instance)?.GetValue(manager)
            ?? manager.GetType().GetProperty("AvailableEngines", BindingFlags.Public | BindingFlags.Instance)?.GetValue(manager);
        if (enginesMember is not System.Collections.IEnumerable engines)
            return Fail("PythonEngineManager exists but its AvailableEngines member has an unexpected " +
                        "shape — Dynamo's API changed. Use execute_csharp and report this so the tool " +
                        "can be updated.");

        var byName = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var engineObj in engines)
        {
            if (engineObj == null) continue;
            var name = engineObj.GetType().GetProperty("Name")?.GetValue(engineObj)?.ToString();
            if (!string.IsNullOrEmpty(name)) byName[name!] = engineObj;
        }
        if (byName.Count == 0)
        {
            var dynamoLog = ReadDynamoLogTail();
            return Fail("Dynamo booted but has NO Python engines loaded — this installation cannot " +
                        "run Python headlessly. Use execute_csharp instead. " +
                        (dynamoLog != null ? "Tail of Dynamo's own session log:\n" + dynamoLog : ""));
        }

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

        // The learning journal should record the engine that actually ran, not the
        // (usually absent) request input.
        ScriptJournal.SetEngine(engineName);

        var evaluate = engineInstance!.GetType().GetMethod(
            "Evaluate", BindingFlags.Public | BindingFlags.Instance,
            [typeof(string), typeof(System.Collections.IList), typeof(System.Collections.IList)]);
        if (evaluate == null)
            return Fail($"Python engine {engineName} is loaded but has no " +
                        "Evaluate(string, IList, IList) method — Dynamo's engine API changed. " +
                        "Use execute_csharp and report this so the tool can be updated.");

        object? result;
        try
        {
            result = evaluate.Invoke(engineInstance,
                [WrapForDirectEvaluation(code), new System.Collections.ArrayList(), new System.Collections.ArrayList()]);
        }
        catch (Exception ex)
        {
            // Engine exists but evaluation blew up below the harness (engine init, marshal…).
            // The snippet may have partially executed — roll back anything it left open.
            CloseDynamoTransaction(commit: false);
            var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
            return Fail($"Python engine {engineName} failed to evaluate the script: {inner.Message}. " +
                        "If the script may have started, " + VerifyGuidance);
        }

        var reportJson = result?.ToString();
        if (string.IsNullOrWhiteSpace(reportJson))
        {
            CloseDynamoTransaction(commit: false);
            return Fail($"Python engine {engineName} returned no report. The MODEL STATE IS UNKNOWN — " +
                        VerifyGuidance);
        }

        JsonElement report;
        try
        {
            using var doc = JsonDocument.Parse(reportJson!);
            report = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            CloseDynamoTransaction(commit: false);
            return Fail($"Python engine {engineName} ran but its report could not be parsed ({ex.Message}). " +
                        "The MODEL STATE IS UNKNOWN — " + VerifyGuidance);
        }

        // A script using TransactionManager.Instance.EnsureInTransaction relies on Dynamo's
        // graph-run lifecycle to close the transaction; there is no run here, so close it
        // ourselves — COMMIT only when the script finished cleanly, ROLL BACK its half-done
        // work when it raised. No-op for scripts using explicit Transactions.
        CloseDynamoTransaction(commit: !report.TryGetProperty("error", out _));

        return BuildResponse(report, engineName, app);
    }

    // Wraps the user's snippet so its OUT value, traceback and document identity come back
    // as the evaluation result (a JSON string).
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

    // Closes a RevitServices transaction the script left open via EnsureInTransaction:
    // commit=true uses the public ForceCloseTransaction (commits, as Dynamo's own run
    // lifecycle does); commit=false cancels via the private TransactionHandle so a crashed
    // script's half-done work is ROLLED BACK, not persisted. No-op when nothing is open.
    private static void CloseDynamoTransaction(bool commit)
    {
        try
        {
            var tmType = FindType("RevitServices.Transactions.TransactionManager");
            var instance = tmType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (instance == null) return;

            var wrapper = instance.GetType().GetProperty("TransactionWrapper")?.GetValue(instance);
            if (wrapper?.GetType().GetProperty("TransactionActive")?.GetValue(wrapper) is not true)
                return;

            if (!commit)
            {
                var handle = instance.GetType()
                    .GetField("handle", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);
                var cancel = handle?.GetType()
                    .GetMethod("CancelTransaction", BindingFlags.Public | BindingFlags.Instance);
                if (cancel != null)
                {
                    cancel.Invoke(handle, null);
                    return;
                }
                // The rollback path is version-fragile (private field) — rather than leave
                // the transaction open (it would block every later tool), fall through to
                // the public commit and let the caller's error text stay conservative.
            }

            instance.GetType()
                .GetMethod("ForceCloseTransaction", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(instance, null);
        }
        catch { /* best-effort */ }
    }

    // Current DynamoRevit keeps the state on the static RevitDynamoModel instance
    // (State: NotStarted / StartedUIless / StartedUI); very old versions exposed a static
    // ModelState property instead. Null when neither exists or nothing started yet.
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

    // Turns the run report (the harness's evaluation result) into the tool result.
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
                error = "The Python script raised an exception. An EnsureInTransaction transaction " +
                        "it left open was ROLLED BACK; transactions the script explicitly COMMITTED " +
                        "before the exception persist in the model — " + VerifyGuidance + "\n" + error,
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

    private static string Fail(string message) =>
        JsonSerializer.Serialize(new { ok = false, engine = "dynamo", error = message });

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
