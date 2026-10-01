using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ClaudeRevit.Services;
using ClaudeRevit.UI;
using Xunit;

namespace ClaudeRevit.Tests;

public class SessionIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClaudeRevit-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void SameProjectSimultaneouslyUsesDifferentHistoryAndCliFiles()
    {
        using var a = ConversationWorkspace.Acquire(_root, "project-a");
        using var b = ConversationWorkspace.Acquire(_root, "project-a");
        Assert.NotEqual(a.HistoryPath, b.HistoryPath);
        Assert.NotEqual(a.CodexSessionPath, b.CodexSessionPath);
        Assert.NotEqual(a.ClaudeSessionPath, b.ClaudeSessionPath);
        HistoryStore.Save(a, new[] { new ChatMessage { Role = "user", Text = "A" } }, new[] { new ApiTurn { Role = "user", Blocks = new() { new ChatTextBlock("A") } } });
        HistoryStore.Save(b, new[] { new ChatMessage { Role = "user", Text = "B" } }, new[] { new ApiTurn { Role = "user", Blocks = new() { new ChatTextBlock("B") } } });
        Assert.Equal("A", Assert.Single(HistoryStore.LoadUiMessages(a)).Text);
        Assert.Equal("B", Assert.Single(HistoryStore.LoadUiMessages(b)).Text);
        HistoryStore.Clear(a);
        Assert.Empty(HistoryStore.LoadUiMessages(a));
        Assert.Single(HistoryStore.LoadApiHistory(b));
    }

    [Fact]
    public void ReleasedSlotRestoresOnlyItsProjectAfterRestart()
    {
        string historyPath;
        using (var a = ConversationWorkspace.Acquire(_root, "a"))
        {
            historyPath = a.HistoryPath;
            HistoryStore.Save(a, new[] { new ChatMessage { Role = "assistant", Text = "saved", AssistantName = "Codex" } }, Array.Empty<ApiTurn>());
            ConversationWorkspace.AtomicWrite(a.CodexSessionPath, "session-a");
        }
        using var b = ConversationWorkspace.Acquire(_root, "b");
        Assert.Empty(HistoryStore.LoadUiMessages(b));
        using var restored = ConversationWorkspace.Acquire(_root, "a");
        Assert.Equal(historyPath, restored.HistoryPath);
        Assert.Equal("Codex", Assert.Single(HistoryStore.LoadUiMessages(restored)).AssistantName);
        Assert.Equal("session-a", File.ReadAllText(restored.CodexSessionPath));
    }

    [Fact]
    public void UnknownLegacyHistoryIsPreservedAndNeverAssignedToAnArbitraryProject()
    {
        Directory.CreateDirectory(_root);
        var legacy = Path.Combine(_root, "conversation.json");
        File.WriteAllText(legacy, "[old unscoped history]");
        using var scoped = ConversationWorkspace.Acquire(_root, "new project");
        Assert.Empty(HistoryStore.LoadUiMessages(scoped));
        Assert.Equal("[old unscoped history]", File.ReadAllText(legacy));
    }

    [Fact]
    public void AtomicWriteLeavesNoTemporaryFilesAndProjectIdentityCannotEscapeRoot()
    {
        using var scope = ConversationWorkspace.Acquire(_root, "../../outside:данные");
        ConversationWorkspace.AtomicWrite(scope.CodexSessionPath, "first");
        ConversationWorkspace.AtomicWrite(scope.CodexSessionPath, "second");
        Assert.StartsWith(Path.GetFullPath(_root), Path.GetFullPath(scope.DirectoryPath));
        Assert.Equal("second", File.ReadAllText(scope.CodexSessionPath));
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task CancelledQueuedJobNeverStarts()
    {
        using var cts = new CancellationTokenSource();
        using var job = new QueuedOperation<string>(cts.Token);
        cts.Cancel();
        Assert.False(job.TryStart());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.Task);
    }

    [Fact]
    public async Task CancellationWaitsForRunningJobToSettle()
    {
        using var cts = new CancellationTokenSource();
        using var job = new QueuedOperation<string>(cts.Token);
        Assert.True(job.TryStart());
        cts.Cancel();
        Assert.False(job.Task.IsCompleted);
        // Represents the Revit thread rolling back the current transaction.
        job.Completion.TrySetCanceled(cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.Task);
    }

    [Fact]
    public void SameRequestIdInDifferentClientsDoesNotShareCancellation()
    {
        using var a = new McpClientState("Claude", "1");
        using var b = new McpClientState("Codex", "1");
        using var ar = a.BeginRequest(JsonValue.Create(7)!, default, default);
        using var br = b.BeginRequest(JsonValue.Create(7)!, default, default);
        a.CancelRequest(JsonValue.Create(7));
        Assert.True(ar.Token.IsCancellationRequested);
        Assert.False(br.Token.IsCancellationRequested);
    }

    [Fact]
    public void StringAndNumericRequestIdsAndUnknownCancellationsAreDistinct()
    {
        using var client = new McpClientState("Codex", "1");
        using var numeric = client.BeginRequest(JsonValue.Create(1)!, default, default);
        using var text = client.BeginRequest(JsonValue.Create("1")!, default, default);
        client.CancelRequest(JsonValue.Create("missing")); client.CancelRequest(null);
        client.CancelRequest(JsonValue.Create("1"));
        Assert.True(text.Token.IsCancellationRequested);
        Assert.False(numeric.Token.IsCancellationRequested);
        Assert.Throws<InvalidOperationException>(() => client.BeginRequest(JsonValue.Create(1)!, default, default));
    }

    [Fact]
    public void IdentityAndModelDirectivesStayWithTheirClient()
    {
        using var a = new McpClientState("Claude", "1");
        using var b = new McpClientState("Codex", "1");
        a.ReportModel("claude-a"); b.ReportModel("gpt-b");
        a.RequestModel("claude-next");
        Assert.Null(b.TakeDirective());
        Assert.Contains("claude-next", a.TakeDirective());
        a.ReportModel("claude-next-preview");
        Assert.NotNull(a.RequestedModel);
        a.ReportModel("claude-next");
        Assert.Null(a.RequestedModel);
        Assert.Equal("gpt-b", b.ReportedModel);
    }

    [Fact]
    public async Task ClosingTurnRejectsLateCallsAndWaitsForRunningRevitWork()
    {
        var turn = McpTurnChannel.Open(default, "doc-a");
        using var call = turn.EnterCall();
        var close = turn.CloseAsync();
        Assert.True(turn.Token.IsCancellationRequested);
        Assert.Null(McpTurnChannel.Find(turn.Id));
        Assert.False(close.IsCompleted);
        Assert.Throws<OperationCanceledException>(() => turn.EnterCall());
        call.Dispose();
        await close;
    }

    [Fact]
    public async Task CancellingOneTurnLeavesAnotherTurnAvailable()
    {
        using var cts = new CancellationTokenSource();
        var a = McpTurnChannel.Open(cts.Token, "a");
        var b = McpTurnChannel.Open(default, "b");
        cts.Cancel();
        Assert.True(a.Token.IsCancellationRequested);
        using (b.EnterCall()) Assert.False(b.Token.IsCancellationRequested);
        await a.CloseAsync(); await b.CloseAsync();
    }
}
