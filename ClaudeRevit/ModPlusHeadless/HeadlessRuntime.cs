#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ModPlus.Headless;

public sealed class HeadlessBindingException : Exception
{
    public string Code { get; }
    public HeadlessBindingException(string code, string message) : base(message) { Code = code; }
}

public sealed class BindingCatalog
{
    private readonly JsonElement root;
    public BindingCatalog(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        root = document.RootElement.Clone();
        if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("targetRevit").GetInt32() != 2027)
            throw new ArgumentException("Unsupported binding catalog.");
    }
    public static BindingCatalog FromFile(string path) => new BindingCatalog(File.ReadAllText(path));
    public static BindingCatalog FromEmbedded()
    {
        string local = Environment.GetEnvironmentVariable("CLAUDEREVIT_MODPLUS_BINDINGS");
        if (!string.IsNullOrWhiteSpace(local)) return FromFile(Path.GetFullPath(local));
        using Stream stream = typeof(BindingCatalog).Assembly.GetManifestResourceStream("ClaudeRevit.ModPlusHeadless.Bindings.json")
            ?? throw new InvalidOperationException("Embedded ModPlus headless bindings are missing.");
        using var reader = new StreamReader(stream);
        return new BindingCatalog(reader.ReadToEnd());
    }
    public IEnumerable<JsonElement> Modules => root.GetProperty("modules").EnumerateArray();
    public IEnumerable<JsonElement> Shared => root.TryGetProperty("shared", out JsonElement shared) ? shared.EnumerateArray() : Array.Empty<JsonElement>();
    public JsonElement Module(string id) => Modules.FirstOrDefault(m => m.GetProperty("id").GetString() == id) is JsonElement m && m.ValueKind != JsonValueKind.Undefined
        ? m : throw new HeadlessBindingException("unknown_module", "Unknown module: " + id);
    public JsonElement Method(string module, string id) => Module(module).GetProperty("methods").EnumerateArray()
        .FirstOrDefault(m => m.GetProperty("id").GetString() == id) is JsonElement m && m.ValueKind != JsonValueKind.Undefined
        ? m : throw new HeadlessBindingException("unknown_method", "Unknown method binding: " + id);
    public JsonElement Type(string module, string id)
    {
        JsonElement local = Module(module).GetProperty("types").EnumerateArray().FirstOrDefault(t => t.GetProperty("id").GetString() == id || t.GetProperty("name").GetString() == id);
        if (local.ValueKind != JsonValueKind.Undefined) return local;
        JsonElement[] matches = Shared.SelectMany(s => s.GetProperty("types").EnumerateArray()).Where(t => t.GetProperty("id").GetString() == id || t.GetProperty("name").GetString() == id).ToArray();
        return matches.Length == 1 ? matches[0] : throw new HeadlessBindingException("unknown_type", "Unknown or ambiguous input type: " + id);
    }
    public JsonElement Describe(string module, string query = "")
    {
        JsonElement m = Module(module);
        return JsonSerializer.SerializeToElement(new
        {
            module, available = m.GetProperty("available"), title = m.GetProperty("title"),
            methods = m.GetProperty("methods").EnumerateArray().Where(x => x.GetRawText().Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray(),
            types = m.GetProperty("types"),
            semantics = "Core-method bindings. Signature support does not imply a completed GUI workflow port. Runtime UI preflight can reject calls."
        });
    }
}

internal sealed class CallContext
{
    internal readonly UIApplication App;
    internal readonly UIDocument UiDoc;
    internal Document Doc => UiDoc.Document;
    internal readonly string Module;
    internal readonly BindingCatalog Catalog;
    internal readonly Assembly Assembly;
    internal readonly UiCallGuard Guard;
    internal string TransactionMode = "auto";
    internal readonly Dictionary<string, object> Values = new Dictionary<string, object>(StringComparer.Ordinal);
    internal readonly HashSet<object> Created = new HashSet<object>(ReferenceEqualityComparer.Instance);
    internal CallContext(UIApplication app, BindingCatalog catalog, string module)
    {
        App = app ?? throw new ArgumentNullException(nameof(app));
        if (app.Application.VersionNumber != "2027") throw new HeadlessBindingException("revit_version", "Revit 2027 is required.");
        UiDoc = app.ActiveUIDocument ?? throw new HeadlessBindingException("no_document", "Open a document before running an algorithm.");
        Catalog = catalog; Module = module;
        JsonElement binding = catalog.Module(module);
        if (!binding.GetProperty("available").GetBoolean()) throw new HeadlessBindingException("no_2027_binary", "This module has no Revit 2027 binary.");
        string assemblyName = binding.GetProperty("assembly").GetString();
        Assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == assemblyName)
            ?? throw new HeadlessBindingException("module_not_loaded", "The ModPlus host must load " + assemblyName + " before this adapter can call it.");
        if (Assembly.ManifestModule.ModuleVersionId.ToString() != binding.GetProperty("mvid").GetString())
            throw new HeadlessBindingException("snapshot_changed", "Module MVID differs from the binding snapshot. Regenerate the bindings.");
        if (string.IsNullOrEmpty(Assembly.Location)) throw new HeadlessBindingException("unverifiable_module", "The module has no verifiable location.");
        using (FileStream file = File.OpenRead(Assembly.Location))
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(binding.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new HeadlessBindingException("snapshot_changed", "Module SHA-256 differs from the binding snapshot. Regenerate the bindings.");
        Assembly host = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ModPlus_Revit_2027");
        object modApp = host?.GetType("ModPlus_Revit.ModPlus")?.GetProperty("UiApplication", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (modApp is not UIApplication modplusApp || modplusApp.ActiveUIDocument == null || !modplusApp.ActiveUIDocument.Document.Equals(Doc))
            throw new HeadlessBindingException("host_context", "The ModPlus current document must match the MCP active document.");
        Guard = new UiCallGuard(Assembly);
        Values["app"] = app; Values["uidoc"] = UiDoc; Values["doc"] = Doc; Values["view"] = Doc.ActiveView;
    }
    internal MethodBase Method(JsonElement entry)
    {
        int token = int.Parse(entry.GetProperty("token").GetString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        Assembly assembly = entry.TryGetProperty("assembly", out JsonElement external) ? SharedAssembly(external.GetString()) : Assembly;
        MethodBase method = assembly.ManifestModule.ResolveMethod(token);
        if (method == null || method.DeclaringType.FullName != entry.GetProperty("declaringType").GetString() || method.Name != entry.GetProperty("name").GetString())
            throw new HeadlessBindingException("binding_mismatch", "The method identity no longer matches the catalog.");
        if (method.ContainsGenericParameters || method.GetParameters().Any(p => p.ParameterType.IsByRef || p.ParameterType.IsPointer || typeof(Delegate).IsAssignableFrom(p.ParameterType)))
            throw new HeadlessBindingException("unsupported_signature", "Generic, by-reference, pointer and delegate input signatures require a dedicated adapter.");
        Guard.Check(method);
        return method;
    }
    private Assembly SharedAssembly(string name)
    {
        JsonElement source = Catalog.Shared.FirstOrDefault(s => s.GetProperty("assembly").GetString() == name);
        if (source.ValueKind == JsonValueKind.Undefined) throw new HeadlessBindingException("assembly", "The assembly is not in the binding catalog.");
        Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == name)
            ?? throw new HeadlessBindingException("module_not_loaded", "The shared ModPlus assembly is not loaded: " + name);
        if (assembly.ManifestModule.ModuleVersionId.ToString() != source.GetProperty("mvid").GetString()) throw new HeadlessBindingException("snapshot_changed", "Shared assembly MVID changed: " + name);
        using FileStream file = File.OpenRead(assembly.Location);
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(source.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw new HeadlessBindingException("snapshot_changed", "Shared assembly SHA-256 changed: " + name);
        return assembly;
    }
    internal Type DataType(string id)
    {
        JsonElement type = Catalog.Type(Module, id);
        Assembly assembly = type.TryGetProperty("assembly", out JsonElement external) ? SharedAssembly(external.GetString()) : Assembly;
        Type result = assembly.GetType(type.GetProperty("name").GetString(), true);
        UiCallGuard.CheckType(result);
        if (result.ContainsGenericParameters || result.IsAbstract || result.IsInterface)
            throw new HeadlessBindingException("unsupported_input_type", "Choose a concrete non-generic input type.");
        return result;
    }
    internal object Invoke(MethodBase method, object receiver, object[] arguments, bool materialize = false)
    {
        bool owns = Guard.Check(method);
        if (method is MethodInfo virtualMethod && virtualMethod.IsVirtual && !virtualMethod.IsFinal
            && receiver != null && receiver.GetType() != method.DeclaringType)
            throw new HeadlessBindingException("indirect_dispatch", "A base virtual binding cannot be invoked on a different receiver type. Select the concrete override binding.");
        bool single = TransactionMode == "single" || TransactionMode == "auto" && !owns;
        if (single && owns || Doc.IsModifiable && owns)
            throw new HeadlessBindingException("nested_transaction", "The method owns transactions and cannot run inside another transaction. Use transaction=owned.");
        object Call()
        {
            object result = method is ConstructorInfo constructor ? constructor.Invoke(arguments) : method.Invoke(receiver, arguments);
            return materialize ? HeadlessRuntime.Materialize(result, Guard) : result;
        }
        if (!single || Doc.IsModifiable) return Call();
        using var transaction = new Transaction(Doc, "Claude: ModPlus / " + method.Name);
        if (transaction.Start() != TransactionStatus.Started) throw new HeadlessBindingException("transaction_start", "Could not start a transaction.");
        NoDialogFailures.Configure(transaction);
        object value = Call();
        if (transaction.Commit() != TransactionStatus.Committed) throw new HeadlessBindingException("commit_failed", "The transaction was not committed.");
        return value;
    }
}

// A conservative preflight, not a sandbox for arbitrary code. The allowlist is
// tied to the installed module snapshot; callers cannot supply assembly paths,
// C# source, arbitrary method names or arbitrary .NET types.
internal sealed class UiCallGuard
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)).ToDictionary(o => o.Value);
    private readonly Dictionary<string, bool> checkedMethods = new Dictionary<string, bool>(StringComparer.Ordinal);
    internal UiCallGuard(Assembly assembly) { if (assembly == null) throw new ArgumentNullException(nameof(assembly)); }
    internal static bool Owned(Assembly assembly)
    {
        string name = assembly.GetName().Name ?? "";
        return name.StartsWith("mpr", StringComparison.Ordinal) || name.StartsWith("ModPlus", StringComparison.Ordinal);
    }
    internal static void CheckType(Type type)
    {
        for (Type t = type; t != null; t = t.BaseType)
        {
            string name = t.FullName ?? "";
            if (name is "System.Windows.Window" or "System.Windows.UIElement" or "System.Windows.Controls.Control" or "System.Windows.Forms.Form" or "System.Windows.Forms.Control"
                || name.StartsWith("Autodesk.Revit.UI.TaskDialog", StringComparison.Ordinal))
                throw new HeadlessBindingException("ui_required", "UI input/receiver types are unavailable in headless calls: " + type.FullName);
        }
    }
    internal bool Check(MethodBase method)
    {
        string rootKey = Key(method);
        if (checkedMethods.TryGetValue(rootKey, out bool cached)) return cached;
        var localVisited = new HashSet<string>(StringComparer.Ordinal);
        bool ownsTransactions = false;
        var queue = new Queue<(MethodBase Method, string Path)>();
        queue.Enqueue((method, method.DeclaringType.FullName + "." + method.Name));
        int visited = 0;
        while (queue.Count > 0)
        {
            var next = queue.Dequeue(); MethodBase current = next.Method;
            string key = Key(current);
            if (!localVisited.Add(key)) continue;
            if (++visited > 12000) throw new HeadlessBindingException("preflight_limit", "The call graph exceeded the preflight limit.");
            Reject(current, next.Path);
            string owner = current.DeclaringType?.FullName ?? "";
            if (owner is "Autodesk.Revit.DB.Transaction" or "Autodesk.Revit.DB.TransactionGroup" or "Autodesk.Revit.DB.SubTransaction") ownsTransactions = true;
            if (!Owned(current.Module.Assembly))
            {
                string external = current.Module.Assembly.GetName().Name ?? "";
                if (external is not "System.Private.CoreLib" and not "mscorlib" and not "netstandard" and not "RevitAPI" and not "RevitAPIUI" and not "WindowsBase" and not "PresentationCore"
                    && !external.StartsWith("System.", StringComparison.Ordinal))
                    throw new HeadlessBindingException("external_dependency", "An unaudited external dependency requires a dedicated adapter: " + next.Path);
                continue;
            }
            if ((current.Attributes & MethodAttributes.PinvokeImpl) != 0) throw new HeadlessBindingException("unmanaged_call", "ModPlus P/Invoke needs a dedicated adapter: " + next.Path);
            MethodBody body;
            try { body = current.GetMethodBody(); }
            catch (Exception exception) { throw new HeadlessBindingException("uninspectable_method", exception.Message); }
            if (body == null)
            {
                if (current.IsAbstract) throw new HeadlessBindingException("indirect_dispatch", "Unresolved abstract/interface dispatch: " + next.Path);
                continue;
            }
            Type initializerType = current.DeclaringType;
            if (initializerType.TypeInitializer != null && current != initializerType.TypeInitializer)
                queue.Enqueue((initializerType.TypeInitializer, next.Path + " -> .cctor"));
            foreach (CustomAttributeData attr in current.GetCustomAttributesData())
            {
                string attrName = attr.AttributeType.FullName ?? "";
                if (attrName.Contains("AsyncStateMachineAttribute", StringComparison.Ordinal))
                    throw new HeadlessBindingException("async_operation", "Asynchronous operations need an asynchronous host adapter: " + next.Path);
                if (attrName.Contains("IteratorStateMachineAttribute", StringComparison.Ordinal) && attr.ConstructorArguments.Count > 0 && attr.ConstructorArguments[0].Value is Type state)
                    foreach (MethodInfo stateMethod in state.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        if (stateMethod.Name.Contains("MoveNext", StringComparison.Ordinal) || stateMethod.Name.Contains("Dispose", StringComparison.Ordinal)) queue.Enqueue((stateMethod, next.Path + " -> iterator." + stateMethod.Name));
            }
            byte[] il = body.GetILAsByteArray();
            for (int position = 0; position < il.Length;)
            {
                short value = il[position++];
                if (value == 0xfe) value = (short)(0xfe00 | il[position++]);
                if (!OpCodesByValue.TryGetValue(value, out OpCode op)) throw new HeadlessBindingException("invalid_il", "Unsupported IL instruction.");
                int length = OperandLength(op.OperandType, il, position);
                if (op == OpCodes.Calli) throw new HeadlessBindingException("indirect_dispatch", "Indirect function-pointer invocation requires a dedicated adapter: " + next.Path);
                if (op.OperandType == OperandType.InlineMethod)
                {
                    int token = BitConverter.ToInt32(il, position);
                    MethodBase callee;
                    try { callee = current.Module.ResolveMethod(token, current.DeclaringType.GetGenericArguments(), current is MethodInfo mi ? mi.GetGenericArguments() : Type.EmptyTypes); }
                    catch (Exception e) { throw new HeadlessBindingException("unresolved_call", next.Path + ": " + e.Message); }
                    if (callee != null)
                    {
                        if ((op == OpCodes.Callvirt || op == OpCodes.Ldvirtftn) && callee.IsVirtual && !callee.IsFinal && !callee.DeclaringType.IsSealed
                            && (Owned(callee.Module.Assembly) || callee.DeclaringType.IsInterface && callee.Module.Assembly.GetName().Name != "RevitAPI"))
                            throw new HeadlessBindingException("indirect_dispatch", "Unresolved virtual/interface dispatch requires a dedicated adapter: " + next.Path + " -> " + callee.DeclaringType.FullName + "." + callee.Name);
                        queue.Enqueue((callee, next.Path + " -> " + callee.DeclaringType.FullName + "." + callee.Name));
                    }
                }
                if (op.OperandType == OperandType.InlineField)
                {
                    FieldInfo field;
                    try { field = current.Module.ResolveField(BitConverter.ToInt32(il, position), current.DeclaringType.GetGenericArguments(), current is MethodInfo mi ? mi.GetGenericArguments() : Type.EmptyTypes); }
                    catch (Exception e) { throw new HeadlessBindingException("unresolved_field", next.Path + ": " + e.Message); }
                    if (field != null)
                    {
                        CheckType(field.FieldType);
                        if (field.IsStatic && Owned(field.Module.Assembly) && field.DeclaringType.TypeInitializer != null)
                            queue.Enqueue((field.DeclaringType.TypeInitializer, next.Path + " -> field .cctor"));
                    }
                }
                position += length;
            }
        }
        // Cache only the root of a completely successful walk. A failed walk
        // must never poison the cache and permit a later unchecked invocation.
        checkedMethods[rootKey] = ownsTransactions;
        return ownsTransactions;
    }
    private static string Key(MethodBase method) => method.Module.ModuleVersionId + ":" + method.MetadataToken + ":" + method.DeclaringType.AssemblyQualifiedName + ":" + method;
    internal IEnumerable<object> Enumerate(IEnumerable values)
    {
        CheckInterface(values.GetType(), typeof(IEnumerable));
        IEnumerator iterator = values.GetEnumerator();
        CheckInterface(iterator.GetType(), typeof(IEnumerator));
        if (iterator is IDisposable) CheckInterface(iterator.GetType(), typeof(IDisposable));
        try { while (iterator.MoveNext()) yield return iterator.Current; }
        finally { (iterator as IDisposable)?.Dispose(); }
    }
    private void CheckInterface(Type concrete, Type contract)
    {
        CheckType(concrete);
        InterfaceMapping map = concrete.GetInterfaceMap(contract);
        foreach (MethodInfo implementation in map.TargetMethods) Check(implementation);
    }
    private static void Reject(MethodBase method, string path)
    {
        string owner = method.DeclaringType?.FullName ?? "", name = method.Name;
        CheckType(method.DeclaringType);
        bool forbidden = owner.StartsWith("Autodesk.Revit.UI.TaskDialog", StringComparison.Ordinal)
            || owner is "System.Windows.MessageBox" or "System.Windows.Forms.MessageBox"
            || owner == "Autodesk.Revit.UI.Selection.Selection" && name.StartsWith("Pick", StringComparison.Ordinal)
            || owner == "Autodesk.Revit.UI.UIApplication" && name is "PostCommand" or "LoadAddIn"
            || owner == "Autodesk.Revit.UI.ExternalEvent" && name == "Raise"
            || owner == "ModPlus_Revit.RevitEvent" && name == "Run"
            || owner.StartsWith("ModPlusAPI.Windows.", StringComparison.Ordinal)
            || name is "ShowInExceptionBox" or "ShowNotification" or "ShowYesNo" or "ShowDialog" or "ShowModeless" or "ShowModal"
            || owner.StartsWith("System.Windows.Threading.Dispatcher", StringComparison.Ordinal)
            || owner.StartsWith("Microsoft.Win32.", StringComparison.Ordinal) && owner.EndsWith("Dialog", StringComparison.Ordinal)
            || owner == "System.Diagnostics.Process" && name == "Start"
            || owner is "System.Reflection.MethodBase" or "System.Reflection.MethodInfo" or "System.Reflection.ConstructorInfo" && name == "Invoke"
            || owner == "System.Activator" && name.StartsWith("CreateInstance", StringComparison.Ordinal)
            || owner == "System.Type" && name == "InvokeMember"
            || owner.StartsWith("Microsoft.CSharp.RuntimeBinder.", StringComparison.Ordinal)
            || typeof(Delegate).IsAssignableFrom(method.DeclaringType) && name is "Invoke" or "DynamicInvoke" or "BeginInvoke"
            || owner.StartsWith("System.Reflection.Emit.", StringComparison.Ordinal)
            || owner.StartsWith("System.Linq.Expressions.", StringComparison.Ordinal) && name.StartsWith("Compile", StringComparison.Ordinal)
            || owner.StartsWith("System.Threading.ThreadPool", StringComparison.Ordinal)
            || owner == "System.Threading.Thread" && name is "Start" or ".ctor"
            || owner.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal) && name is "Run" or "Start" or "StartNew"
            || owner.StartsWith("System.Threading.", StringComparison.Ordinal) && owner.Contains("Timer", StringComparison.Ordinal)
            || owner == "Autodesk.Revit.DB.FailureHandlingOptions" && name is "SetFailuresPreprocessor" or "SetFailuresProcessor" or "SetTransactionFinalizer"
            || owner == "Autodesk.Revit.DB.Document" && (name.StartsWith("Save", StringComparison.Ordinal) || name.StartsWith("Export", StringComparison.Ordinal) || name == "Close")
            || owner == "Autodesk.Revit.UI.UIApplication" && name == "OpenAndActivateDocument"
            || owner == "Autodesk.Revit.ApplicationServices.Application" && name == "OpenDocumentFile"
            || owner == "System.IO.File" && (name.StartsWith("Write", StringComparison.Ordinal) || name.StartsWith("Append", StringComparison.Ordinal) || name is "Delete" or "Copy" or "Move" or "Create" or "Open" or "OpenWrite" or "Replace")
            || owner == "System.IO.FileInfo" && (name.StartsWith("Open", StringComparison.Ordinal) || name.StartsWith("Create", StringComparison.Ordinal) || name is "Delete" or "CopyTo" or "MoveTo" or "Replace" or "AppendText")
            || owner is "System.IO.Directory" or "System.IO.DirectoryInfo" && (name.StartsWith("Create", StringComparison.Ordinal) || name.StartsWith("Delete", StringComparison.Ordinal) || name.StartsWith("Move", StringComparison.Ordinal))
            || owner.StartsWith("System.Xaml.", StringComparison.Ordinal) && name is "Load" or "Parse" or "Save"
            || owner is "System.IO.FileStream" or "System.IO.StreamWriter" && name == ".ctor"
            || owner == "Microsoft.Win32.RegistryKey" && (name.StartsWith("Set", StringComparison.Ordinal) || name.StartsWith("Delete", StringComparison.Ordinal) || name.StartsWith("Create", StringComparison.Ordinal))
            || owner == "System.Reflection.Assembly" && name.StartsWith("Load", StringComparison.Ordinal)
            || owner == "Autodesk.Revit.DB.PrintManager" && name == "SubmitPrint";
        if (forbidden) throw new HeadlessBindingException("ui_or_indirect_call", "A dedicated headless port is required for this call path: " + path);
    }
    private static int OperandLength(OperandType type, byte[] il, int position) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineI or OperandType.ShortInlineVar or OperandType.ShortInlineBrTarget => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + BitConverter.ToInt32(il, position) * 4,
        _ => 4
    };
}

internal sealed class JsonArguments
{
    private readonly CallContext context;
    internal JsonArguments(CallContext c) { context = c; }
    internal object[] Arguments(MethodBase method, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new HeadlessBindingException("arguments", "args must be an object keyed by parameter name.");
        ParameterInfo[] parameters = method.GetParameters();
        foreach (JsonProperty property in args.EnumerateObject())
            if (!parameters.Any(p => p.Name == property.Name)) throw new HeadlessBindingException("arguments", "Unknown parameter: " + property.Name);
        return parameters.Select(p =>
        {
            if (args.TryGetProperty(p.Name, out JsonElement value)) return Convert(value, p.ParameterType, 0);
            if (p.ParameterType == typeof(Document)) return context.Doc;
            if (p.ParameterType == typeof(UIDocument)) return context.UiDoc;
            if (p.ParameterType == typeof(UIApplication)) return context.App;
            if (p.ParameterType == typeof(Autodesk.Revit.ApplicationServices.Application)) return context.App.Application;
            if (p.HasDefaultValue) return p.DefaultValue;
            throw new HeadlessBindingException("arguments", "Required parameter is missing: " + p.Name);
        }).ToArray();
    }
    internal object Convert(JsonElement value, Type type, int depth)
    {
        if (depth > 20) throw new HeadlessBindingException("input_depth", "The object graph exceeds the input depth limit.");
        UiCallGuard.CheckType(type);
        Type nullable = Nullable.GetUnderlyingType(type);
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (type.IsValueType && nullable == null) throw new HeadlessBindingException("null_value", "Null cannot be used for " + type.FullName);
            return null;
        }
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$ref", out JsonElement reference))
        {
            if (!context.Values.TryGetValue(reference.GetString(), out object result)) throw new HeadlessBindingException("unknown_ref", "Unknown request-scoped reference.");
            if (value.TryGetProperty("path", out JsonElement path))
            {
                if (path.ValueKind != JsonValueKind.Array || path.GetArrayLength() > 20) throw new HeadlessBindingException("reference_path", "path must have at most 20 property/index components.");
                foreach (JsonElement component in path.EnumerateArray()) result = ReadMember(result, component);
            }
            if (result == null && type.IsValueType && nullable == null) throw new HeadlessBindingException("null_value", "A null reference cannot be assigned to " + type.FullName);
            if (result != null && !(nullable ?? type).IsInstanceOfType(result)) throw new HeadlessBindingException("reference_type", "Reference type differs from " + type.FullName);
            return result;
        }
        if (nullable != null) return Convert(value, nullable, depth + 1);
        if (type == typeof(JsonElement)) return value.Clone();
        if (type == typeof(object))
        {
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
            if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt64(out long integer) ? (object)integer : value.GetDouble();
        }
        if (type == typeof(string)) return value.GetString();
        if (type == typeof(bool)) return value.GetBoolean();
        if (type.IsEnum)
        {
            object result = value.ValueKind == JsonValueKind.String ? Enum.Parse(type, value.GetString(), false) : Enum.ToObject(type, value.GetInt64());
            if (!type.IsDefined(typeof(FlagsAttribute), false) && !Enum.IsDefined(type, result)) throw new HeadlessBindingException("enum_value", "Unknown enum value for " + type.FullName);
            return result;
        }
        if (type == typeof(Guid)) return Guid.Parse(value.GetString());
        if (type == typeof(System.Windows.Media.Color))
        {
            string hex = value.GetString()?.TrimStart('#');
            if (hex == null || hex.Length is not 6 and not 8) throw new HeadlessBindingException("color", "Use #RRGGBB or #AARRGGBB.");
            uint color = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return System.Windows.Media.Color.FromArgb(hex.Length == 8 ? (byte)(color >> 24) : (byte)255, (byte)(color >> 16), (byte)(color >> 8), (byte)color);
        }
        if (type == typeof(ElementId)) return new ElementId(value.GetInt64());
        if (typeof(Element).IsAssignableFrom(type))
        {
            long id = value.ValueKind == JsonValueKind.Object ? value.GetProperty("element_id").GetInt64() : value.GetInt64();
            Element result = context.Doc.GetElement(new ElementId(id));
            if (result == null || !type.IsInstanceOfType(result)) throw new HeadlessBindingException("element_type", "Element " + id + " is missing or is not " + type.Name);
            return result;
        }
        if (type == typeof(Document))
            throw new HeadlessBindingException("document_ref", "Use {$ref:'doc'} for the current document. Cross-document commits require a dedicated adapter.");
        if (type == typeof(Reference)) return Reference.ParseFromStableRepresentation(context.Doc, value.GetProperty("stable_reference").GetString());
        if (type == typeof(XYZ)) return Point(value, value.TryGetProperty("unit", out JsonElement pointUnit) && pointUnit.GetString() == "vector");
        if (type == typeof(Line))
            return Line.CreateBound(Point(value.GetProperty("start")), Point(value.GetProperty("end")));
        if (type == typeof(Curve))
        {
            if (value.TryGetProperty("curve_element_id", out JsonElement id))
            {
                Element element = context.Doc.GetElement(new ElementId(id.GetInt64()));
                return element is CurveElement ce ? ce.GeometryCurve : element?.Location is LocationCurve lc ? lc.Curve : throw new HeadlessBindingException("curve", "Element has no curve.");
            }
            return Line.CreateBound(Point(value.GetProperty("start")), Point(value.GetProperty("end")));
        }
        if (type == typeof(CurveLoop))
        {
            if (value.GetProperty("curves").GetArrayLength() < 1 || value.GetProperty("curves").GetArrayLength() > 1000) throw new HeadlessBindingException("input_size", "A CurveLoop needs 1..1000 curves.");
            var loop = new CurveLoop();
            foreach (JsonElement curve in value.GetProperty("curves").EnumerateArray()) loop.Append((Curve)Convert(curve, typeof(Curve), depth + 1));
            return loop;
        }
        if (type == typeof(Transform))
        {
            var transform = Transform.Identity;
            if (value.TryGetProperty("origin", out JsonElement origin)) transform.Origin = Point(origin);
            if (value.TryGetProperty("basis_x", out JsonElement x)) transform.BasisX = Point(x, true);
            if (value.TryGetProperty("basis_y", out JsonElement y)) transform.BasisY = Point(y, true);
            if (value.TryGetProperty("basis_z", out JsonElement z)) transform.BasisZ = Point(z, true);
            return transform;
        }
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            double number;
            if (value.ValueKind == JsonValueKind.Object)
            {
                number = value.GetProperty("value").GetDouble();
                string unit = value.GetProperty("unit").GetString();
                number *= unit switch { "mm" => 1d / 304.8, "m" => 1d / 0.3048, "ft" or "raw" or "rad" => 1d, "deg" => Math.PI / 180d, _ => throw new HeadlessBindingException("unit", "Supported scalar units: raw, ft, mm, m, rad, deg.") };
            }
            else number = value.GetDouble();
            if (!double.IsFinite(number)) throw new HeadlessBindingException("finite_number", "A finite number is required.");
            return System.Convert.ChangeType(number, type, CultureInfo.InvariantCulture);
        }
        if (type.IsPrimitive) return JsonSerializer.Deserialize(value.GetRawText(), type);
        if (type.IsGenericType)
        {
            Type generic = type.GetGenericTypeDefinition(); Type[] arguments = type.GetGenericArguments();
            if (generic == typeof(Dictionary<,>) || generic == typeof(IDictionary<,>) || generic == typeof(IReadOnlyDictionary<,>))
            {
                JsonElement entries = value.GetProperty("entries");
                if (entries.GetArrayLength() > 10000) throw new HeadlessBindingException("input_size", "A dictionary exceeds 10000 entries.");
                IDictionary dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(arguments));
                foreach (JsonElement entry in entries.EnumerateArray()) dictionary.Add(Convert(entry.GetProperty("key"), arguments[0], depth + 1), Convert(entry.GetProperty("value"), arguments[1], depth + 1));
                return dictionary;
            }
            if ((generic.FullName.StartsWith("System.ValueTuple`", StringComparison.Ordinal) || generic.FullName.StartsWith("System.Tuple`", StringComparison.Ordinal)) && value.ValueKind == JsonValueKind.Array)
            {
                if (value.GetArrayLength() != arguments.Length) throw new HeadlessBindingException("tuple", "Tuple arity differs from the method signature.");
                return Activator.CreateInstance(type, arguments.Select((t, index) => Convert(value[index], t, depth + 1)).ToArray());
            }
        }
        if (type.IsArray)
        {
            Type elementType = type.GetElementType(); JsonElement[] items = value.EnumerateArray().ToArray();
            if (items.Length > 10000) throw new HeadlessBindingException("input_size", "A collection exceeds 10000 items.");
            Array array = Array.CreateInstance(elementType, items.Length);
            for (int i = 0; i < items.Length; i++) array.SetValue(Convert(items[i], elementType, depth + 1), i);
            return array;
        }
        if (type.IsGenericType && value.ValueKind == JsonValueKind.Array)
        {
            Type definition = type.GetGenericTypeDefinition(), element = type.GetGenericArguments()[0];
            if (definition == typeof(List<>) || definition == typeof(IList<>) || definition == typeof(IEnumerable<>) || definition == typeof(ICollection<>) || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>))
            {
                IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element));
                foreach (JsonElement item in value.EnumerateArray()) { if (list.Count == 10000) throw new HeadlessBindingException("input_size", "A collection exceeds 10000 items."); list.Add(Convert(item, element, depth + 1)); }
                return list;
            }
            if (definition == typeof(HashSet<>) || definition == typeof(ISet<>))
            {
                object list = Convert(value, typeof(List<>).MakeGenericType(element), depth + 1);
                return Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(element), list);
            }
        }
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$new", out JsonElement spec))
        {
            object result = New(spec, depth + 1);
            if (!type.IsInstanceOfType(result)) throw new HeadlessBindingException("object_type", "Created object differs from " + type.FullName);
            return result;
        }
        throw new HeadlessBindingException("input_adapter_required", "Use a request reference, an explicit $new constructor, or a dedicated converter for " + type.FullName);
    }
    internal object New(JsonElement spec, int depth = 0)
    {
        if (depth > 20) throw new HeadlessBindingException("input_depth", "The object graph exceeds the input depth limit.");
        string typeId = spec.GetProperty("type").GetString();
        Type type = context.DataType(typeId); JsonElement typeEntry = context.Catalog.Type(context.Module, typeId);
        string constructorId = spec.TryGetProperty("constructor", out JsonElement id) ? id.GetString() : null;
        JsonElement[] choices = typeEntry.GetProperty("constructors").EnumerateArray().Where(c => constructorId == null ? c.GetProperty("parameters").GetArrayLength() == 0 : c.GetProperty("id").GetString() == constructorId).ToArray();
        if (choices.Length != 1) throw new HeadlessBindingException("constructor", "Select an exact constructor ID from the type schema.");
        ConstructorInfo constructor = context.Method(choices[0]) as ConstructorInfo ?? throw new HeadlessBindingException("constructor", "A constructor binding is required.");
        JsonElement arguments = spec.TryGetProperty("args", out JsonElement args) ? args : JsonSerializer.SerializeToElement(new Dictionary<string, object>());
        object instance = context.Invoke(constructor, null, Arguments(constructor, arguments));
        context.Created.Add(instance);
        if (spec.TryGetProperty("members", out JsonElement members)) SetMembers(instance, members, depth + 1);
        return instance;
    }
    internal void SetMembers(object instance, JsonElement members, int depth = 0)
    {
        if (depth > 20) throw new HeadlessBindingException("input_depth", "The object graph exceeds the input depth limit.");
        if (instance == null || !context.Created.Contains(instance)) throw new HeadlessBindingException("object_ownership", "Only newly created request-scoped input objects can have members assigned.");
        if (members.ValueKind != JsonValueKind.Object) throw new HeadlessBindingException("members", "members must be an object.");
        Type type = instance.GetType();
        foreach (JsonProperty member in members.EnumerateObject())
        {
            PropertyInfo property = type.GetProperty(member.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.GetIndexParameters().Length == 0 && property.SetMethod != null)
            {
                context.Guard.Check(property.SetMethod);
                context.Invoke(property.SetMethod, instance, new[] { Convert(member.Value, property.PropertyType, depth + 1) });
                continue;
            }
            FieldInfo field = type.GetField(member.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null || field.IsStatic || field.IsInitOnly || field.IsLiteral)
                throw new HeadlessBindingException("member", "Writable member not found: " + type.FullName + "." + member.Name);
            field.SetValue(instance, Convert(member.Value, field.FieldType, depth + 1));
        }
    }
    private object ReadMember(object value, JsonElement component)
    {
        if (value == null) throw new HeadlessBindingException("reference_path", "Cannot traverse a null reference.");
        Type type = value.GetType(); UiCallGuard.CheckType(type);
        if (component.ValueKind == JsonValueKind.Number)
        {
            int index = component.GetInt32();
            if (value is Array array)
            {
                if (array.Rank != 1 || index < 0 || index >= array.Length) throw new HeadlessBindingException("reference_path", "Array index is out of range.");
                return array.GetValue(index);
            }
            if (value is IList list)
            {
                InterfaceMapping map = type.GetInterfaceMap(typeof(IList));
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                    if (map.InterfaceMethods[i].Name is "get_Item" or "get_Count") context.Guard.Check(map.TargetMethods[i]);
                if (index < 0 || index >= list.Count) throw new HeadlessBindingException("reference_path", "List index is out of range.");
                return list[index];
            }
            throw new HeadlessBindingException("reference_path", "Index components require an array or list.");
        }
        string name = component.GetString();
        PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetMethod != null && property.GetIndexParameters().Length == 0)
        {
            context.Guard.Check(property.GetMethod);
            if (property.GetMethod.IsVirtual && !property.GetMethod.IsFinal && UiCallGuard.Owned(type.Assembly) && property.DeclaringType != type)
                throw new HeadlessBindingException("indirect_dispatch", "An inherited virtual reference property requires a dedicated converter.");
            return property.GetValue(value);
        }
        FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
        if (field != null) return field.GetValue(value);
        throw new HeadlessBindingException("reference_path", "Readable property/field not found: " + type.FullName + "." + name);
    }
    private static XYZ Point(JsonElement value, bool vector = false)
    {
        JsonElement coordinates = value.GetProperty("value");
        if (coordinates.GetArrayLength() != 3) throw new HeadlessBindingException("point", "A point/vector must have three coordinates.");
        double[] numbers = coordinates.EnumerateArray().Select(n => n.GetDouble()).ToArray();
        if (numbers.Any(n => !double.IsFinite(n))) throw new HeadlessBindingException("point", "Coordinates must be finite.");
        string unit = value.GetProperty("unit").GetString();
        double scale = unit switch { "mm" when !vector => 1d / 304.8, "m" when !vector => 1d / 0.3048, "ft" when !vector => 1d, "vector" when vector => 1d, _ => throw new HeadlessBindingException("point_unit", vector ? "Use unit=vector for basis vectors." : "Use explicit point units mm, m or ft.") };
        return new XYZ(numbers[0] * scale, numbers[1] * scale, numbers[2] * scale);
    }
}

internal sealed class Results
{
    private readonly UiCallGuard guard;
    private readonly HashSet<object> seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
    private int objects;
    internal Results(UiCallGuard g) { guard = g; }
    internal object Read(object value, int depth = 0)
    {
        if (value == null) return null;
        Type type = value.GetType();
        if (value is string or bool || type.IsPrimitive || value is decimal) return value;
        if (value is Enum) return value.ToString();
        if (value is Guid) return value.ToString();
        if (++objects > 20000 || depth > 8) return new { type = type.FullName, truncated = true };
        if (!type.IsValueType && !seen.Add(value)) return new { type = type.FullName, repeatedReference = true };
        if (value is ElementId id) return id.Value;
        if (value is Element element) return new { elementId = element.Id.Value, uniqueId = element.UniqueId, name = element.Name, typeId = element.GetTypeId().Value };
        if (value is XYZ xyz) return new { x = xyz.X, y = xyz.Y, z = xyz.Z, unit = "ft" };
        if (value is Transform transform) return new { origin = Read(transform.Origin, depth + 1), basisX = Vector(transform.BasisX), basisY = Vector(transform.BasisY), basisZ = Vector(transform.BasisZ) };
        if (value is Reference reference) return new { elementId = reference.ElementId.Value, linkedElementId = reference.LinkedElementId.Value };
        if (value is Curve curve) return new { type = type.FullName, points = curve.Tessellate().Select(p => Read(p, depth + 1)).ToArray() };
        if (value is IEnumerable items)
        {
            var rows = new List<object>();
            foreach (object item in guard.Enumerate(items)) { if (rows.Count == 10000) throw new HeadlessBindingException("result_size", "The result exceeds 10000 items. Narrow the input."); rows.Add(Read(item, depth + 1)); }
            return rows;
        }
        var properties = new Dictionary<string, object>();
        var fields = new Dictionary<string, object>();
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.CanRead && p.GetIndexParameters().Length == 0).Take(100))
        {
            try { guard.Check(property.GetMethod); properties[property.Name] = Read(property.GetValue(value), depth + 1); }
            catch (Exception e) { properties[property.Name] = new { unavailable = true, reason = e.Message }; }
        }
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public).Take(100)) fields[field.Name] = Read(field.GetValue(value), depth + 1);
        return new { type = type.FullName, properties, fields };
    }
    private static object Vector(XYZ value) => new { x = value.X, y = value.Y, z = value.Z, unit = "vector" };
}

public sealed class HeadlessRuntime
{
    private readonly BindingCatalog catalog;
    [ThreadStatic] private static bool executing;
    public HeadlessRuntime(BindingCatalog bindings) { catalog = bindings ?? throw new ArgumentNullException(nameof(bindings)); }
    public string ValidateBindings(string module, IEnumerable<string> methodIds, UIApplication app)
    {
        string[] ids = methodIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length < 1 || ids.Length > 50) throw new HeadlessBindingException("method_ids", "Supply 1..50 method binding IDs.");
        var context = new CallContext(app, catalog, module);
        var results = new List<object>();
        foreach (string id in ids)
        {
            ClaudeRevit.Tools.ToolContext.ThrowIfCancelled();
            try
            {
                MethodBase method = context.Method(catalog.Method(module, id));
                results.Add(new { id, headlessEligible = true, ownsTransactions = context.Guard.Check(method) });
            }
            catch (HeadlessBindingException exception) { results.Add(new { id, headlessEligible = false, errorCode = exception.Code, reason = exception.Message }); }
        }
        return JsonSerializer.Serialize(new { module, results, modelModified = false,
            note = "This checks method signatures and static call graphs. Instance construction, argument conversion and model-specific prerequisites are checked at execution." });
    }
    public string Run(string module, JsonElement request, UIApplication app)
    {
        if (executing) throw new HeadlessBindingException("reentrant_call", "Nested headless requests are unsupported.");
        bool preview = !request.TryGetProperty("preview", out JsonElement p) || p.GetBoolean();
        string transactionMode = request.TryGetProperty("transaction", out JsonElement tm) ? tm.GetString() : "auto";
        if (transactionMode is not "auto" and not "single" and not "owned") throw new HeadlessBindingException("transaction", "Use auto, single, or owned.");
        JsonElement[] steps = request.GetProperty("steps").EnumerateArray().ToArray();
        if (steps.Length < 1 || steps.Length > 100) throw new HeadlessBindingException("steps", "Supply 1..100 steps.");
        var context = new CallContext(app, catalog, module);
        context.TransactionMode = transactionMode;
        if (context.Doc.IsModifiable) throw new HeadlessBindingException("transaction_context", "Call this tool without an enclosing transaction. Forward RequiresNoTurnGroup in the host proxy.");
        var converter = new JsonArguments(context);
        // Check all explicitly selected methods before the first mutation.
        foreach (JsonElement step in steps)
            if (step.TryGetProperty("method", out JsonElement id)) context.Method(catalog.Method(module, id.GetString()));
        List<ElementId> savedSelection = context.UiDoc.Selection.GetElementIds().ToList();
        var output = new List<object>();
        using var group = new TransactionGroup(context.Doc, "Claude: ModPlus / ModPlus " + module);
        using var failures = new NoDialogFailures(app.Application, context.Doc);
        executing = true;
        try
        {
            if (group.Start() != TransactionStatus.Started) throw new HeadlessBindingException("transaction_start", "Could not start the transaction group.");
            if (request.TryGetProperty("element_ids", out JsonElement ids))
            {
                List<ElementId> selected = ids.EnumerateArray().Select(id => new ElementId(id.GetInt64())).Distinct().ToList();
                if (selected.Count > 10000 || selected.Any(id => context.Doc.GetElement(id) == null)) throw new HeadlessBindingException("element_ids", "Selection IDs are missing or exceed the limit.");
                context.UiDoc.Selection.SetElementIds(selected);
            }
            foreach (JsonElement step in steps)
            {
                ClaudeRevit.Tools.ToolContext.ThrowIfCancelled();
                string action = step.GetProperty("action").GetString(); object result;
                if (action == "new") result = converter.New(step);
                else if (action == "set")
                {
                    string receiver = step.GetProperty("instance").GetString();
                    if (!context.Values.TryGetValue(receiver, out object instance)) throw new HeadlessBindingException("unknown_ref", "Unknown instance: " + receiver);
                    converter.SetMembers(instance, step.GetProperty("members")); result = instance;
                }
                else if (action == "call")
                {
                    JsonElement binding = catalog.Method(module, step.GetProperty("method").GetString());
                    MethodInfo method = context.Method(binding) as MethodInfo ?? throw new HeadlessBindingException("method", "A method binding is required.");
                    object instance = null;
                    if (!method.IsStatic)
                    {
                        string receiver = step.GetProperty("instance").GetString();
                        if (!context.Values.TryGetValue(receiver, out instance) || !method.DeclaringType.IsInstanceOfType(instance)) throw new HeadlessBindingException("instance", "The receiver differs from the method declaring type.");
                    }
                    JsonElement args = step.TryGetProperty("args", out JsonElement supplied) ? supplied : JsonSerializer.SerializeToElement(new Dictionary<string, object>());
                    object[] arguments = converter.Arguments(method, args);
                    result = context.Invoke(method, instance, arguments, materialize: true);
                }
                else throw new HeadlessBindingException("action", "Supported step actions: new, set, call.");
                if (failures.HasErrors) throw new HeadlessBindingException("model_failure", "Revit rejected an algorithm transaction. The request was rolled back.");
                if (step.TryGetProperty("store", out JsonElement store))
                {
                    string name = store.GetString();
                    if (string.IsNullOrWhiteSpace(name) || context.Values.ContainsKey(name)) throw new HeadlessBindingException("reference_name", "Use a unique non-empty request reference name.");
                    context.Values[name] = result;
                }
                if (!step.TryGetProperty("emit", out JsonElement emit) || emit.GetBoolean()) output.Add(new Results(context.Guard).Read(result));
            }
            if (failures.HasErrors) throw new HeadlessBindingException("model_failure", "Revit rejected the request. No model changes were committed.");
            ClaudeRevit.Tools.ToolContext.ThrowIfCancelled();
            TransactionStatus status = preview ? group.RollBack() : group.Assimilate();
            if (status != (preview ? TransactionStatus.RolledBack : TransactionStatus.Committed)) throw new HeadlessBindingException("group_status", "Unexpected final transaction group status: " + status);
            return JsonSerializer.Serialize(new { module, preview, committed = !preview, results = output,
                failures = failures.Messages,
                note = preview ? "Changes were rolled back; element IDs created during preview are temporary." : "Changes were committed." });
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            throw new HeadlessBindingException("algorithm_failed", exception.InnerException.Message);
        }
        catch
        {
            if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            throw;
        }
        finally
        {
            try { context.UiDoc.Selection.SetElementIds(savedSelection.Where(id => context.Doc.GetElement(id) != null).ToList()); }
            finally { executing = false; }
        }
    }
    internal static object Materialize(object value, UiCallGuard guard)
    {
        if (value is not IEnumerable values || value is string || value is Array || value is IList) return value;
        var result = new List<object>();
        foreach (object item in guard.Enumerate(values)) { if (result.Count == 10000) throw new HeadlessBindingException("result_size", "An iterator exceeds 10000 items."); result.Add(item); }
        // Keep the original typed enumerable for later steps where possible.
        Type enumerable = value.GetType().GetInterfaces().FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable == null) return result;
        IList typed = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(enumerable.GetGenericArguments()[0]));
        foreach (object item in result) typed.Add(item);
        return typed;
    }
}
