using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class OpenAIResponsesTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("https://api.openai.com.evil.example/v1", false)]
    [InlineData("http://localhost:1234/v1", false)]
    [InlineData("https://api.deepseek.com/v1", false)]
    public void RoutesOnlyOfficialOpenAI(string url, bool expected) =>
        Assert.Equal(expected, OpenAIResponses.UsesResponses(url));

    [Fact]
    public void ReplaysToolResultsAndOpaqueReasoningInOrder()
    {
        var tools = JsonNode.Parse("""[{"type":"function","function":{"name":"get_levels","parameters":{"type":"object","properties":{}}}}]""")!.AsArray();
        var history = new[] {
            new ApiTurn { Role = "assistant", Blocks = new() {
                new ChatOpenAIReasoningBlock("gpt-6-astra", """{"type":"reasoning","id":"rs_test","summary":[],"encrypted_content":"opaque"}"""),
                new ChatToolUseBlock("call_test", "get_levels", "{}") } },
            new ApiTurn { Blocks = new() { new ChatToolResultBlock("call_test", "[]", false) } }
        };
        var request = OpenAIResponses.BuildRequest("gpt-6-astra", "system", history, "context", tools);
        Assert.False(request["store"]!.GetValue<bool>());
        Assert.Equal("reasoning", request["input"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("function_call", request["input"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("call_test", request["input"]![2]!["call_id"]!.GetValue<string>());
        Assert.False(request["tools"]![0]!["strict"]!.GetValue<bool>());
        Assert.Equal("get_levels", request["tools"]![0]!["name"]!.GetValue<string>());
        Assert.NotNull(tools[0]!["function"]); // caller-owned schema wasn't mutated
        var switched = OpenAIResponses.BuildRequest("gpt-5-mini", "", history, "", tools);
        Assert.Equal("function_call", switched["input"]![0]!["type"]!.GetValue<string>());
    }

    private const string Completed = """
        {"status":"completed","output":[
          {"type":"reasoning","id":"rs_test","summary":[],"encrypted_content":"opaque"},
          {"type":"function_call","call_id":"call_test","name":"create_level","arguments":"{\"elevation_ft\":1}"}],
          "usage":{"input_tokens":100,"input_tokens_details":{"cached_tokens":30},"output_tokens":20}}
        """;

    [Fact]
    public async Task StreamingUsesCompletedArgumentsAndCountsCachedTokens()
    {
        var payload = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello\"}\n\n" +
            "data: {\"type\":\"response.completed\",\"response\":" + Completed + "}\n\n";
        // Each SSE data payload is one line in real transport.
        payload = payload.Replace(Completed, JsonNode.Parse(Completed)!.ToJsonString());
        using var client = new HttpClient(new Reply(payload));
        var text = new StringBuilder();
        var result = await OpenAIResponses.SendAsync("https://api.openai.com/v1", "test-key",
            OpenAIResponses.BuildRequest("gpt-6-astra", "", Array.Empty<ApiTurn>(), "", new()),
            delta => { text.Append(delta); return Task.CompletedTask; }, CancellationToken.None, client);
        Assert.Equal("Hello", text.ToString());
        Assert.Single(result.Blocks.OfType<ChatToolUseBlock>());
        Assert.Single(result.Blocks.OfType<ChatOpenAIReasoningBlock>());
        Assert.Equal(70, result.InputTokens);
        Assert.Equal(30, result.CacheReadTokens);
        Assert.Equal(20, result.OutputTokens);
    }

    [Theory]
    [InlineData("data: {\"type\":\"response.output_item.done\",\"item\":{\"type\":\"function_call\"}}\n\n")]
    [InlineData("data: {\"type\":\"response.incomplete\"}\n\n")]
    [InlineData("data: {\"type\":\"response.failed\"}\n\n")]
    public async Task IncompleteStreamNeverReturnsToolCalls(string payload)
    {
        using var client = new HttpClient(new Reply(payload));
        await Assert.ThrowsAnyAsync<Exception>(() => OpenAIResponses.SendAsync("https://api.openai.com/v1", "test-key",
            OpenAIResponses.BuildRequest("gpt-6-astra", "", Array.Empty<ApiTurn>(), "", new()),
            _ => Task.CompletedTask, CancellationToken.None, client));
    }

    [Fact]
    public void InvalidToolArgumentsAreRejected() => Assert.ThrowsAny<Exception>(() =>
        OpenAIResponses.ParseCompleted(JsonNode.Parse(Completed.Replace("{\\\"elevation_ft\\\":1}", "[1]"))!, "gpt-6-astra"));

    private sealed class Reply(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(payload, Encoding.UTF8, "text/event-stream") });
        }
    }
}
