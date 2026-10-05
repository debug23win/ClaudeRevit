using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClaudeRevit.Tools;

namespace ClaudeRevit.NativeTests;

// Loaded in a separate AssemblyLoadContext from the installed add-in. Its
// ExternalEvent runs after the launcher's transaction ends. No OnStartup, MCP
// server, pane, or global updater is started by this acceptance harness.
public static class NativeAcceptance
{
    private static readonly Handler HandlerInstance = new();
    private static ExternalEvent? _event;
    private static string? _pending;
    private static bool _running;
    public static string Queue(string requestPath)
    {
        if (_running || _pending != null) throw new InvalidOperationException("Acceptance job is already queued/running.");
        _event ??= ExternalEvent.Create(HandlerInstance);
        _pending = Path.GetFullPath(requestPath);
        var result = _event.Raise();
        if (result != ExternalEventRequest.Accepted) { _pending = null; throw new InvalidOperationException("ExternalEvent: " + result); }
        return "queued";
    }
    private sealed class Handler : IExternalEventHandler
    {
        public string GetName() => "ClaudeRevit native acceptance (disposable documents only)";
        public void Execute(UIApplication app)
        {
            var requestPath = _pending; _pending = null;
            if (requestPath == null) return;
            _running = true;
            var clock = Stopwatch.StartNew();
            var output = requestPath + ".result.json";
            var openBefore = app.Application.Documents.Cast<Document>().ToArray();
            var activeBefore = app.ActiveUIDocument?.Document;
            var modifiedBefore = openBefore.ToDictionary(d => d, d => d.IsModified);
            object? result = null; string? error = null;
            try
            {
                File.WriteAllText(requestPath + ".started.json", JsonSerializer.Serialize(new { utc = DateTime.UtcNow, state = "running" }));
                var failureType = typeof(InspectFamilyFiles).Assembly.GetType("ClaudeRevit.Tools.FamilyInspectionFailures", true)!;
                using var backgroundFailures = (IDisposable)Activator.CreateInstance(failureType, app.Application)!;
                using var request = JsonDocument.Parse(File.ReadAllText(requestPath));
                var root = request.RootElement;
                var action = root.GetProperty("action").GetString();
                if (action == "tool")
                {
                    var typeName = root.GetProperty("type").GetString()!;
                    var type = typeof(InspectFamilyFiles).Assembly.GetType("ClaudeRevit.Tools." + typeName, true)!;
                    if (Activator.CreateInstance(type) is not IRevitTool tool) throw new InvalidOperationException("Not a native tool.");
                    // This entry point inspects references only. Editing/model tests
                    // below must create their own disposable document explicitly.
                    if (tool is not (InspectFamilyFiles or AnalyzeFamilyStructure or FlexFamily)) throw new InvalidOperationException("Reference tool is not read-only or rollback-only.");
                    var input = root.GetProperty("arguments").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
                    result = JsonSerializer.Deserialize<JsonElement>(tool.Execute(input, app));
                }
                else if (action == "sections") result = SyntheticFixtures.Sections(app, root);
                else if (action == "checkpoint") result = SyntheticFixtures.Checkpoint(app, root);
                else if (action == "nested") result = SyntheticFixtures.Nested(app, root);
                else if (action == "live_schedule") result = SyntheticFixtures.LiveSchedule(app, root);
                else if (action == "benchmark_probe") result = SyntheticFixtures.BenchmarkProbe(app);
                else if (action == "dependent_node") result = SyntheticFixtures.DependentNode(app, root);
                else if (action == "template_probe")
                {
                    var rows = new List<object>();
                    foreach (var template in root.GetProperty("templates").EnumerateArray())
                    {
                        Document? family = null;
                        try
                        {
                            family = app.Application.NewFamilyDocument(template.GetString()!);
                            rows.Add(new { template = template.GetString(), view = family.ActiveView?.Name, direction = family.ActiveView == null ? null : new[] { family.ActiveView.ViewDirection.X, family.ActiveView.ViewDirection.Y, family.ActiveView.ViewDirection.Z },
                                views = new FilteredElementCollector(family).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).Select(v => new { name = v.Name, kind = v.ViewType.ToString(), direction = new[] { v.ViewDirection.X, v.ViewDirection.Y, v.ViewDirection.Z } }).ToArray(),
                                parameters = family.FamilyManager.Parameters.Cast<FamilyParameter>().Select(p => new { name = p.Definition.Name, instance = p.IsInstance, spec = p.Definition.GetDataType().TypeId }).ToArray() });
                        }
                        finally { if (family is { IsValidObject: true }) family.Close(false); }
                    }
                    result = rows;
                }
                else throw new InvalidOperationException("Unknown acceptance action: " + action);
            }
            catch (Exception ex) { error = ex.ToString(); }
            finally
            {
                _running = false;
                var openAfter = app.Application.Documents.Cast<Document>().ToArray();
                var originalDocumentsPreserved = openBefore.All(d => d.IsValidObject && openAfter.Contains(d) && d.IsModified == modifiedBefore[d]);
                var activePreserved = app.ActiveUIDocument?.Document == activeBefore;
                File.WriteAllText(output, JsonSerializer.Serialize(new { utc = DateTime.UtcNow, revit = app.Application.VersionNumber, build = app.Application.VersionBuild,
                    assembly_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(InspectFamilyFiles).Assembly.Location))).ToLowerInvariant(),
                    elapsed_seconds = clock.Elapsed.TotalSeconds, original_documents_preserved = originalDocumentsPreserved, active_document_preserved = activePreserved,
                    background_document_leaks = openAfter.Except(openBefore).Select(d => d.Title).ToArray(), result, error }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }
    internal static void Target(Document? document)
    {
        var type = typeof(InspectFamilyFiles).Assembly.GetType("ClaudeRevit.Tools.ToolContext", true)!;
        if (document == null) type.GetMethod("Clear")!.Invoke(null, null);
        else type.GetMethod("Set")!.Invoke(null, new object?[] { CancellationToken.None, document, null });
    }
}
