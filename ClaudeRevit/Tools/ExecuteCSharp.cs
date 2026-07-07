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
// dispatcher's transaction. Gated by the code-execution opt-in; per-run confirmation only
// if the user enabled it in settings.
public class ExecuteCSharp : IRevitTool
{
    // Single source for the namespaces available to snippets — feeds both the compiled
    // prelude and the tool description so they cannot drift.
    private static readonly string[] Namespaces =
    [
        "System", "System.Linq", "System.Collections.Generic",
        "Autodesk.Revit.DB", "Autodesk.Revit.DB.Structure",
        "Autodesk.Revit.DB.Architecture", "Autodesk.Revit.UI"
    ];

    public string Name => "execute_csharp";

    public string Description =>
        "The DEFAULT code escape hatch: runs a C# snippet directly against the full Revit API — " +
        "no Dynamo required, fastest and most reliable. Use for operations no dedicated tool " +
        "covers. These variables are ALREADY defined — use them directly, do NOT re-declare " +
        "them and do NOT use uidoc.Document/__revit__ to fetch them: 'uiapp' (UIApplication), " +
        "'uidoc' (UIDocument) and 'doc' (active Document). Write STATEMENTS (the snippet becomes a method body — no " +
        "top-level classes, no bare trailing expressions) and end with 'return <expr>;' to " +
        "report a result (it is ToString()'d); 'return doc.Title;' is a good smoke test. " +
        "The snippet already runs inside a transaction that rolls back if it throws — do NOT " +
        "open your own Transaction (SubTransactions are fine). Imported namespaces: " +
        string.Join(", ", Namespaces) + ". " +
        "Runs require the user's code-execution opt-in (plus per-run confirmation if the " +
        "user enabled it in settings).";

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
    public bool IsScriptTool => true;

    // Emitted assembly bytes keyed by full source — see Execute.
    private static readonly Dictionary<string, byte[]> CompileCache = new();

    // The method body starts right after this prelude; used to map compiler diagnostics
    // back to the user's snippet line numbers.
    private static readonly string Prelude =
        string.Concat(Namespaces.Select(n => $"using {n};\n")) +
        "#pragma warning disable CS0162\n" +
        "public static class __ClaudeScript\n" +
        "{\n" +
        "    public static object Run(UIApplication uiapp, UIDocument uidoc, Document doc)\n" +
        "    {\n";

    // Models habitually start a snippet by re-bootstrapping the context
    // (`var doc = uidoc.Document;`, `var doc = __revit__.ActiveUIDocument.Document;`), which
    // collides with the doc/uidoc/uiapp we already provide and fails to compile. Blank those
    // lines out (keeping the line count so error line numbers stay accurate) so the snippet
    // just uses the supplied variables.
    private static readonly System.Text.RegularExpressions.Regex BootstrapLine = new(
        @"(?m)^[ \t]*(?:var|Document|UIDocument|UIApplication|Autodesk\.Revit\.[\w.]+)\s+" +
        @"(?:doc|uidoc|uiapp)\s*=[^;\n]*;[ \t]*$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var code = input["code"].GetString();
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("code is empty.");

        var uidoc = app.ActiveUIDocument
            ?? throw new InvalidOperationException("No document is open.");
        var doc = uidoc.Document;

        code = BootstrapLine.Replace(code, "");
        var source = Prelude + code + "\nreturn null;\n    }\n}\n";
        var preludeLines = Prelude.Count(c => c == '\n');

        // Repeated snippets (retry loops, replay from the script journal) skip Roslyn.
        if (!CompileCache.TryGetValue(source, out var assemblyBytes))
        {
            var compilation = CSharpCompilation.Create(
                "ClaudeScript_" + Guid.NewGuid().ToString("N"),
                [CSharpSyntaxTree.ParseText(source)],
                ScriptCompiler.RuntimeReferences(),
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

            assemblyBytes = ms.ToArray();
            if (CompileCache.Count >= 20) CompileCache.Clear(); // tiny bound is plenty
            CompileCache[source] = assemblyBytes;
        }

        var alc = new AssemblyLoadContext("ClaudeScript", isCollectible: true);
        try
        {
            using var loadStream = new MemoryStream(assemblyBytes);
            var asm = alc.LoadFromStream(loadStream);
            var run = asm.GetType("__ClaudeScript")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

            object? result;
            try
            {
                result = run.Invoke(null, [app, uidoc, doc]);
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                throw new InvalidOperationException(
                    $"The snippet threw {inner.GetType().Name}: {inner.Message}");
            }

            // ToString() before the load context is unloaded so no live references remain.
            // Relaxed encoder: script output is often Cyrillic-heavy — keep it readable and
            // cheap in tokens instead of \uXXXX-escaping every character.
            return Services.Json.Serialize(new
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
}
