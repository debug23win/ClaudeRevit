using System.Reflection;
using System.Runtime.Loader;
using ClaudeRevit.Tools;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ClaudeRevit.Tests
{
    public class ScriptResponsivenessTests
    {
        private static object? Run(string body)
        {
            var tree = ScriptLoopInstrumentation.Apply(CSharpSyntaxTree.ParseText("public static class Probe { public static object Run() { " + body + " } }"));
            var compilation = CSharpCompilation.Create("LoopProbe_" + Guid.NewGuid().ToString("N"), new[] { tree }, ScriptCompiler.RuntimeReferences(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var stream = new MemoryStream(); var emit = compilation.Emit(stream);
            Assert.True(emit.Success, string.Join("\n", emit.Diagnostics)); stream.Position = 0;
            var context = new AssemblyLoadContext("LoopProbe", true);
            try { return context.LoadFromStream(stream).GetType("Probe")!.GetMethod("Run")!.Invoke(null, null); }
            finally { context.Unload(); }
        }
        [Fact]
        public void LoopChecksPreserveBracedSingleStatementAndDeconstructionSemantics()
        {
            var calls = 0; ScriptRuntime.Check = () => calls++;
            try
            {
                Assert.Equal(13, Run("int n=0; for(int i=0;i<2;i++) n++; foreach(var x in new[]{1,2}) {n+=x;} int j=0; while(j++<2) n++; do {n++;} while(n<8); foreach(var (a,b) in new[]{(2,3)}) n+=a+b; return n;"));
                Assert.Equal(8, calls);
            }
            finally { ScriptRuntime.Check = null; }
        }
        [Fact]
        public void AutomaticallyInstrumentedInfiniteLoopHonorsCancellation()
        {
            var calls = 0; using var cancel = new CancellationTokenSource();
            ScriptRuntime.Check = () => { if (++calls == 3) cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); };
            try { Assert.IsType<OperationCanceledException>(Assert.Throws<TargetInvocationException>(() => Run("while(true) { } return null;")).InnerException); Assert.Equal(3, calls); }
            finally { ScriptRuntime.Check = null; }
        }
        [Fact]
        public void InstrumentationRetainsOriginalCompilerErrorLineNumbers()
        {
            var tree = CSharpSyntaxTree.ParseText("public static class Probe { public static object Run() {\nfor(int i=0;i<2;i++) {\n}\nreturn MissingValue;\n} }");
            var rewritten = ScriptLoopInstrumentation.Apply(tree);
            Diagnostic Error(SyntaxTree t) => CSharpCompilation.Create("LineProbe", new[] { t }, ScriptCompiler.RuntimeReferences(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).GetDiagnostics().Single(d => d.Id == "CS0103");
            Assert.Equal(Error(tree).Location.GetLineSpan().StartLinePosition.Line, Error(rewritten).Location.GetLineSpan().StartLinePosition.Line);
        }
    }
}
namespace ClaudeRevit.Tools
{
    // A token check fixture for the emitted assembly; no Revit runtime or API calls.
    public static class ScriptRuntime
    {
        [ThreadStatic] public static Action? Check;
        public static void CheckCancellation() => Check?.Invoke();
    }
}
