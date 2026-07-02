using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace ClaudeRevit.Tools;

// Escape hatch giving Claude the full Revit API when no dedicated tool exists.
// Compiles a C# snippet with Roslyn and runs it on the Revit API thread inside a
// transaction. GATED: RequiresConfirmation shows the code for Allow/Deny first.
public class ExecuteCSharp : IRevitTool
{
    // Globals available to the snippet
    public class Ctx
    {
        public UIApplication uiapp = null!;
        public Document doc = null!;
    }

    public string Name => "execute_csharp";

    public string Description =>
        "Runs a C# snippet against the full Revit API when no dedicated tool fits. " +
        "Prefer a dedicated tool whenever one exists — only use this for operations no tool covers. " +
        "Available globals: 'uiapp' (UIApplication), 'doc' (active Document). Return a value (any " +
        "type) or a string to report back; it is ToString()'d. The snippet already runs inside a " +
        "transaction, so do NOT open your own. Namespaces Autodesk.Revit.DB, .Structure, .UI, " +
        "System, System.Linq, System.Collections.Generic are imported. The user must approve each run.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["code"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "C# statements. Use 'return <expr>;' to report a result."
            })
        },
        Required = ["code"]
    };

    public bool RequiresTransaction => true;
    public bool RequiresConfirmation => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var code = input["code"].GetString()
            ?? throw new InvalidOperationException("code is empty.");

        var options = ScriptOptions.Default
            .WithReferences(RuntimeRevitAssemblies())
            .WithImports(
                "System", "System.Linq", "System.Collections.Generic",
                "Autodesk.Revit.DB", "Autodesk.Revit.DB.Structure", "Autodesk.Revit.UI");

        var ctx = new Ctx { uiapp = app, doc = app.ActiveUIDocument?.Document! };

        object? result;
        try
        {
            // Runs on the Revit API thread (external event handler), so blocking here is fine.
            result = CSharpScript.EvaluateAsync<object>(code, options, ctx, typeof(Ctx))
                .GetAwaiter().GetResult();
        }
        catch (CompilationErrorException ce)
        {
            throw new InvalidOperationException(
                "Compilation error:\n" + string.Join("\n", ce.Diagnostics));
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            result = result?.ToString() ?? "(no return value)"
        });
    }

    // Reference the real Revit assemblies currently loaded in the process (not the
    // NuGet reference-only DLLs, which can't be used to run code).
    private static IEnumerable<Assembly> RuntimeRevitAssemblies()
    {
        var wanted = new[]
        {
            "RevitAPI", "RevitAPIUI", "System.Runtime", "System.Linq",
            "System.Collections", "netstandard", "mscorlib", "System.Private.CoreLib"
        };
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Where(a =>
            {
                var n = a.GetName().Name ?? "";
                return wanted.Contains(n) || n.StartsWith("System") || n == "Autodesk.Revit.DB"
                       || n.StartsWith("RevitAPI");
            })
            .GroupBy(a => a.GetName().Name)
            .Select(g => g.First());
    }
}
