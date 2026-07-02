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
// Execution feedback: DynamoRevit.ExecuteCommand returns Succeeded as long as the graph
// merely OPENED, so the snippet is wrapped in a harness that writes a run report (OUT,
// traceback, document identity) to a temp file. No report file after the call means the
// Python node never ran, and that is surfaced as an error instead of a fake success.
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
                description = "Python engine (default CPython3). If the chosen engine never executes the node, the other one is tried automatically."
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

            // The headless journal path only executes when Dynamo is NOT showing its UI —
            // with the Dynamo window open the run is routed through UI commands and the
            // node never executes. Fail fast with a clear action instead of a silent no-op.
            var modelState = GetModelState(dynamoRevit);
            if (modelState == "StartedUIThread")
                return Fail("The Dynamo window is currently open in Revit, which blocks headless " +
                            "script execution. Ask the user to close the Dynamo window, then retry.");

            var requested = input.TryGetValue("engine", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() : null;
            // Requested engine first; if the node never executes on it (e.g. that engine is
            // not installed), retry once on the other engine before giving up.
            var engines = requested == "IronPython2"
                ? new[] { "IronPython2", "CPython3" }
                : new[] { "CPython3", "IronPython2" };

            var attempts = new List<string>();
            foreach (var engine in engines)
            {
                var (report, dynamoResult) = RunOnce(dynamoRevit, app, code!, engine);
                attempts.Add($"{engine}: {(report != null ? "executed" : $"did not execute (Dynamo returned '{dynamoResult}')")}");
                if (report != null)
                    return BuildResponse(report.Value, engine, app);
            }

            return Fail(
                "The Python node never executed on either engine (it wrote no run report), so NO " +
                "model changes were made. Attempts: " + string.Join("; ", attempts) + ". " +
                "Likely causes: this Dynamo installation has no working Python engine, or the " +
                "Dynamo/Revit version routes journal runs differently. Check Dynamo's own log " +
                "(%AppData%\\Dynamo\\Dynamo Revit\\<version>\\Logs) for node errors.");
        }
        catch (Exception ex)
        {
            // Never rethrow — a crash here is exactly what we're avoiding.
            Log.Error("run_dynamo_python failed", ex);
            return Fail("Dynamo execution failed: " + (ex.InnerException?.Message ?? ex.Message));
        }
    }

    // One full headless journal run on the given engine. Returns the parsed run report
    // (null if the node never executed) plus DynamoRevit's own result string.
    private static (JsonElement? Report, string DynamoResult) RunOnce(
        System.Type dynamoRevit, UIApplication app, string code, string engine)
    {
        string? dynPath = null;
        var reportPath = Path.Combine(Path.GetTempPath(),
            "ClaudeRevit_" + Guid.NewGuid().ToString("N") + ".report.json");
        try
        {
            dynPath = WriteGraph(WrapInHarness(code, reportPath), engine);

            // DynamoRevit.ExecuteCommand(DynamoRevitCommandData) drives a headless run via
            // journal data. Built entirely by reflection so we don't depend on Dynamo at
            // compile time; any shape mismatch is caught and reported, never thrown to Revit.
            var journalData = new Dictionary<string, string>
            {
                ["dynPath"] = dynPath,
                // Without dynPathExecute the UIless path merely OPENS the graph — and a .dyn
                // that has never run in the UI always opens in Manual run mode, so it is shut
                // down again without a single evaluation while ExecuteCommand still returns
                // Succeeded. dynPathExecute makes DynamoRevit issue an explicit RunCancel
                // command after opening, which in automation mode runs synchronously.
                ["dynPathExecute"] = "True",
                ["dynAutomation"] = "True",
                ["dynShowUI"] = "False",
                ["dynModelShutDown"] = "True"
            };

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

            var result = exec.Invoke(instance, new[] { cmdData });
            var dynamoResult = result?.ToString() ?? "Succeeded";

            if (!File.Exists(reportPath))
                return (null, dynamoResult);
            using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
            return (doc.RootElement.Clone(), dynamoResult);
        }
        finally
        {
            try { if (dynPath != null && File.Exists(dynPath)) File.Delete(dynPath); } catch { }
            try { if (File.Exists(reportPath)) File.Delete(reportPath); } catch { }
        }
    }

    private static string? GetModelState(System.Type dynamoRevit)
    {
        try
        {
            var prop = dynamoRevit.GetProperty("ModelState", BindingFlags.Public | BindingFlags.Static);
            return prop?.GetValue(null)?.ToString();
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
                error = "The Python script raised an exception (any transaction it left open was " +
                        "rolled back):\n" + error,
                document = dynDocTitle,
                warning
            });
        }

        Log.Info("run_dynamo_python executed a graph.");
        return JsonSerializer.Serialize(new
        {
            ok = true,
            engine,
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
__cr_report = {'executed': True, 'python': __cr_sys.version.split()[0]}
try:
    exec(compile(__cr_b64.b64decode('{{codeB64}}').decode('utf-8'), '<claude_snippet>', 'exec'), globals())
    if 'OUT' in globals():
        try:
            __cr_report['out'] = __cr_json.dumps(globals()['OUT'], default=repr)
        except Exception:
            __cr_report['out'] = repr(globals()['OUT'])
except Exception:
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
    __cr_f = __cr_io.open({{pathLiteral}}, 'wb')
    try:
        __cr_f.write(__cr_json.dumps(__cr_report).encode('ascii'))
    finally:
        __cr_f.close()
except Exception:
    pass
OUT = __cr_report
""";
    }

    private static string Fail(string message) =>
        JsonSerializer.Serialize(new { ok = false, engine = "dynamo", error = message });

    // Minimal Dynamo graph (.dyn JSON) with a single Python Script node containing the code.
    private static string WriteGraph(string code, string engine)
    {
        var nodeId = "11111111-1111-1111-1111-111111111111";
        var graph = new
        {
            Uuid = "00000000-0000-0000-0000-000000000000",
            IsCustomNode = false,
            Description = "",
            Name = "ClaudeRevit",
            Nodes = new object[]
            {
                new
                {
                    ConcreteType = "PythonNodeModels.PythonNode, PythonNodeModels",
                    Id = nodeId,
                    NodeType = "PythonScriptNode",
                    Code = code,
                    // Older Dynamo deserializes "Engine", newer builds "EngineName"; unknown
                    // JSON properties are ignored, so write both.
                    Engine = engine,
                    EngineName = engine,
                    VariableInputPorts = true,
                    Inputs = Array.Empty<object>(),
                    Outputs = new object[]
                    {
                        new
                        {
                            Id = "22222222-2222-2222-2222-222222222222",
                            Name = "OUT",
                            Description = "Result of the python script",
                            UsingDefaultValue = false,
                            Level = 2,
                            UseLevels = false,
                            KeepListStructure = false
                        }
                    },
                    Replication = "Disabled"
                }
            },
            Connectors = Array.Empty<object>(),
            // RunType/HasRunWithoutCrash matter on code paths that honour the file's own run
            // settings (e.g. when the Dynamo UI is already open): without them Dynamo forces
            // Manual run mode and the graph would open without evaluating.
            View = new
            {
                Dynamo = new
                {
                    Version = "3.0.0.0",
                    RunType = "Automatic",
                    HasRunWithoutCrash = true
                }
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
