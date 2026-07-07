using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// Self-extension: tools the program learns are stored as plain C# source files under
// %AppData%\ClaudeRevit\tools\*.cs and compiled + registered at runtime, so a proven
// execute_csharp pattern can be promoted into a first-class, persistent tool WITHOUT
// recompiling the add-in. Each file defines one (or more) public classes implementing
// IRevitTool — the exact same contract as the built-in tools.
//
// SECURITY: a dynamic tool is arbitrary code with full Revit API access. Loading and
// running it is therefore gated behind the same "Allow code execution" opt-in as
// execute_csharp — nothing here loads unless the user has enabled it. Each loaded tool is
// also wrapped in DynamicToolProxy, which re-asserts that gate.
public static class DynamicToolLoader
{
    public static string ToolsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeRevit", "tools");

    // file path → its collectible load context + the tool names it registered, so a file can
    // be reloaded (on save) or removed (on delete) cleanly.
    private sealed class LoadedFile
    {
        public required AssemblyLoadContext Alc;
        public required List<string> ToolNames;
    }

    private static readonly Dictionary<string, LoadedFile> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DynamicNames = new(StringComparer.Ordinal);

    // When an override file shadows a compiled built-in, we keep the original instance here so
    // deleting the override restores the built-in exactly.
    private static readonly Dictionary<string, IRevitTool> OverriddenBuiltins = new(StringComparer.Ordinal);

    // Every dynamic/override assembly compiles under this fixed name so ClaudeRevit's
    // [InternalsVisibleTo] grants it access to the internal helpers (ToolInput, Services.Json,
    // FamilyEditorUtil…) that the built-in tools use — otherwise an ejected built-in couldn't
    // recompile. ALCs keep the same-named assemblies isolated.
    private const string DynamicAssemblyName = "ClaudeRevitDynamicTools";

    public static bool IsDynamic(string toolName) => DynamicNames.Contains(toolName);
    public static bool IsOverride(string toolName) => OverriddenBuiltins.ContainsKey(toolName);

    public sealed class Report
    {
        public bool Skipped;
        public readonly List<string> Loaded = new();
        public readonly List<string> Errors = new();
    }

    // Called once at startup. No-op (with Skipped=true) unless code execution is enabled.
    public static Report LoadAll()
    {
        var report = new Report();
        if (!SettingsStore.AllowCodeExecution) { report.Skipped = true; return report; }
        try
        {
            if (!Directory.Exists(ToolsDir)) return report;
            foreach (var file in Directory.GetFiles(ToolsDir, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
            {
                try { report.Loaded.AddRange(LoadFile(file)); }
                catch (Exception ex)
                {
                    report.Errors.Add(Path.GetFileName(file) + ": " + ex.Message);
                    Log.Error("Dynamic tool load failed: " + file, ex);
                }
            }
        }
        catch (Exception ex) { Log.Error("DynamicToolLoader.LoadAll failed", ex); }
        return report;
    }

    // Compiles a file, loads it into its own collectible context, and registers every
    // IRevitTool it defines. Replacing a previously-loaded version of the same file first
    // unloads the old context. Throws with a readable message on any failure.
    private static List<string> LoadFile(string file)
    {
        var source = File.ReadAllText(file);
        var (bytes, error) = ScriptCompiler.CompileAssembly(source, DynamicAssemblyName);
        if (bytes == null)
            throw new InvalidOperationException("compile error:\n" + error);

        var alc = new AssemblyLoadContext("ClaudeDynTool_" + Path.GetFileName(file), isCollectible: true);
        Assembly asm;
        try
        {
            using var ms = new MemoryStream(bytes);
            asm = alc.LoadFromStream(ms);
        }
        catch { try { alc.Unload(); } catch { } throw; }

        var toolTypes = asm.GetTypes()
            .Where(t => typeof(IRevitTool).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface
                        && t.GetConstructor(Type.EmptyTypes) != null)
            .ToList();
        if (toolTypes.Count == 0)
        {
            try { alc.Unload(); } catch { }
            throw new InvalidOperationException(
                "no public class implementing ClaudeRevit.Tools.IRevitTool with a parameterless " +
                "constructor was found in the file.");
        }

        // Validate everything BEFORE mutating the registry, so a half-broken file can't leave
        // a partial set of tools registered.
        var instances = new List<IRevitTool>();
        foreach (var t in toolTypes)
        {
            IRevitTool tool;
            try { tool = (IRevitTool)Activator.CreateInstance(t)!; }
            catch (Exception ex)
            {
                try { alc.Unload(); } catch { }
                throw new InvalidOperationException($"constructing {t.Name} threw: {ex.Message}");
            }
            if (string.IsNullOrWhiteSpace(tool.Name))
            {
                try { alc.Unload(); } catch { }
                throw new InvalidOperationException($"{t.Name}.Name is empty.");
            }
            instances.Add(tool);
        }

        // Now commit: drop the old version of this file (if any), then register the new tools.
        UnloadFile(file);
        var names = new List<string>();
        foreach (var tool in instances)
        {
            // If this shadows a compiled built-in, remember the original so deleting the
            // override restores it exactly. (A file that shadows another dynamic tool just
            // replaces it.)
            if (ToolRegistry.Instance.Get(tool.Name) is { } existing
                && existing is not DynamicToolProxy && !OverriddenBuiltins.ContainsKey(tool.Name))
                OverriddenBuiltins[tool.Name] = existing;

            ToolRegistry.Instance.Register(new DynamicToolProxy(tool));
            DynamicNames.Add(tool.Name);
            names.Add(tool.Name);
        }
        Loaded[file] = new LoadedFile { Alc = alc, ToolNames = names };
        Log.Info($"Dynamic tool loaded from {Path.GetFileName(file)}: {string.Join(", ", names)}");
        return names;
    }

    private static void UnloadFile(string file)
    {
        if (!Loaded.TryGetValue(file, out var lf)) return;
        foreach (var n in lf.ToolNames)
        {
            ToolRegistry.Instance.Unregister(n);
            DynamicNames.Remove(n);
            // If this was an override of a built-in, put the compiled built-in back.
            if (OverriddenBuiltins.TryGetValue(n, out var original))
            {
                ToolRegistry.Instance.Register(original);
                OverriddenBuiltins.Remove(n);
            }
        }
        try { lf.Alc.Unload(); } catch { }
        Loaded.Remove(file);
    }

    public sealed class SaveResult
    {
        public required string File;
        public required List<string> ToolNames;
    }

    // Validates and installs a tool file. On compile/registration failure the previous file
    // content is restored (and its tools reloaded) so a bad edit can't break a working tool.
    public static SaveResult SaveAndLoad(string name, string source)
    {
        if (!SettingsStore.AllowCodeExecution)
            throw new InvalidOperationException(
                "Code execution is disabled. Enable 'Allow Claude to run code' in Settings (gear icon) " +
                "before creating custom tools.");
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("source is empty.");

        Directory.CreateDirectory(ToolsDir);
        var file = Path.Combine(ToolsDir, Sanitize(name) + ".cs");
        var backup = File.Exists(file) ? File.ReadAllText(file) : null;

        File.WriteAllText(file, source);
        try
        {
            var names = LoadFile(file);
            return new SaveResult { File = file, ToolNames = names };
        }
        catch
        {
            // Roll the file back to its prior state (or remove a brand-new one) and restore
            // the previously-working tools.
            if (backup != null)
            {
                File.WriteAllText(file, backup);
                try { LoadFile(file); } catch { /* prior version also broken — leave unloaded */ }
            }
            else
            {
                try { File.Delete(file); } catch { }
            }
            throw;
        }
    }

    public static bool Delete(string name)
    {
        // Match by the file we saved it under first, then by any file that registered a tool
        // of this name.
        var byFile = Path.Combine(ToolsDir, Sanitize(name) + ".cs");
        var target = Loaded.Keys.FirstOrDefault(f => string.Equals(f, byFile, StringComparison.OrdinalIgnoreCase))
                     ?? Loaded.FirstOrDefault(kv => kv.Value.ToolNames.Contains(name)).Key;

        if (target == null)
        {
            // Not currently loaded (e.g. code execution was off) — still delete the file.
            if (File.Exists(byFile)) { File.Delete(byFile); return true; }
            return false;
        }

        UnloadFile(target);
        try { if (File.Exists(target)) File.Delete(target); } catch { }
        return true;
    }

    // The custom tools currently loaded, as (tool name, source file). Lets the model see
    // what it has already built so it can refine one instead of starting over.
    public static IReadOnlyList<(string Name, string File)> ListCustom()
    {
        var result = new List<(string, string)>();
        foreach (var kv in Loaded)
            foreach (var n in kv.Value.ToolNames)
                result.Add((n, kv.Key));
        return result;
    }

    // The saved tool files on disk (name without extension), whether or not they are currently
    // loaded. Lets the UI tell "nothing was ever saved" apart from "saved but not loaded because
    // code execution was off at startup".
    public static IReadOnlyList<string> ListSavedFiles()
    {
        if (!Directory.Exists(ToolsDir)) return Array.Empty<string>();
        try
        {
            return Directory.GetFiles(ToolsDir, "*.cs")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    // The source of a tool by name — the on-disk override/custom file if there is one, else
    // the built-in's original source embedded in the add-in. So the model can read ANY tool
    // (built-in included) to study or refine it.
    public static string? GetSource(string name)
    {
        var loadedFile = Loaded.FirstOrDefault(kv => kv.Value.ToolNames.Contains(name)).Key;
        var file = loadedFile ?? Path.Combine(ToolsDir, Sanitize(name) + ".cs");
        if (File.Exists(file)) return File.ReadAllText(file);
        return BuiltinSource(name);
    }

    // The C# source of a compiled built-in tool, read from the embedded resources shipped in
    // the add-in (Tools/*.cs, embedded as toolsrc/<ClassName>.cs). Returns null for a custom
    // tool (its source is on disk) or an unknown name.
    private static string? BuiltinSource(string name)
    {
        var tool = ToolRegistry.Instance.Get(name);
        if (tool == null || tool is DynamicToolProxy) return null;
        var typeName = tool.GetType().Name;
        var asm = typeof(DynamicToolLoader).Assembly;
        var res = asm.GetManifestResourceNames()
            .FirstOrDefault(r => r.EndsWith("/" + typeName + ".cs", StringComparison.Ordinal)
                              || r.EndsWith("." + typeName + ".cs", StringComparison.Ordinal));
        if (res == null) return null;
        using var s = asm.GetManifestResourceStream(res);
        if (s == null) return null;
        using var reader = new StreamReader(s);
        return reader.ReadToEnd();
    }

    // "Ejects" a tool to an editable override file that shadows the compiled version, so it can
    // then be edited on the fly with save_tool (same name) and reverted with delete_tool. For
    // a built-in this copies its embedded source out; for a tool already ejected it is a no-op
    // re-save.
    public static SaveResult Eject(string name)
    {
        var src = GetSource(name)
            ?? throw new InvalidOperationException(
                $"No source found for tool '{name}'. Check the exact tool name.");
        return SaveAndLoad(name, src);
    }

    // Names the source file; sanitised so a tool name can't escape the tools directory.
    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (name ?? "").Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var safe = new string(chars);
        if (safe.Length == 0) safe = "tool";
        return safe.Length > 80 ? safe[..80] : safe;
    }
}
