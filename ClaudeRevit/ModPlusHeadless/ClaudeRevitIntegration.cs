#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;
using ClaudeRevit.Tools;

namespace ModPlus.Headless;

public static class ModPlusHeadlessRegistration
{
    private static BindingCatalog bundled;
    public static BindingCatalog Bundled => bundled ??= BindingCatalog.FromEmbedded();
    public static IReadOnlyList<string> RegisterBundled(ToolRegistry registry = null) => RegisterAll(Bundled, registry);
    // Call explicitly from the server's startup code. This source never copies
    // files, changes application settings or registers itself on load.
    public static IReadOnlyList<string> RegisterAll(string definitionsFile, ToolRegistry registry = null)
        => RegisterAll(BindingCatalog.FromFile(definitionsFile), registry);
    public static IReadOnlyList<string> RegisterAll(BindingCatalog catalog, ToolRegistry registry = null)
    {
        registry ??= ToolRegistry.Instance;
        var result = new List<string>();
        foreach (JsonElement module in catalog.Modules.Where(m => m.GetProperty("available").GetBoolean() || NativeOperations.Operations(m.GetProperty("id").GetString()).Length > 0))
        {
            var tool = new ModPlusHeadlessModuleTool(module.GetProperty("id").GetString(), catalog);
            registry.Register(tool); result.Add(tool.Name);
        }
        return result;
    }
}

public sealed class ModPlusHeadlessModuleTool : IRevitTool
{
    private readonly string module;
    private readonly BindingCatalog bindings;
    private readonly HeadlessRuntime runtime;
    public ModPlusHeadlessModuleTool(string moduleId, BindingCatalog catalog)
    {
        module = moduleId; bindings = catalog;
        if (!bindings.Module(module).GetProperty("available").GetBoolean() && NativeOperations.Operations(module).Length == 0) throw new ArgumentException("No 2027 module binary or independent native operation.");
        runtime = new HeadlessRuntime(catalog);
    }
    public string Name => "modplus_" + module.ToLowerInvariant();
    public string Description => bindings.Module(module).GetProperty("title").GetString()
        + ". Automatic API operations (action=native) and typed original-core pipelines (action=execute). action=describe lists contracts; validate checks original methods before execution. UI-dependent and unresolved-dispatch paths fail explicitly. Native operations do not require ModPlus assemblies; original-core execution requires the installed module loaded in Revit 2027. The adapter is not a complete port of every GUI workflow.";
    public bool RequiresTransaction => false;
    public bool RequiresNoTurnGroup => true;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresConfirmation => true;
    public bool RequiresCodeExecutionOptIn => true;
    public bool InvalidatesCatalog => true;
    private static JsonElement Field(object value) => JsonSerializer.SerializeToElement(value);
    public InputSchema InputSchema => new InputSchema
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["action"] = Field(new { type = "string", @enum = new[] { "describe", "validate", "execute", "native" }, description = "describe returns the contract; validate checks signatures/call graphs without executing algorithms; execute calls an original-core pipeline; native runs a separately ported operation." }),
            ["method_ids"] = Field(new { type = "array", items = new { type = "string" }, minItems = 1, maxItems = 50, description = "validate: exact IDs returned by describe." }),
            ["query"] = Field(new { type = "string", description = "describe: substring in method/type/signature." }),
            ["offset"] = Field(new { type = "integer", minimum = 0, description = "describe pagination; default 0." }),
            ["limit"] = Field(new { type = "integer", minimum = 1, maximum = 50, description = "describe pagination; default 20." }),
            ["type"] = Field(new { type = "string", description = "describe: exact type ID or name to inspect its constructors and fields." }),
            ["preview"] = Field(new { type = "boolean", description = "execute/native: default true. Preview rolls model changes back; false commits." }),
            ["transaction"] = Field(new { type = "string", @enum = new[] { "auto", "owned", "single" }, description = "execute: auto determines transaction ownership from the method graph. owned uses transactions opened by the original algorithm. single opens a transaction for each call." }),
            ["element_ids"] = Field(new { type = "array", items = new { type = "integer" }, maxItems = 10000, description = "Optional explicit active-document selection, restored after execution." }),
            ["steps"] = Field(new { type = "array", minItems = 1, maxItems = 100,
                items = new { type = "object", properties = new Dictionary<string, object>
                {
                    ["action"] = new { type = "string", @enum = new[] { "new", "set", "call" } },
                    ["type"] = new { type = "string" }, ["constructor"] = new { type = "string" },
                    ["method"] = new { type = "string" }, ["instance"] = new { type = "string" },
                    ["args"] = new { type = "object" }, ["members"] = new { type = "object" },
                    ["store"] = new { type = "string" }, ["emit"] = new { type = "boolean" }
                }, required = new[] { "action" }, additionalProperties = false },
                description = "Typed original-core pipeline. new constructs an allowlisted object, set initializes an owned input object, call invokes a listed method. $ref resolves earlier stored values or app/uidoc/doc/view." }),
            ["operation"] = Field(new { type = "string", description = "native: an operation listed in describe.nativeOperations." }),
            ["args"] = Field(new { type = "object", description = "native arguments." })
        },
        Required = new[] { "action" }
    };
    public void Preflight(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        ValidateKeys(input);
        string action = input.TryGetValue("action", out JsonElement a) ? a.GetString() : null;
        if (action is not "describe" and not "validate" and not "execute" and not "native") throw new ArgumentException("action must be describe, validate, execute, or native.");
        if (action != "describe")
        {
            if (!ClaudeRevit.Services.SettingsStore.AllowCodeExecution) throw new HeadlessBindingException("code_disabled", "Code execution is disabled in ClaudeRevit Settings.");
            var bound = ClaudeRevit.Tools.ToolContext.UiDocument(app)?.Document;
            if (!ClaudeRevit.Services.DocumentSessions.Same(bound, app.ActiveUIDocument?.Document))
                throw new HeadlessBindingException("document_context", "Activate the bound project tab before invoking a headless adapter.");
            ClaudeRevit.Tools.ToolContext.ThrowIfCancelled();
        }
    }
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        Preflight(input, app);
        string action = input["action"].GetString();
        if (action == "describe")
        {
            JsonElement entry = bindings.Module(module);
            if (input.TryGetValue("type", out JsonElement t)) return HeadlessResultLimits.Compact(JsonSerializer.Serialize(bindings.Type(module, t.GetString())));
            string query = input.TryGetValue("query", out JsonElement q) ? q.GetString() ?? "" : "";
            int offset = input.TryGetValue("offset", out JsonElement o) ? o.GetInt32() : 0;
            int limit = input.TryGetValue("limit", out JsonElement l) ? l.GetInt32() : 20;
            if (offset < 0 || limit < 1 || limit > 50) throw new ArgumentException("Invalid pagination bounds.");
            JsonElement[] methods = entry.GetProperty("methods").EnumerateArray()
                .Where(m => m.GetRawText().Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            return HeadlessResultLimits.Compact(JsonSerializer.Serialize(new { module, total = methods.Length, offset,
                originalCoreAvailable = entry.GetProperty("available").GetBoolean(),
                methods = methods.Skip(offset).Take(limit).ToArray(),
                nextOffset = offset + limit < methods.Length ? (int?)(offset + limit) : null,
                nativeOperations = NativeOperations.Operations(module),
                nativeContracts = NativeContracts.ForModule(module).ToArray(),
                note = "signatureSupported covers the argument ABI only. GUI call graphs and dispatch are checked separately at execution." }));
        }
        if (action == "validate")
        {
            if (!input.TryGetValue("method_ids", out JsonElement ids)) throw new ArgumentException("validate requires method_ids.");
            return HeadlessResultLimits.Compact(runtime.ValidateBindings(module, ids.EnumerateArray().Select(id => id.GetString()), app));
        }
        if (action == "native")
        {
            if (!input.TryGetValue("operation", out JsonElement op) || !input.TryGetValue("args", out JsonElement args)) throw new ArgumentException("native requires operation and args.");
            bool preview = !input.TryGetValue("preview", out JsonElement p) || p.GetBoolean();
            return HeadlessResultLimits.Compact(NativeOperations.Run(module, op.GetString(), args, app, preview));
        }
        if (!input.ContainsKey("steps")) throw new ArgumentException("execute requires steps.");
        return HeadlessResultLimits.Compact(runtime.Run(module, JsonSerializer.SerializeToElement(input), app));
    }
    private static void ValidateKeys(IReadOnlyDictionary<string, JsonElement> input)
    {
        var allowed = new HashSet<string>(new[] { "action", "query", "offset", "limit", "type", "preview", "transaction", "element_ids", "steps", "operation", "args", "method_ids", "plan_token" }, StringComparer.Ordinal);
        foreach (string key in input.Keys) if (!allowed.Contains(key)) throw new ArgumentException("Unknown argument: " + key);
    }
}
