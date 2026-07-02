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
        "(Autodesk.Revit.DB). Put the result into the Dynamo output variable 'OUT'. " +
        "Requires Dynamo for Revit to be installed. The user must approve each run.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["code"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Python code for a Dynamo Python Script node. Assign the result to OUT."
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

        string? dynPath = null;
        try
        {
            var dynamoRevit = FindDynamoRevitType();
            if (dynamoRevit == null)
                return Fail("Dynamo for Revit was not found in this session. Install Dynamo for " +
                            "Revit, or ask the user to enable execute_csharp in settings as a fallback.");

            dynPath = WriteGraph(code!);

            // DynamoRevit.ExecuteCommand(DynamoRevitCommandData) drives a headless run via
            // journal data. Built entirely by reflection so we don't depend on Dynamo at
            // compile time; any shape mismatch is caught and reported, never thrown to Revit.
            var journalData = new Dictionary<string, string>
            {
                ["dynPath"] = dynPath,
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

            Log.Info("run_dynamo_python executed a graph.");
            return JsonSerializer.Serialize(new
            {
                ok = true,
                engine = "dynamo",
                result = result?.ToString() ?? "Succeeded",
                note = "Dynamo ran the graph. If nothing changed, check the snippet or Dynamo's own log."
            });
        }
        catch (Exception ex)
        {
            // Never rethrow — a crash here is exactly what we're avoiding.
            Log.Error("run_dynamo_python failed", ex);
            return Fail("Dynamo execution failed: " + (ex.InnerException?.Message ?? ex.Message));
        }
        finally
        {
            try { if (dynPath != null && File.Exists(dynPath)) File.Delete(dynPath); } catch { }
        }
    }

    private static string Fail(string message) =>
        JsonSerializer.Serialize(new { ok = false, engine = "dynamo", error = message });

    // Minimal Dynamo graph (.dyn JSON) with a single Python Script node containing the code.
    private static string WriteGraph(string code)
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
                    Engine = "CPython3",
                    Inputs = Array.Empty<object>(),
                    Outputs = new object[]
                    {
                        new { Id = "22222222-2222-2222-2222-222222222222", Name = "OUT", NodeType = "OutputNode" }
                    }
                }
            },
            Connectors = Array.Empty<object>(),
            View = new { Dynamo = new { Version = "3.0.0.0" } }
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
