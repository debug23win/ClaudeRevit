using System.Text.Json.Nodes;
using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public sealed class McpOperationReplayTests
{
    private static JsonObject Params(string args = "{}") => JsonNode.Parse("{\"name\":\"create_wall\",\"arguments\":" + args + "}")!.AsObject();

    [Fact]
    public async Task ReconnectedConcurrentCallsShareOneExecutionAndIndependentResults()
    {
        var receipts = new McpOperationReplay();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        async Task<(JsonNode?, JsonObject?)> Execute()
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return (new JsonObject { ["element_id"] = 42 }, null);
        }
        var first = receipts.ExecuteAsync("op-1", Params("{\"x\":1,\"nested\":{\"b\":2,\"a\":3}}"), Execute);
        var replay = receipts.ExecuteAsync("op-1", Params("{\"nested\":{\"a\":3,\"b\":2},\"x\":1}"), Execute);
        Assert.Equal(1, calls);
        release.SetResult();
        var a = await first; var b = await replay;
        Assert.Equal(42, b.value!["element_id"]!.GetValue<int>());
        a.value!["element_id"] = 99;
        Assert.Equal(42, b.value["element_id"]!.GetValue<int>());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task IdReuseWithDifferentCommandIsRejectedButNewIntentCanRun()
    {
        var receipts = new McpOperationReplay();
        int calls = 0;
        Task<(JsonNode?, JsonObject?)> Execute() { calls++; return Task.FromResult<(JsonNode?, JsonObject?)>((new JsonObject(), null)); }
        await receipts.ExecuteAsync("same", Params("{\"height\":3}"), Execute);
        await Assert.ThrowsAsync<ArgumentException>(() => receipts.ExecuteAsync("same", Params("{\"height\":4}"), Execute));
        await receipts.ExecuteAsync("next", Params("{\"height\":3}"), Execute);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("has space")] [InlineData("newline\n")] [InlineData("кириллица")]
    public async Task MissingOrInvalidIdentityNeverAdmitsTool(string? id)
    {
        var receipts = new McpOperationReplay();
        await Assert.ThrowsAsync<ArgumentException>(() => receipts.ExecuteAsync(id, Params(), () => throw new Exception("must not execute")));
    }

    [Fact]
    public async Task AnUncertainFailureIsNeverExecutedAgain()
    {
        var receipts = new McpOperationReplay();
        int calls = 0;
        Task<(JsonNode?, JsonObject?)> Execute() { calls++; throw new IOException("Response lost after write"); }
        await Assert.ThrowsAsync<IOException>(() => receipts.ExecuteAsync("failed", Params(), Execute));
        await Assert.ThrowsAsync<IOException>(() => receipts.ExecuteAsync("failed", Params(), Execute));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OversizedReceiptKeepsTombstoneInsteadOfExecutingAgain()
    {
        var receipts = new McpOperationReplay();
        int calls = 0;
        Task<(JsonNode?, JsonObject?)> Execute()
        { calls++; return Task.FromResult<(JsonNode?, JsonObject?)>((new JsonObject { ["data"] = new string('x', 8 * 1024 * 1024) }, null)); }
        Assert.NotNull((await receipts.ExecuteAsync("image", Params(), Execute)).value);
        var replay = await receipts.ExecuteAsync("image", Params(), Execute);
        Assert.Equal(-32001, replay.error!["code"]!.GetValue<int>());
        Assert.Equal(1, calls);
    }
}
