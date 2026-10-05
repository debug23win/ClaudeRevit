// This probe compiles the real pane with an isolated in-memory backend. No Revit, account,
// network, user settings, saved conversations or inference calls are used.
using System.Collections.ObjectModel;
using System.Windows;
using ClaudeRevit.UI;

namespace ClaudeRevit.Services
{
    public static class SettingsStore
    {
        public static string ChatAgent { get; set; } = "api";
        public static string UiLanguage => "ru";
        public static string AltModel => "";
        public static string CodexExe => "codex";
        public static bool AllowCodeExecution => false;
        private static readonly Dictionary<string, McpAgentSelection> Choices = new();
        public static McpAgentSelection GetAgentSelection(string agent) => Choices.TryGetValue(agent, out var selection) ? selection : new(agent);
        public static void SaveAgentSelection(McpAgentSelection selection) => Choices[selection.Agent] = selection;
    }
    public sealed class ChatService
    {
        private string _scope;
        public ChatService(bool ephemeral = false, DocumentSessions.Snapshot? snapshot = null)
        { _scope = (snapshot ?? DocumentSessions.Current).Workspace.Identity; }
        public static readonly Dictionary<string, List<ChatMessage>> Saved = new();
        public bool SubscriptionMode { get; set; }
        public Func<string, string, Task<bool>>? ConfirmToolAsync;
        public Action<int, int>? OnRound;
        public Action<string>? OnStatus;
        public static McpAgentSelection? SentSelection;
        public static TaskCompletionSource? Pending;
        public readonly List<ChatRequest> Supplements = new();
        public bool Supplement(ChatRequest request) { Supplements.Add(request); return true; }
        public Task SendAsync(ObservableCollection<ChatMessage> messages, string model, CancellationToken ct,
            string? image = null, string? mime = null, McpAgentSelection? mcpSelection = null, IReadOnlyList<ChatAttachment>? attachments = null)
        {
            SentSelection = mcpSelection;
            return Pending?.Task.WaitAsync(ct) ?? Task.CompletedTask;
        }
        public bool WorkspaceIsCurrent => _scope == DocumentSessions.CurrentWorkspace.Identity;
        public bool WorkspaceMatches(DocumentSessions.Snapshot snapshot) => _scope == snapshot.Workspace.Identity;
        public void SwitchWorkspace(bool preserveHistory = false, DocumentSessions.Snapshot? snapshot = null) => _scope = (snapshot ?? DocumentSessions.Current).Workspace.Identity;
        public List<ChatMessage> LoadUiMessages() => Saved.TryGetValue(_scope, out var saved) ? new(saved) : new();
        public void SaveHistory(IEnumerable<ChatMessage> messages) => Saved[_scope] = messages.ToList();
        public void ClearHistory() { }
        public void RecreateClient() { }
        public void Activate() { }
    }
    public static class CodexModelCatalog
    {
        public static Task<IReadOnlyList<CodexModel>> ReadAsync(string exe, string workDir, CancellationToken ct, bool forceRefresh = false) =>
            Task.FromResult<IReadOnlyList<CodexModel>>(new[] {
                new CodexModel("gpt-test", "Test model", "medium", new[] { "low", "medium", "high" }, true),
                new CodexModel("gpt-other", "Other model", "low", new[] { "low" }, false)
            });
    }
    public static class McpServer { public static string ClientWorkDir() => Environment.CurrentDirectory; }
    public sealed class ConversationWorkspace : IDisposable
    {
        public string Identity { get; private init; } = "";
        public static ConversationWorkspace Acquire(string root, string identity) => new() { Identity = identity };
        public void Dispose() { }
    }
    public static class HistoryStore { public static List<ChatMessage> LoadUiMessages() => new(); }
    public static class UsageTracker
    {
        public static event Action? Updated;
        public static string Format() => "";
        public static void Reset() => Updated?.Invoke();
    }
    public static class SelectionService
    {
        public sealed record SelectionInfo(List<long> Ids, string Description);
        public static event Action<SelectionInfo>? Changed;
        public static void Notify() => Changed?.Invoke(Current);
        public static SelectionInfo Current => new(new(), "");
    }
    public static class UpdateChecker
    {
        public sealed record Result(bool UpdateAvailable, string? DownloadUrl, string? Latest);
        public static Task<Result> CheckAsync() => Task.FromResult(new Result(false, null, null));
    }
    public static class TextUtil { public static string Truncate(string text, int limit) => text.Length > limit ? text[..limit] : text; }
    public sealed class BenchmarkResult
    {
        public string RevitTime => "0.01s";
        public string TaskId { get; set; } = ""; public string Model { get; set; } = ""; public string Title { get; set; } = "";
        public string Verdict { get; set; } = "?"; public string Time { get; set; } = "0s"; public long Tokens { get; set; }
        public int? Quality { get; set; } public double? Speed { get; set; } public double? Score { get; set; } public double Seconds { get; set; }
    }
    public static class BenchmarkRunner
    {
        public static BenchmarkExecution? Execution, Judge;
        public static TaskCompletionSource Pending = new();
        public static Action<string>? Status;
        public static Action<BenchmarkResult>? Result;
        public static Task RunAsync(BenchmarkExecution execution, IReadOnlyList<BenchmarkTask> tasks, BenchmarkExecution judge, string runStamp,
            bool resetBetweenTasks, int maxRoundsPerTask, int maxSecondsPerTask, Action<string> onStatus, Action<BenchmarkResult> onResult, CancellationToken ct)
        { Execution = execution; Judge = judge; Status = onStatus; Result = onResult; return Pending.Task.WaitAsync(ct); }
    }
    public static class Log { public static string ReadTail()=>"test log"; public static void Error(string message, Exception ex) => Console.Error.WriteLine(message + ": " + ex.Message); }
}
namespace ClaudeRevit.Tools
{
    public static class DynamicToolLoader { public static void LoadAll() { } }
    public static class ToolContext { public static bool IsExecuting { get; set; } }
    public static class ToolDispatcher
    {
        public static event Action<string, string>? ProgressChanged;
        public static void NotifyProgress(string key, string stage) => ProgressChanged?.Invoke(key, stage);
    }
}
namespace ClaudeRevit.UI
{
    public class SettingsWindow : Window { }
    public class RunToolWindow : Window { }
    public static class PlainTextProp
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(PlainTextProp));
        public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);
        public static string GetText(DependencyObject target) => (string)target.GetValue(TextProperty);
    }
    public static class ElementIdLinker
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(ElementIdLinker));
        public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);
        public static string GetText(DependencyObject target) => (string)target.GetValue(TextProperty);
    }
    public static class MarkdownProps
    {
        public static readonly DependencyProperty MarkdownProperty = DependencyProperty.RegisterAttached("Markdown", typeof(string), typeof(MarkdownProps));
        public static void SetMarkdown(DependencyObject target, string value) => target.SetValue(MarkdownProperty, value);
        public static string GetMarkdown(DependencyObject target) => (string)target.GetValue(MarkdownProperty);
    }
}
