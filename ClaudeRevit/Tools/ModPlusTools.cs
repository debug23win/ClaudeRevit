using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

internal sealed record ModPlusButton(string ClassName, string Name, string Title, string Panel, string? CommandId, bool CanPost);
internal static class ModPlusRuntime
{
    private static readonly JsonSerializerOptions Options = new(Json.Options) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
    public static List<ModPlusButton> Buttons(UIApplication app)
    {
        var result = new List<ModPlusButton>();
        List<RibbonPanel> panels;
        try { panels = app.GetRibbonPanels("ModPlus"); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { return result; }
        void Add(RibbonItem item, RibbonPanel panel)
        {
            if (item is PushButton b)
            {
                var id = ModPlusCatalog.CommandId(panel.Name, b.Name);
                RevitCommandId? command = null;
                try { command = RevitCommandId.LookupCommandId(id); } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                result.Add(new(b.ClassName, b.Name, b.ItemText, panel.Name, command == null ? null : id, command != null && app.CanPostCommand(command)));
            }
            else if (item is PulldownButton pull) foreach (var child in pull.GetItems()) Add(child, panel);
            else if (item is SplitButton split) foreach (var child in split.GetItems()) Add(child, panel);
        }
        foreach (var panel in panels) foreach (var item in panel.GetItems()) Add(item, panel);
        return result;
    }

    public static UIDocument ActiveDocument(UIApplication app)
    {
        if (app.Application.VersionNumber != "2027") throw new ToolInputException("ModPlus execution in this integration requires Revit 2027; the catalog can be inspected on other supported versions.");
        var bound = ToolContext.UiDocument(app) ?? throw new ToolInputException("Open a model first.");
        if (!DocumentSessions.Same(bound.Document, app.ActiveUIDocument?.Document))
            throw new ToolInputException("Activate the bound project tab before calling ModPlus. Nothing was executed.");
        return bound;
    }

    public static int Int(IReadOnlyDictionary<string, JsonElement> input, string key, int fallback, int min, int max)
    {
        if (!input.TryGetValue(key, out var v) || v.ValueKind == JsonValueKind.Null) return fallback;
        var value = ToolInput.RequiredInt(input, key);
        if (value < min || value > max) throw new ToolInputException($"{key} must be in {min}..{max}.");
        return value;
    }
    public static void Only(IReadOnlyDictionary<string, JsonElement> input, params string[] fields)
    {
        foreach (var name in input.Keys) if (!fields.Contains(name, StringComparer.Ordinal)) throw new ToolInputException("Unknown argument: " + name);
    }
    public static Assembly Loaded(string name) => AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == name)
        ?? throw new ToolInputException($"ModPlus has not loaded {name}. Enable the module in ModPlus and restart Revit.");
}

public sealed class GetModPlusTools : IRevitTool
{
    public string Name => "get_modplus_tools";
    public string Description => "Search the ModPlus Revit 2027 snapshot (84 available modules / 215 declared commands), including unavailable versions, service candidates and live ribbon availability. Only sum parameters has a fixed read-only service adapter; other commands can be queued interactively by run_modplus_command. Metadata coverage is not a headless port or runtime acceptance. Does not execute source or read credentials.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["query"] = NativeToolUtil.Field("string", "Substring in module ID, Russian title, description or group."),
        ["module"] = NativeToolUtil.Field("string", "Optional exact module ID; useful for paging its commands."),
        ["offset"] = NativeToolUtil.Field("integer", "Default 0."),
        ["limit"] = NativeToolUtil.Field("integer", "Default 10, 1..10; pages keep the model context small."),
        ["command_offset"] = NativeToolUtil.Field("integer", "Default 0; applies to command and installed-button lists."),
        ["command_limit"] = NativeToolUtil.Field("integer", "Default 10, 1..10.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        ModPlusRuntime.Only(input, "query", "module", "offset", "limit", "command_offset", "command_limit");
        var source = ModPlusCatalog.Source;
        var query = NativeToolUtil.Text(input, "query");
        var offset = ModPlusRuntime.Int(input, "offset", 0, 0, 100000);
        var limit = ModPlusRuntime.Int(input, "limit", 10, 1, 10);
        var matches = ModPlusCatalog.Search(source, query);
        var exact = NativeToolUtil.Text(input, "module");
        if (exact.Length > 0)
        {
            try { matches = [ModPlusCatalog.Module(source, exact)]; }
            catch (ArgumentException ex) { throw new ToolInputException(ex.Message); }
        }
        var commandOffset = ModPlusRuntime.Int(input, "command_offset", 0, 0, 100000);
        var commandLimit = ModPlusRuntime.Int(input, "command_limit", 10, 1, 10);
        var live = app.Application.VersionNumber == "2027" ? ModPlusRuntime.Buttons(app) : [];
        return ModPlusRuntime.Serialize(new
        {
            targetRevit = 2027, currentRevit = app.Application.VersionNumber, capturedAt = source.GetProperty("capturedAt"),
            summary = source.GetProperty("summary"), total = matches.Length, offset, installedButtons = live.Count,
            modules = matches.Skip(offset).Take(limit).Select(m => new
            {
                id = m.GetProperty("module"), title = m.GetProperty("title"), description = m.GetProperty("description"),
                available2027 = m.GetProperty("available2027"), adapterStatus = m.GetProperty("adapterStatus"),
                serviceAdapter = m.GetProperty("module").GetString() == "mprSumParameters" ? "modplus_sum_parameters" : null,
#if REVIT2027
                headlessTool = m.GetProperty("module").GetString() == "mprSumParameters" ? null : "modplus_" + m.GetProperty("module").GetString()!.ToLowerInvariant(),
                headlessRouter = m.GetProperty("module").GetString() == "mprSumParameters" ? null : "run_modplus_headless",
#endif
                externalCommandCount = m.GetProperty("externalCommands").GetArrayLength(),
                externalCommands = m.GetProperty("externalCommands").EnumerateArray().Skip(commandOffset).Take(commandLimit).Select(c => c.GetProperty("class").GetString()),
                nextCommandOffset = commandOffset + commandLimit < m.GetProperty("externalCommands").GetArrayLength() ? (int?)(commandOffset + commandLimit) : null,
                algorithmClassCount = m.GetProperty("algorithmClasses").GetArrayLength(),
                installedButtonCount = live.Count(b => m.GetProperty("externalCommands").EnumerateArray().Any(c => c.GetProperty("class").GetString() == b.ClassName)),
                installed = live.Where(b => m.GetProperty("externalCommands").EnumerateArray().Any(c => c.GetProperty("class").GetString() == b.ClassName)).Skip(commandOffset).Take(commandLimit)
            }),
            nextOffset = offset + limit < matches.Length ? (int?)(offset + limit) : null,
            note = "Use get_modplus_algorithm for paged command/service signatures. CanPost queues an interactive command; it does not prove completion or unattended execution."
        });
    }
}

public sealed class GetModPlusAlgorithm : IRevitTool
{
    public string Name => "get_modplus_algorithm";
    public string Description => "Inspect one ModPlus module's mapped service/command classes, method signatures, dependencies and bounded source excerpt from the local analysis bundle. Metadata is bundled; source stays local under ClaudeRevit/tools/modplus-revit-2027. Does not compile or execute source. Without the local bundle, returns metadata and source_available=false.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["module"] = NativeToolUtil.Field("string", "Exact module ID from get_modplus_tools."),
        ["class_query"] = NativeToolUtil.Field("string", "Filter service/command class names."),
        ["method_query"] = NativeToolUtil.Field("string", "Filter method names; 10 signatures per class."),
        ["method_offset"] = NativeToolUtil.Field("integer", "Default 0; pages matching methods within each returned class."),
        ["class_offset"] = NativeToolUtil.Field("integer", "Default 0."),
        ["class_limit"] = NativeToolUtil.Field("integer", "Default 3, 1..3."),
        ["start_line"] = NativeToolUtil.Field("integer", "One-based source start, default 1."),
        ["line_count"] = NativeToolUtil.Field("integer", "Default 60, 1..300; excerpt capped at 12000 characters.")
    }, "module");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        ModPlusRuntime.Only(input, "module", "class_query", "method_query", "method_offset", "class_offset", "class_limit", "start_line", "line_count");
        var id = ToolInput.RequiredString(input, "module");
        JsonElement module;
        try { module = ModPlusCatalog.Module(ModPlusCatalog.Source, id); }
        catch (ArgumentException ex) { throw new ToolInputException(ex.Message); }
        if (!module.GetProperty("available2027").GetBoolean()) return ModPlusRuntime.Serialize(new { module = id, available2027 = false, installedVersions = module.GetProperty("installedVersions") });
        var start = ModPlusRuntime.Int(input, "start_line", 1, 1, 10000000);
        var count = ModPlusRuntime.Int(input, "line_count", 60, 1, 300);
        var offset = ModPlusRuntime.Int(input, "class_offset", 0, 0, 100000);
        var limit = ModPlusRuntime.Int(input, "class_limit", 3, 1, 3);
        var cq = NativeToolUtil.Text(input, "class_query"); var mq = NativeToolUtil.Text(input, "method_query");
        var methodOffset = ModPlusRuntime.Int(input, "method_offset", 0, 0, 100000);
        var classes = module.GetProperty("algorithmClasses").EnumerateArray().Where(c => (c.GetProperty("class").GetString() ?? "").Contains(cq, StringComparison.OrdinalIgnoreCase)).ToArray();
        var file = ModPlusCatalog.SourcePath(ModPlusCatalog.DataDirectory, module.GetProperty("source").GetString() ?? "");
        string? excerpt = null;
        if (File.Exists(file)) try { excerpt = ModPlusCatalog.Excerpt(file, start, count); } catch (ArgumentException ex) { throw new ToolInputException(ex.Message); }
        return ModPlusRuntime.Serialize(new
        {
            module = id, title = module.GetProperty("title"), adapterStatus = module.GetProperty("adapterStatus"),
            totalClasses = classes.Length, classOffset = offset, nextClassOffset = offset + limit < classes.Length ? (int?)(offset + limit) : null,
            algorithmClasses = classes.Skip(offset).Take(limit).Select(c => new
            {
                className = c.GetProperty("class"), kind = c.GetProperty("kind"),
                matchingMethods = c.GetProperty("methods").EnumerateArray().Count(m => (m.GetProperty("name").GetString() ?? "").Contains(mq, StringComparison.OrdinalIgnoreCase)),
                methodOffset,
                methods = c.GetProperty("methods").EnumerateArray().Where(m => (m.GetProperty("name").GetString() ?? "").Contains(mq, StringComparison.OrdinalIgnoreCase)).Skip(methodOffset).Take(10)
            }),
            dependencies = module.GetProperty("dependencyMarkers"), warnings = module.GetProperty("decompilerWarnings"),
            source_available = excerpt != null, sourceLineCount = module.GetProperty("sourceLines"), startLine = start,
            nextLine = start - 1 + count < module.GetProperty("sourceLines").GetInt32() ? (int?)(start + count) : null, source = excerpt,
            note = "Static service candidates require semantic review; signatures and source are analysis data, not runnable tools."
        });
    }
}

public sealed class ModPlusSumParameters : IRevitTool
{
    public string Name => "modplus_sum_parameters";
    public string Description => "Read totals through the installed free ModPlus mprSumParameters public service in Revit 2027. Exact module SHA-256 and public contract are verified before invoking its fixed GetSelectElements method. Explicit IDs or current selection; selection is restored on success, failure or cancellation. Preserves grouping, native units and type parameters counted per selected instance. No model edits and no arbitrary method/source execution.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["element_ids"] = NativeToolUtil.Array("integer", "Optional 1..5000 IDs in the active bound document; otherwise current selection."),
        ["precision"] = NativeToolUtil.Field("integer", "0..15, default 3; midpoint-to-even rounding."),
        ["coefficient"] = NativeToolUtil.Field("number", "Finite multiplier, default 1."),
        ["parameter_query"] = NativeToolUtil.Field("string", "Filter parameter display names."),
        ["offset"] = NativeToolUtil.Field("integer", "Default 0."),
        ["limit"] = NativeToolUtil.Field("integer", "Default 25, 1..25; long displayed text is bounded.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        ModPlusRuntime.Only(input, "element_ids", "precision", "coefficient", "parameter_query", "offset", "limit");
        var uidoc = ModPlusRuntime.ActiveDocument(app);
        var precision = ModPlusRuntime.Int(input, "precision", 3, 0, 15);
        var offset = ModPlusRuntime.Int(input, "offset", 0, 0, 100000);
        var limit = ModPlusRuntime.Int(input, "limit", 25, 1, 25);
        var coefficient = input.TryGetValue("coefficient", out var factor) && factor.ValueKind != JsonValueKind.Null ? ToolInput.RequiredDouble(input, "coefficient") : 1;
        if (!double.IsFinite(coefficient)) throw new ToolInputException("coefficient must be finite.");
        var saved = uidoc.Selection.GetElementIds().ToList();
        var explicitIds = input.TryGetValue("element_ids", out var requested) && requested.ValueKind != JsonValueKind.Null;
        var ids = explicitIds ? NativeToolUtil.Ids(ToolInput.RequiredArray(input, "element_ids"), 5000) : saved;
        if (ids.Count is < 1 or > 5000) throw new ToolInputException("Select or supply 1..5000 elements.");
        foreach (var id in ids) NativeToolUtil.Element(uidoc.Document, id.Value);
        var module = ModPlusCatalog.Module(ModPlusCatalog.Source, "mprSumParameters");
        var assembly = ModPlusRuntime.Loaded("mprSumParameters_2027");
        if (string.IsNullOrEmpty(assembly.Location)) throw new ToolInputException("Cannot verify the ModPlus module location.");
        using (var stream = File.OpenRead(assembly.Location))
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(module.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new ToolInputException("ModPlus module changed since the catalog snapshot. Refresh the local catalog before using this adapter.");
        var host = ModPlusRuntime.Loaded("ModPlus_Revit_2027");
        var hostType = host.GetType("ModPlus_Revit.ModPlus", true)!;
        var context = hostType.GetProperty("UiApplication", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as UIApplication;
        if (!DocumentSessions.Same(context?.ActiveUIDocument?.Document, uidoc.Document))
            throw new ToolInputException("ModPlus current document differs from the bound project. Nothing was executed.");
        var type = assembly.GetType("mprSumParameters.Services", true)!;
        var method = type.GetMethod("GetSelectElements", BindingFlags.Public | BindingFlags.Instance, null, System.Type.EmptyTypes, null)
            ?? throw new ToolInputException("ModPlus public service contract changed.");
        if (!typeof(IEnumerable).IsAssignableFrom(method.ReturnType)) throw new ToolInputException("ModPlus public service return contract changed.");
        ModPlusTotals.Page page;
        try
        {
            ToolContext.ThrowIfCancelled();
            if (explicitIds) uidoc.Selection.SetElementIds(ids);
            var service = Activator.CreateInstance(type)!;
            var items = method.Invoke(service, null) as IEnumerable ?? throw new InvalidOperationException("ModPlus returned no parameter rows.");
            page = ModPlusTotals.Read(items, NativeToolUtil.Text(input, "parameter_query"), offset, limit, precision, coefficient, ToolContext.ThrowIfCancelled);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null) { throw new InvalidOperationException("ModPlus service failed: " + ex.InnerException.Message, ex.InnerException); }
        finally { if (explicitIds) uidoc.Selection.SetElementIds(saved); }
        return ModPlusRuntime.Serialize(new { module = "mprSumParameters", moduleVersion = assembly.GetName().Version?.ToString(), document = uidoc.Document.Title,
            elementCount = ids.Count, precision, coefficient, total = page.Total, offset, rows = page.Rows, nextOffset = page.NextOffset,
            semantics = "Original public service; includes type parameters per selected instance, grouped by display name and storage type.", modelModified = false, selectionRestored = true });
    }
}

public sealed class RunModPlusCommand : IRevitTool
{
    public string Name => "run_modplus_command";
    public string Description => "Queue an installed ModPlus Revit 2027 ribbon command. preview=true (default) checks only; false posts the command through Revit's UI. It may need a licensed module, a dialog or user picks. Returns posted, NEVER completed. Wait for the user to finish, then verify the model. Forbidden in unattended benchmarks; no credential, license or DLL bypass.";
    public bool RequiresTransaction => false;
    public bool RequiresConfirmation => true;
    public bool RequiresNoTurnGroup => true;
    public bool MutatesWithoutTransaction => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["command"] = NativeToolUtil.Field("string", "Exact installed ClassName from get_modplus_tools."),
        ["element_ids"] = NativeToolUtil.Array("integer", "Optional selection for the command, max 5000."),
        ["command_id"] = NativeToolUtil.Field("string", "Optional exact ID for this live ModPlus button."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true. False queues the interactive command.")
    }, "command");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        ModPlusRuntime.Only(input, "command", "element_ids", "command_id", "preview", "plan_token");
        var uidoc = ModPlusRuntime.ActiveDocument(app);
        var name = ToolInput.RequiredString(input, "command");
        var buttons = ModPlusRuntime.Buttons(app).Where(b => b.ClassName == name).ToArray();
        if (buttons.Length != 1) throw new ToolInputException("An unambiguous installed ModPlus button was not found. Inspect get_modplus_tools.");
        var button = buttons[0];
        var id = NativeToolUtil.Text(input, "command_id", button.CommandId ?? "");
        if (!ModPlusCatalog.MatchesCommandId(id, button.Panel, button.Name)) throw new ToolInputException("The command ID must identify this exact live ModPlus button.");
        var command = RevitCommandId.LookupCommandId(id);
        if (command == null || !app.CanPostCommand(command)) throw new ToolInputException("Revit cannot post this ModPlus command now. Close dialogs or use a native alternative.");
        var ids = input.TryGetValue("element_ids", out var requested) && requested.ValueKind != JsonValueKind.Null ? NativeToolUtil.Ids(ToolInput.RequiredArray(input, "element_ids"), 5000) : null;
        if (ids != null) foreach (var element in ids) NativeToolUtil.Element(uidoc.Document, element.Value);
        var preview = NativeToolUtil.Preview(input);
        if (!preview)
        {
            if (ToolDispatcher.ForceSuppress) throw new ToolInputException("Interactive ModPlus commands cannot run in an unattended benchmark.");
            var saved = uidoc.Selection.GetElementIds().ToList();
            try { ToolContext.ThrowIfCancelled(); if (ids != null) uidoc.Selection.SetElementIds(ids); app.PostCommand(command); }
            catch { if (ids != null) uidoc.Selection.SetElementIds(saved); throw; }
        }
        return ModPlusRuntime.Serialize(new { preview, posted = !preview, completed = false, command = name, command_id = id,
            next = "Wait for the ModPlus dialog and user picks to finish; verify actual model changes afterward." });
    }
}
