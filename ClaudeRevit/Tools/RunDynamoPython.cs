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
// Execution flow (matches DynamoRevit.ExecuteCommand in current DynamoRevit sources):
// with dynShowUI=False the command NEVER opens a graph on the call that (re)initializes
// the UIless model — it boots the model and returns Succeeded. The graph is opened (and,
// in automation mode, run synchronously) only on a SUBSEQUENT call, when the model is
// already StartedUIless and no dynModelShutDown key is present. So the tool makes two
// calls per run: (1) reset+boot bound to the current document, (2) open+execute.
// dynShowUI=False also means the WebView2 splash screen is never shown, so a broken
// splash (e.g. unwritable WebView2 data folder) does not affect this path.
//
// Execution feedback: ExecuteCommand's return value only reflects opening, so the snippet
// is wrapped in a harness that writes a run report (OUT, traceback, document identity) to
// a temp file. A missing report is surfaced as an honest error instead of a fake success —
// and the tool never re-runs the snippet by itself, because a missing report cannot prove
// the code did not execute (partial commits may have happened).
public class RunDynamoPython : IRevitTool
{
    public string Name => "run_dynamo_python";

    public string Description =>
        "Runs a Python snippet through Dynamo for Revit — the preferred way to perform an " +
        "action the dedicated tools don't cover. Dynamo manages its own transaction, so this " +
        "is safer than execute_csharp. The snippet runs with the standard Dynamo-Revit Python " +
        "environment: 'clr' is imported and you have access to RevitServices " +
        "(DocumentManager.Instance.CurrentDBDocument for the document, " +
        "TransactionManager.Instance to manage transactions) and the Revit API " +
        "(Autodesk.Revit.DB). Wrap model changes in a transaction — either " +
        "TransactionManager.Instance.EnsureInTransaction(doc) + TransactionTaskDone(), or an " +
        "explicit Autodesk.Revit.DB.Transaction. Put the result into the Dynamo output " +
        "variable 'OUT': it is serialized and returned in the tool result ('output'), and if " +
        "the script raises, the full traceback is returned. " +
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
                @enum = new[] { "CPython3", "IronPython2" },
                description = "Python engine (default CPython3). The tool never retries by itself; " +
                              "if a run reports that the node did not execute, decide yourself whether " +
                              "to retry with the other engine."
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

            var engine = "CPython3";
            if (input.TryGetValue("engine", out var e) && e.ValueKind == JsonValueKind.String)
            {
                var wanted = e.GetString();
                if (string.Equals(wanted, "IronPython2", StringComparison.OrdinalIgnoreCase))
                    engine = "IronPython2";
                else if (!string.Equals(wanted, "CPython3", StringComparison.OrdinalIgnoreCase))
                    return Fail($"Unknown engine '{wanted}'. Valid values: CPython3, IronPython2.");
            }

            // The headless journal path only executes when Dynamo is NOT showing its UI —
            // with the Dynamo window open, ExecuteCommand routes the graph through the UI
            // view-model and the node never executes headlessly. Fail fast with a clear
            // action instead of a silent no-op.
            var modelState = GetDynamoState(dynamoRevit);
            if (modelState is "StartedUI" or "StartedUIThread")
                return Fail("The Dynamo window is currently open in Revit, which blocks headless " +
                            "script execution. Ask the user to close the Dynamo window, then retry.");

            var (report, parseError) = RunGraph(dynamoRevit, app, code!, engine);

            if (parseError != null)
                return Fail("The Python node ran but its run report could not be parsed (" + parseError +
                            "). The MODEL STATE IS UNKNOWN — the script may have committed changes. " +
                            "Verify with query tools (query_elements / get_element_parameters) before " +
                            "re-running anything.");

            if (report == null)
                return Fail(
                    $"The Python node wrote no run report on engine {engine}, so it most likely never " +
                    "executed — but this cannot be fully guaranteed, so VERIFY with query tools before " +
                    "re-running a model-mutating script. Likely causes: this Dynamo installation lacks " +
                    $"the {engine} engine (you may retry once with the other engine explicitly), or the " +
                    "graph failed to open. Dynamo's own log is at %AppData%\\Dynamo\\Dynamo Revit\\<version>\\Logs.");

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

    // One full headless run on the given engine, in the two calls current DynamoRevit
    // requires (see the file header): reset+boot, then open+execute. Returns the parsed
    // run report (null = no report file → the node almost certainly never ran) and a
    // parse-error message when a report exists but is unreadable.
    private static (JsonElement? Report, string? ParseError) RunGraph(
        System.Type dynamoRevit, UIApplication app, string code, string engine)
    {
        string? dynPath = null;
        var reportPath = Path.Combine(Path.GetTempPath(),
            "ClaudeRevit_" + Guid.NewGuid().ToString("N") + ".report.json");
        try
        {
            dynPath = WriteGraph(WrapInHarness(code, reportPath), engine);

            // Call 1 — reset: if a UIless model is alive (possibly bound to a previously
            // active document), dynModelShutDown makes ExecuteCommand shut it down and boot
            // a fresh one bound to the CURRENT document. This call never opens a graph.
            InvokeExecuteCommand(dynamoRevit, app, new Dictionary<string, string>
            {
                ["dynShowUI"] = "False",
                ["dynAutomation"] = "True",
                ["dynModelShutDown"] = "True"
            });

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
                return (null, null);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
                return (doc.RootElement.Clone(), null);
            }
            catch (Exception ex)
            {
                // A present-but-unreadable report means the node DID run; never treat this
                // as "did not execute".
                return (null, ex.Message);
            }
        }
        finally
        {
            try { if (dynPath != null && File.Exists(dynPath)) File.Delete(dynPath); } catch { }
            try { if (File.Exists(reportPath)) File.Delete(reportPath); } catch { }
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
                Dynamo = new
                {
                    ScaleFactor = 1.0,
                    HasRunWithoutCrash = true,
                    IsVisibleInDynamoLibrary = true,
                    Version = "3.0.0.0",
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
