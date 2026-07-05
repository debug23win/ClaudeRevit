using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ClaudeRevit.Tools;

// Shared Roslyn plumbing for the two paths that compile C# at runtime: the execute_csharp
// escape hatch (a snippet wrapped in a method) and the dynamic tool loader (a whole source
// file). Kept in one place so the reference set — the single expensive, easy-to-get-wrong
// part — can't drift between them.
internal static class ScriptCompiler
{
    // Reference the real assemblies currently loaded in the process (not the NuGet
    // reference-only DLLs, which can't be used to run code). Cached: reading metadata for
    // hundreds of assemblies on Revit's UI thread per call froze Revit for seconds, and a
    // single unreadable file (shadow-copied then deleted by another add-in) must skip that
    // assembly, not brick compilation. Rebuilt when the assembly count changes.
    private static List<MetadataReference>? _cachedReferences;
    private static int _cachedAssemblyCount;

    public static List<MetadataReference> RuntimeReferences()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        if (_cachedReferences != null && assemblies.Length == _cachedAssemblyCount)
            return _cachedReferences;

        var refs = new List<MetadataReference>();
        foreach (var group in assemblies
                     .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                     .GroupBy(a => a.GetName().Name))
        {
            try { refs.Add(MetadataReference.CreateFromFile(group.First().Location)); }
            catch { /* vanished/unreadable file — skip this assembly */ }
        }

        _cachedReferences = refs;
        _cachedAssemblyCount = assemblies.Length;
        return refs;
    }

    // Compiles a COMPLETE source file to an in-memory assembly. Returns the emitted bytes
    // on success, or a human-readable error listing (with 1-based line numbers) on failure.
    public static (byte[]? bytes, string? error) CompileAssembly(string source, string assemblyName)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            RuntimeReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            var errors = emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(25)
                .Select(d => $"line {d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}");
            return (null, string.Join("\n", errors));
        }
        return (ms.ToArray(), null);
    }
}
