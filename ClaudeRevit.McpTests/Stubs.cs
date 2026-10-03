using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeRevit.Services;

namespace ClaudeRevit.Services
{
    public static class SettingsStore
    {
        public static int McpPort { get; set; }
        public static string McpToken => "isolated-test-token";
        public static bool McpEnabled => true;
        public static bool AllowCodeExecution => false;
        public static IReadOnlyList<string> DisabledToolGroups { get; set; } = Array.Empty<string>();
    }
    public static class DocumentSessions
    {
        public static ConversationWorkspace CurrentWorkspace { get; set; } = null!;
        public static string CurrentDocumentKey => "test-document";
    }
    public static class MemoryStore { public static string Load() => ""; }
    public static class ExperienceStore { public static string Digest() => ""; }
    public static class Log
    {
        public static void Info(string message) { }
        public static void Error(string message, Exception? exception = null) => Console.Error.WriteLine(message + ": " + exception?.Message);
    }
}
namespace ClaudeRevit.Tools
{
    public sealed class TestSchema
    {
        public Dictionary<string, JsonElement>? Properties => new() { ["count"] = JsonSerializer.SerializeToElement(new { type = "integer", minimum = 1 }) };
        public string[]? Required => new[] { "count" };
    }
    public sealed class TestTool(string name, string category = "Test", bool code = false)
    {
        public string Name => name;
        public string Description => "An isolated test tool.";
        public string Category => category;
        public bool RequiresCodeExecutionOptIn => code;
        public TestSchema InputSchema => new();
    }
    public sealed class ToolRegistry
    {
        public static ToolRegistry Instance { get; } = new();
        public IEnumerable<TestTool> All => CompactMcpTools.DirectNames.Select(n => new TestTool(n))
            .Concat(new[] { new TestTool("probe"), new TestTool("wait"), new TestTool("execute_csharp", "Code", true) })
            .Concat(Enumerable.Range(0, 130).Select(i => new TestTool("rebar_tool_" + i, "Rebar")));
    }
    public static class ToolCatalog { public static string CategoryOf(TestTool tool) => tool.Category; }
    // Only the Revit dispatcher is replaced. HTTP parsing, session headers, schemas,
    // cancellation routing and turn binding use the real production server.
    public sealed class ToolDispatcher
    {
        public static ToolDispatcher Instance { get; } = new();
        public static TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static void PushSuppress() { }
        public static void PopSuppress() { }
        public async Task<string> ExecuteAsync(string name, IReadOnlyDictionary<string, JsonElement> input,
            CancellationToken ct, string documentKey)
        {
            if (name == "wait") { Started.TrySetResult(); await Task.Delay(TimeSpan.FromMinutes(1), ct); }
            if (name == "probe" && input.TryGetValue("model", out var model)) McpSession.ReportModel(model.GetString());
            return JsonSerializer.Serialize(new { client = McpSession.ClientName, model = McpSession.ReportedModel, document = documentKey, arguments = input });
        }
    }
}
