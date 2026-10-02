using System.Collections.ObjectModel;
using System.Text.Json;
using ClaudeRevit.UI;

namespace ClaudeRevit.Services
{
    public static class DocumentSessions { public static string CurrentDocumentKey { get; set; } = "test-document"; }
    public static class SettingsStore { public static string ClaudeCodeExe => ""; public static string CodexExe => ""; }
    public static class McpServer { public static string ClientWorkDir() => Path.GetTempPath(); }
    public static class ClaudeCodeBackend
    {
        public static Task<string> CompleteAsync(string exe, string prompt, string dir, CancellationToken ct, string? model = null, string? effort = null) => throw new InvalidOperationException("No live subscription in unit tests.");
    }
    public sealed class ChatService
    {
        public ChatService(bool ephemeral) { }
        public bool SubscriptionMode { get; set; }
        public Action<int, int>? OnRound { get; set; }
        public string? LastRunError => null;
        public sealed record TaskMetrics(string Model, int Rounds, long InputTokens, long OutputTokens);
        public TaskMetrics? LastTask => new("test-model", 2, 100, 20);
        public static Func<CancellationToken, Task> Send { get; set; } = _ => Task.CompletedTask;
        public static string Grade { get; set; } = "{\"pass\":true,\"score\":90,\"reason\":\"objective test\"}";
        public Task SendAsync(ObservableCollection<ChatMessage> messages, string tag, CancellationToken ct, McpAgentSelection? mcpSelection) => Send(ct);
        public Task<string> RawCompleteAsync(string tag, string sys, string user, CancellationToken ct) => Task.FromResult(Grade);
    }
}
namespace ClaudeRevit.Tools
{
    public sealed class ToolDispatcher
    {
        public static ToolDispatcher Instance { get; } = new();
        public static bool ForceSuppress;
        public bool Family, ScopeActive;
        public int Starts, Ends;
        public List<string> Tools { get; } = new();
        public Task BenchmarkScopeAsync(bool begin, string key, CancellationToken ct)
        {
            if (begin) { ct.ThrowIfCancellationRequested(); ScopeActive = true; Starts++; }
            else { ScopeActive = false; Ends++; }
            return Task.CompletedTask;
        }
        public Task<string> BenchmarkProbeAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(JsonSerializer.Serialize(new { is_family_document = Family, total = 0, nested_seed_types = Array.Empty<object>() })); }
        public Task<string> ExecuteAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct, string documentKey)
        {
            Tools.Add(name);
            return Task.FromResult(name == "analyze_family_structure" ? "{\"nodes\":[],\"edges\":[]}" : "{\"restored\":true,\"results\":[]}");
        }
    }
}
