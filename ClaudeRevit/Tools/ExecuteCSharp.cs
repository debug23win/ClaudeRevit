using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ClaudeRevit.Tools;

// Escape hatch giving Claude the full Revit API when no dedicated tool exists — with no
// Dynamo involved. The snippet is wrapped in a static method, compiled SYNCHRONOUSLY with
// Roslyn (CSharpCompilation.Emit — deliberately no CSharpScript/async: blocking on the
// scripting API's tasks from Revit's UI thread is what deadlocked Revit before), loaded
// into a collectible AssemblyLoadContext and invoked on the Revit API thread inside the
// dispatcher's transaction. GATED: RequiresConfirmation shows the code for Allow/Deny.
public class ExecuteCSharp : IRevitTool
{
    public string Name => "execute_csharp";

    public string Description =>
        "Runs a C# snippet directly against the full Revit API — no Dynamo required. Use for " +
        "operations no dedicated tool covers. Available variables: 'uiapp' (UIApplication) and " +
        "'doc' (active Document). End with 'return <expr>;' to report a result (it is " +
        "ToString()'d); a plain 'return doc.Title;' is a good smoke test. The snippet already " +
        "runs inside a transaction — do NOT open your own Transaction (SubTransactions are " +
        "fine). Imported namespaces: System, System.Linq, System.Collections.Generic, " +
        "Autodesk.Revit.DB, .DB.Structure, .DB.Architecture, Autodesk.Revit.UI. " +
        "The user must approve each run.";

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
    public bool RequiresCodeExecutionOptIn => true;

    // The method body starts right after this prelude; used to map compiler diagnostics
    // back to the user's snippet line numbers.
    private const string Prelude =
        "using System;\n" +
        "using System.Linq;\n" +
        "using System.Collections.Generic;\n" +
        "using Autodesk.Revit.DB;\n" +
        "using Autodesk.Revit.DB.Structure;\n" +
        "using Autodesk.Revit.DB.Architecture;\n" +
        "using Autodesk.Revit.UI;\n" +
        "#pragma warning disable CS0162\n" +
        "public static class __ClaudeScript\n" +
        "{\n" +
        "    public static object Run(UIApplication uiapp, Document doc)\n" +
        "    {\n";

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var code = input["code"].GetString();
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("code is empty.");

        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var source = Prelude + code + "\nreturn null;\n    }\n}\n";
        var preludeLines = Prelude.Count(c => c == '\n');

        var compilation = CSharpCompilation.Create(
            "ClaudeScript_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)],
            RuntimeReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            // Report errors with line numbers relative to the user's snippet.
            var errors = emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(20)
                .Select(d =>
                {
                    var line = d.Location.GetLineSpan().StartLinePosition.Line - preludeLines + 1;
                    return $"line {line}: {d.GetMessage()}";
                });
            throw new InvalidOperationException("Compilation error:\n" + string.Join("\n", errors));
        }

        ms.Position = 0;
        var alc = new AssemblyLoadContext("ClaudeScript", isCollectible: true);
        try
        {
            var asm = alc.LoadFromStream(ms);
            var run = asm.GetType("__ClaudeScript")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

            object? result;
            try
            {
                result = run.Invoke(null, [app, doc]);
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                throw new InvalidOperationException(
                    $"The snippet threw {inner.GetType().Name}: {inner.Message}");
            }

            // ToString() before the load context is unloaded so no live references remain.
            return JsonSerializer.Serialize(new
            {
                ok = true,
                result = result?.ToString() ?? "(no return value)"
            });
        }
        finally
        {
            try { alc.Unload(); } catch { }
        }
    }

    // Reference the real assemblies currently loaded in the process (not the NuGet
    // reference-only DLLs, which can't be used to run code).
    private static List<MetadataReference> RuntimeReferences() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .GroupBy(a => a.GetName().Name)
            .Select(g => (MetadataReference)MetadataReference.CreateFromFile(g.First().Location))
            .ToList();
}
