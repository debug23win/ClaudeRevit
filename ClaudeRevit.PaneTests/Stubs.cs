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
        private string _scope = DocumentSessions.Scope;
        public static readonly Dictionary<string, List<ChatMessage>> Saved = new();
        public bool SubscriptionMode { get; set; }
        public Func<string, string, Task<bool>>? ConfirmToolAsync;
        public Action<int, int>? OnRound;
        public static McpAgentSelection? SentSelection;
        public static TaskCompletionSource? Pending;
        public Task SendAsync(ObservableCollection<ChatMessage> messages, string model, CancellationToken ct,
            string? image, string? mime, McpAgentSelection? selection)
        {
            SentSelection = selection;
            return Pending?.Task.WaitAsync(ct) ?? Task.CompletedTask;
        }
        public bool WorkspaceIsCurrent => _scope == DocumentSessions.Scope;
        public void SwitchWorkspace() => _scope = DocumentSessions.Scope;
        public List<ChatMessage> LoadUiMessages() => Saved.TryGetValue(_scope, out var saved) ? new(saved) : new();
        public void SaveHistory(IEnumerable<ChatMessage> messages) => Saved[_scope] = messages.ToList();
        public void ClearHistory() { }
        public void RecreateClient() { }
    }
    public static class CodexModelCatalog
    {
        public static Task<IReadOnlyList<CodexModel>> ReadAsync(string exe, string workDir, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CodexModel>>(new[] {
                new CodexModel("gpt-test", "Test model", "medium", new[] { "low", "medium", "high" }, true),
                new CodexModel("gpt-other", "Other model", "low", new[] { "low" }, false)
            });
    }
    public static class McpServer { public static string ClientWorkDir() => Environment.CurrentDirectory; }
    public static class DocumentSessions
    {
        public static string Scope = "a";
        public static event Action<bool>? Changed;
        public static void Change(string scope, bool managed) { Scope = scope; Changed?.Invoke(managed); }
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
    public static class Log { public static void Error(string message, Exception ex) => Console.Error.WriteLine(message + ": " + ex.Message); }
}
namespace ClaudeRevit.Tools { public static class DynamicToolLoader { public static void LoadAll() { } } }
namespace ClaudeRevit.UI
{
    public class SettingsWindow : Window { }
    public class BenchmarkWindow : Window { }
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
