using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// In-process Python — the third escape hatch, and the only one that needs neither Dynamo nor a
// compile step.
//
// Why this exists next to run_dynamo_python: that tool boots a headless Dynamo model just to reach
// a Python engine, which costs seconds and requires Dynamo to be installed at all. pyRevit and
// RevitPythonShell already host an IronPython engine INSIDE the Revit process, so if either is
// installed we can evaluate a snippet directly on this thread — no boot, no graph, no IPC.
//
// Why IronPython and not CPython/Python.NET: IronPython runs on the .NET runtime itself, so it
// shares Revit API objects directly and can be called synchronously on the Revit API thread.
// pyRevit's CPython engines go through Python.NET, which owns a separate interpreter state and a
// GIL — acquiring it from Revit's API thread is exactly the kind of blocking call that deadlocks
// (the same reason execute_csharp avoids CSharpScript's async API).
//
// Runtime caveat that drives the engine search: Revit 2025+ runs on .NET 8, and IronPython 2.7 is
// .NET Framework only — it CANNOT load there. Only IronPython 3.x is usable on modern Revit, so
// the search prefers it and, when it finds nothing but a 2.7 engine, says so plainly instead of
// failing with a confusing BadImageFormatException.
//
// Every failure path returns an error string; this tool must never crash Revit.
public class RunPython : IRevitTool
{
    public string Name => "run_python";

    public string Description =>
        "Runs a Python snippet IN-PROCESS through pyRevit's or RevitPythonShell's IronPython engine — " +
        "no Dynamo boot, so it starts instantly. Requires pyRevit or RevitPythonShell to be installed.\n" +
        "Pre-set variables (RevitPythonShell/pyRevit conventions): `doc` (active Document), `uidoc`, " +
        "`app`, `uiapp`, `__revit__` (UIApplication). `clr` is imported and the Revit API assemblies are " +
        "referenced, so `from Autodesk.Revit.DB import *` works.\n" +
        "The tool already opened a transaction, so plain model edits just work; `print` output is captured " +
        "and returned. Assign your result to `OUT` to get it back in the result.\n" +
        "Revit 2025+ runs on .NET 8, where only IronPython 3.x can load (IronPython 2.7 is .NET Framework " +
        "only). Revit 2027 API note: use ElementId.Value (long) — IntegerValue was removed.\n" +
        "Prefer execute_csharp for new code (no third-party dependency); use this when the user wants " +
        "Python, or for a proven pyRevit/RPS snippet. Requires the code-execution opt-in.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["code"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Python source to execute. Assign the result to OUT to return it."
            }),
            ["engine_path"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Optional explicit folder containing IronPython.dll, if auto-detection fails."
            })
        },
        Required = ["code"]
    };

    public bool RequiresTransaction => true;
    public bool RequiresCodeExecutionOptIn => true;
    public bool IsScriptTool => true;   // journaled with its model delta, like the other code tools

    // The hosting runtime is expensive to build and safe to keep: reuse it across calls so a
    // sequence of snippets doesn't pay engine construction every time.
    private static object? _engine;
    private static Assembly? _ironPython;
    private static Assembly? _scripting;
    private static string? _engineSource;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var code = input["code"].GetString();
        if (string.IsNullOrWhiteSpace(code))
            return JsonSerializer.Serialize(new { error = "code is empty." });

        var explicitPath = input.TryGetValue("engine_path", out var ep) ? ep.GetString() : null;

        string? setupError;
        var engine = GetOrCreateEngine(explicitPath, out setupError);
        if (engine == null)
            return JsonSerializer.Serialize(new
            {
                error = setupError ?? "No in-process Python engine found.",
                hint = "Install pyRevit (or RevitPythonShell), or pass engine_path pointing at a folder " +
                       "with IronPython.dll. Alternatives that need no extra install: execute_csharp, " +
                       "or run_dynamo_python if Dynamo is present."
            });

        var doc = app.ActiveUIDocument?.Document;

        try
        {
            var scope = engine.GetType()
                .GetMethod("CreateScope", System.Type.EmptyTypes)?.Invoke(engine, null);
            if (scope == null)
                return JsonSerializer.Serialize(new { error = "Could not create a Python scope." });

            SetVariable(scope, "uiapp", app);
            SetVariable(scope, "__revit__", app);          // RevitPythonShell convention
            SetVariable(scope, "uidoc", app.ActiveUIDocument);
            SetVariable(scope, "doc", doc);
            SetVariable(scope, "app", app.Application);
            SetVariable(scope, "OUT", null);

            var stdout = CaptureOutput(engine);

            // Statements, not Expression: the snippet is a script, not a single expression.
            var kind = _scripting?.GetType("Microsoft.Scripting.SourceCodeKind");
            var statements = kind != null ? Enum.Parse(kind, "Statements") : null;

            var createSource = engine.GetType().GetMethod("CreateScriptSourceFromString",
                new[] { typeof(string), kind ?? typeof(string) });
            var source = createSource?.Invoke(engine, new[] { (object)code!, statements! });
            if (source == null)
                return JsonSerializer.Serialize(new { error = "Could not create a Python script source." });

            object? outValue = null;
            string? traceback = null;

            try
            {
                var exec = source.GetType().GetMethod("Execute", new[] { scope.GetType() })
                           ?? source.GetType().GetMethods().FirstOrDefault(m =>
                                  m.Name == "Execute" && !m.IsGenericMethod && m.GetParameters().Length == 1);
                exec?.Invoke(source, new[] { scope });

                outValue = GetVariable(scope, "OUT");
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                traceback = FormatPythonException(engine, tie.InnerException);
            }
            catch (Exception ex)
            {
                traceback = FormatPythonException(engine, ex);
            }

            var printed = ReadOutput(stdout);

            return JsonSerializer.Serialize(new
            {
                engine = _engineSource,
                succeeded = traceback == null,
                output = Describe(outValue),
                printed = string.IsNullOrEmpty(printed) ? null : Trim(printed, 8000),
                traceback = traceback == null ? null : Trim(traceback, 4000),
                document = doc?.Title,
                note = traceback == null
                    ? "Ran in-process (no Dynamo). Changes are in this turn's transaction — one Ctrl+Z."
                    : "The script raised. Model changes made before the error may have been applied — " +
                      "the traceback is reported verbatim rather than re-running the snippet."
            });
        }
        catch (Exception ex)
        {
            // Never let a hosting-layer failure escape into Revit.
            return JsonSerializer.Serialize(new { error = "Python host error: " + ex.Message });
        }
    }

    // ---- engine discovery -------------------------------------------------------------------

    private static object? GetOrCreateEngine(string? explicitPath, out string? error)
    {
        error = null;
        if (_engine != null) return _engine;

        if (!TryLoadIronPython(explicitPath, out error)) return null;

        try
        {
            // IronPython.Hosting.Python.CreateEngine()
            var pythonType = _ironPython!.GetType("IronPython.Hosting.Python");
            var create = pythonType?.GetMethod("CreateEngine", System.Type.EmptyTypes);
            var engine = create?.Invoke(null, null);
            if (engine == null) { error = "IronPython.Hosting.Python.CreateEngine() returned nothing."; return null; }

            // Make the Revit API importable from Python.
            LoadAssemblyIntoRuntime(engine, typeof(Document).Assembly);        // RevitAPI
            LoadAssemblyIntoRuntime(engine, typeof(UIApplication).Assembly);   // RevitAPIUI
            LoadAssemblyIntoRuntime(engine, typeof(object).Assembly);          // mscorlib/System.Private.CoreLib
            try { LoadAssemblyIntoRuntime(engine, Assembly.Load("System.Runtime")); } catch { }

            _engine = engine;
            return _engine;
        }
        catch (Exception ex)
        {
            error = "Could not start the IronPython engine: " + ex.Message;
            return null;
        }
    }

    private static bool TryLoadIronPython(string? explicitPath, out string? error)
    {
        error = null;

        // 1) Already loaded — pyRevit or RevitPythonShell is running in this process.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (string.Equals(name, "IronPython", StringComparison.OrdinalIgnoreCase)) _ironPython = asm;
            else if (string.Equals(name, "Microsoft.Scripting", StringComparison.OrdinalIgnoreCase)) _scripting = asm;
        }
        if (_ironPython != null && _scripting != null)
        {
            _engineSource = $"in-process IronPython {_ironPython.GetName().Version} (already loaded by pyRevit/RevitPythonShell)";
            return true;
        }

        // 2) Load from disk. Only .NET-Core-capable IronPython 3.x can load on Revit 2025+ (.NET 8),
        //    so prefer those folders and treat a 2.7-only install as an explicit, explained failure.
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath!);
        candidates.AddRange(EngineFolders());

        string? sawLegacyOnly = null;
        foreach (var dir in candidates)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var dll = Path.Combine(dir, "IronPython.dll");
                if (!File.Exists(dll)) continue;

                var ver = System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).FileMajorPart;
                if (ver == 2 && Environment.Version.Major >= 5)
                {
                    sawLegacyOnly = dir;   // .NET Framework build; unusable on .NET 8
                    continue;
                }

                var ip = Assembly.LoadFrom(dll);
                var scriptingDll = Path.Combine(dir, "Microsoft.Scripting.dll");
                var sc = File.Exists(scriptingDll) ? Assembly.LoadFrom(scriptingDll) : null;
                if (sc == null) continue;

                _ironPython = ip;
                _scripting = sc;
                _engineSource = $"IronPython {ip.GetName().Version} loaded from {dir}";
                return true;
            }
            catch { /* try the next candidate */ }
        }

        error = sawLegacyOnly != null
            ? $"Only IronPython 2.7 was found ({sawLegacyOnly}). It is a .NET Framework build and cannot " +
              $"load in Revit's .NET {Environment.Version.Major} process. Install a pyRevit release with an " +
              "IronPython 3 engine, or use execute_csharp / run_dynamo_python instead."
            : "Neither pyRevit nor RevitPythonShell was found in this Revit process, and no IronPython.dll " +
              "was located on disk.";
        return false;
    }

    // Where pyRevit and RevitPythonShell keep their engines.
    private static IEnumerable<string> EngineFolders()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        foreach (var root in new[]
                 {
                     Path.Combine(appData, "pyRevit-Master", "bin", "engines"),
                     Path.Combine(appData, "pyRevit", "bin", "engines"),
                     Path.Combine(programData, "pyRevit-Master", "bin", "engines"),
                 })
        {
            if (!Directory.Exists(root)) continue;
            // Engine folders are named like IPY342, IPY2711 — prefer the highest 3.x.
            List<string> subs;
            try { subs = Directory.GetDirectories(root).ToList(); } catch { continue; }
            foreach (var d in subs.Where(d => Path.GetFileName(d).StartsWith("IPY3", StringComparison.OrdinalIgnoreCase))
                                  .OrderByDescending(d => d))
                yield return d;
            foreach (var d in subs.Where(d => !Path.GetFileName(d).StartsWith("IPY3", StringComparison.OrdinalIgnoreCase)))
                yield return d;
        }

        foreach (var rps in new[]
                 {
                     Path.Combine(appData, "RevitPythonShell"),
                     Path.Combine(programData, "RevitPythonShell"),
                 })
            if (Directory.Exists(rps)) yield return rps;
    }

    // ---- hosting helpers (all reflection — IronPython is not referenced at build time) --------

    private static void LoadAssemblyIntoRuntime(object engine, Assembly asm)
    {
        try
        {
            var runtime = engine.GetType().GetProperty("Runtime")?.GetValue(engine);
            runtime?.GetType().GetMethod("LoadAssembly", new[] { typeof(Assembly) })?
                   .Invoke(runtime, new object[] { asm });
        }
        catch { /* a missing reference only limits what the snippet can import */ }
    }

    private static void SetVariable(object scope, string name, object? value)
    {
        try
        {
            scope.GetType().GetMethod("SetVariable", new[] { typeof(string), typeof(object) })?
                 .Invoke(scope, new[] { name, value });
        }
        catch { }
    }

    private static object? GetVariable(object scope, string name)
    {
        try
        {
            var tryGet = scope.GetType().GetMethod("TryGetVariable", new[] { typeof(string), typeof(object).MakeByRefType() });
            if (tryGet != null)
            {
                var args = new object?[] { name, null };
                if (tryGet.Invoke(scope, args) is true) return args[1];
                return null;
            }
        }
        catch { }
        return null;
    }

    // Redirect the engine's stdout so `print` reaches the tool result.
    private static MemoryStream? CaptureOutput(object engine)
    {
        try
        {
            var runtime = engine.GetType().GetProperty("Runtime")?.GetValue(engine);
            var io = runtime?.GetType().GetProperty("IO")?.GetValue(runtime);
            var setOutput = io?.GetType().GetMethod("SetOutput", new[] { typeof(Stream), typeof(Encoding) });
            if (setOutput == null) return null;

            var ms = new MemoryStream();
            setOutput.Invoke(io, new object[] { ms, new UTF8Encoding(false) });
            return ms;
        }
        catch { return null; }
    }

    private static string ReadOutput(MemoryStream? ms)
    {
        if (ms == null) return "";
        try { return new UTF8Encoding(false).GetString(ms.ToArray()); }
        catch { return ""; }
    }

    // A Python traceback is far more useful than the .NET exception text, so ask IronPython to
    // format it and only fall back to the CLR message.
    private static string FormatPythonException(object engine, Exception ex)
    {
        try
        {
            var opsType = _scripting?.GetType("Microsoft.Scripting.Hosting.ExceptionOperations");
            if (opsType != null)
            {
                var getService = engine.GetType().GetMethods()
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethod && m.GetParameters().Length == 0);
                var ops = getService?.MakeGenericMethod(opsType).Invoke(engine, null);
                var format = ops?.GetType().GetMethod("FormatException", new[] { typeof(Exception) });
                if (format?.Invoke(ops, new object[] { ex }) is string s && !string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }
        catch { }
        return ex.GetType().Name + ": " + ex.Message;
    }

    private static object? Describe(object? v)
    {
        if (v == null) return null;
        try
        {
            switch (v)
            {
                case string or bool or int or long or double or float or decimal: return v;
                case Element el: return new { id = el.Id.Value, name = el.Name, category = el.Category?.Name };
                case ElementId id: return id.Value;
                case System.Collections.IEnumerable seq and not string:
                {
                    var items = new List<object?>();
                    foreach (var o in seq)
                    {
                        if (items.Count >= 200) { items.Add("… truncated"); break; }
                        items.Add(Describe(o));
                    }
                    return items;
                }
                default: return v.ToString();
            }
        }
        catch { return v.ToString(); }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "\n… truncated";
}
