using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

internal static class ManagedTests
{
    private static int passed;
    private static void Check(bool success, string description)
    {
        if (!success) throw new Exception("FAILED: " + description);
        passed++;
    }
    public static void Main(string[] args)
    {
        Assembly library = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
        Assembly fixture = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
        Type guard = library.GetType("ModPlus.Headless.UiCallGuard", true);
        object instance = Activator.CreateInstance(guard, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { fixture }, null);
        MethodInfo inspect = guard.GetMethod("Check", BindingFlags.Instance | BindingFlags.NonPublic);
        Type functions = fixture.GetType("mprHeadlessFixtures.Algorithms", true);
        Check((bool)inspect.Invoke(instance, new object[] { functions.GetMethod("Sum") }) == false, "pure function allowed");
        Check((bool)inspect.Invoke(instance, new object[] { functions.GetMethod("Sum") }) == false, "safe cache remains valid");
        foreach (string name in new[] { "DirectUi", "TransitiveUi", "NewWindow", "DeferredUi", "AsyncOperation", "ReflectiveCall", "MessageBeep", "DelegateCall", "VirtualCall", "StaticFieldCall" })
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool rejected = false;
                try { inspect.Invoke(instance, new object[] { functions.GetMethod(name) }); }
                catch (TargetInvocationException e) { rejected = e.InnerException?.GetType().FullName == "ModPlus.Headless.HeadlessBindingException"; }
                Check(rejected, name + ": rejected on attempt " + attempt);
            }
        }
        object unsafeEnumerable = Activator.CreateInstance(fixture.GetType("mprHeadlessFixtures.LateUiEnumerable", true));
        MethodInfo enumerate = guard.GetMethod("Enumerate", BindingFlags.Instance | BindingFlags.NonPublic);
        var deferred = (IEnumerable<object>)enumerate.Invoke(instance, new[] { unsafeEnumerable });
        bool lateRejected = false;
        try { deferred.ToArray(); } catch (Exception e) { lateRejected = e.GetType().FullName == "ModPlus.Headless.HeadlessBindingException"; }
        Check(lateRejected, "actual iterator Current body checked before iteration");
        Type catalogType = library.GetType("ModPlus.Headless.BindingCatalog", true);
        object catalog = Activator.CreateInstance(catalogType, File.ReadAllText(args[2]));
        MethodInfo moduleMethod = catalogType.GetMethod("Module");
        MethodInfo typeMethod = catalogType.GetMethod("Type");
        JsonElement module = (JsonElement)moduleMethod.Invoke(catalog, new object[] { "mprCopyAlongCurve" });
        Check(module.GetProperty("available").GetBoolean(), "available module lookup");
        JsonElement method = module.GetProperty("methods").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "Divide");
        Check(method.GetProperty("parameters")[1].GetProperty("name").GetString() == "spacing", "original parameter name retained");
        Check(method.GetProperty("parameters")[3].GetProperty("type").GetString().Contains("Nullable", StringComparison.Ordinal), "nullable signature retained");
        JsonElement absent = (JsonElement)moduleMethod.Invoke(catalog, new object[] { "mprCopyParameterValues" });
        Check(!absent.GetProperty("available").GetBoolean(), "missing 2027 module stays unavailable");
        bool missingRejected = false;
        try { moduleMethod.Invoke(catalog, new object[] { "../outside" }); }
        catch (TargetInvocationException e) { missingRejected = e.InnerException?.GetType().FullName == "ModPlus.Headless.HeadlessBindingException"; }
        Check(missingRejected, "unknown/traversal module rejected");
        using JsonDocument source = JsonDocument.Parse(File.ReadAllText(args[2]));
        JsonElement shared = source.RootElement.GetProperty("shared")[0].GetProperty("types").EnumerateArray().First();
        JsonElement result = (JsonElement)typeMethod.Invoke(catalog, new object[] { "mprCopyAlongCurve", shared.GetProperty("id").GetString() });
        Check(result.GetProperty("assembly").GetString() == "ModPlusAPI", "shared DTO lookup retains assembly identity");
        JsonElement[] available = source.RootElement.GetProperty("modules").EnumerateArray().Where(m => m.GetProperty("available").GetBoolean()).ToArray();
        string previousBindings = Environment.GetEnvironmentVariable("CLAUDEREVIT_MODPLUS_BINDINGS");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDEREVIT_MODPLUS_BINDINGS", null);
            object embedded = catalogType.GetMethod("FromEmbedded").Invoke(null, null);
            var modules = (IEnumerable<JsonElement>)catalogType.GetProperty("Modules").GetValue(embedded);
            Check(modules.Count() == 85, "all 85 module contracts embedded in production assembly");
            JsonElement embeddedModule = (JsonElement)moduleMethod.Invoke(embedded, new object[] { "mprCopyAlongCurve" });
            Check(embeddedModule.GetRawText() == module.GetRawText(), "embedded snapshot matches distributed definitions");
        }
        finally { Environment.SetEnvironmentVariable("CLAUDEREVIT_MODPLUS_BINDINGS", previousBindings); }
        Check(available.Length == 83, "all remaining 83 modules included");
        Check(available.All(m => m.GetProperty("methods").GetArrayLength() > 0), "every module has core bindings");
        Check(available.SelectMany(m => m.GetProperty("methods").EnumerateArray()).Select(m => m.GetProperty("id").GetString()).Distinct().Count() == available.Sum(m => m.GetProperty("methods").GetArrayLength()), "binding IDs unique");
        Check(available.Any(m => m.GetProperty("types").EnumerateArray().Any(t => t.GetProperty("properties").GetArrayLength() > 0)), "writable DTO property schemas included");
        Type contracts = library.GetType("ModPlus.Headless.NativeContracts", true);
        MethodInfo forModule = contracts.GetMethod("ForModule", BindingFlags.Public | BindingFlags.Static);
        Check(source.RootElement.GetProperty("modules").EnumerateArray().Sum(m => ((IEnumerable<JsonElement>)forModule.Invoke(null, new object[] { m.GetProperty("id").GetString() })).Count()) == 32, "all 32 native contracts described");
        MethodInfo validate = contracts.GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic);
        void Validate(string moduleId, string operation, string input)
        {
            using var document = JsonDocument.Parse(input);
            validate.Invoke(null, new object[] { moduleId, operation, document.RootElement });
        }
        Validate("mprPhases", "list_phases", "{}");
        Validate("mprParameters", "set_parameters", "{\"element_ids\":[1],\"values\":[{\"parameter\":{\"built_in\":\"ALL_MODEL_INSTANCE_COMMENTS\"},\"value\":\"Hello\"},{\"parameter\":{\"guid\":\"91f8d025-5a67-4026-9ccf-b7882336b74a\"},\"value\":{\"value\":250,\"unit\":\"mm\"}}]}");
        Check(true, "native read and typed parameter contracts accepted");
        foreach (var invalid in new[]
        {
            ("mprPhases", "list_phases", "{\"element_ids\":[1]}"),
            ("mprVisibility", "set_element_visibility", "{\"view_id\":1,\"element_ids\":[],\"hidden\":true}"),
            ("mprParameters", "set_parameters", "{\"element_ids\":[1],\"values\":[{\"parameter\":{\"name\":\"X\",\"guid\":\"x\"},\"value\":5}]}"),
            ("mprAlignViews", "align_viewport_centers", "{\"reference_viewport_id\":1,\"element_ids\":[2],\"axis\":\"z\"}"),
            ("mprNotes", "create_text_note", "{\"view_id\":1,\"text_type_id\":2,\"point_mm\":[0,0],\"text\":\"X\"}")
        })
        {
            bool rejected = false;
            try { Validate(invalid.Item1, invalid.Item2, invalid.Item3); }
            catch (TargetInvocationException e) { rejected = e.InnerException is ArgumentException; }
            Check(rejected, "malformed native request rejected: " + invalid.Item2);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { passed, scope = "Managed metadata, call-graph rejection and failed-cache regression. No live model execution." }));
    }
}
