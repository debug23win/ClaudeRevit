using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class ClaudeModelsTests
{
    [Theory]
    [InlineData("opus-5-5", "claude-opus-5-5")]
    [InlineData("sonnet-5-5", "claude-sonnet-5-5")]
    [InlineData("haiku-5-5", "claude-haiku-5-5")]
    [InlineData("fable-5-1", "claude-fable-5-1")]
    [InlineData("fable-5-2", "claude-fable-5-2")]
    [InlineData("claude-opus-5-5", "claude-opus-5-5")]
    [InlineData("auto", "claude-sonnet-5-5")]
    public void TagsMapToApiIds(string tag, string id) => Assert.Equal(id, ClaudeModels.ApiId(tag));

    // Served ids come back dashed or in the SDK's enum spelling; the newer ids must not fall
    // into an older family's rate.
    [Theory]
    [InlineData("claude-opus-5-5", "opus-5-5")]
    [InlineData("ClaudeOpus5_5", "opus-5-5")]
    [InlineData("claude-opus-5", "opus-5")]
    [InlineData("ClaudeOpus5", "opus-5")]
    [InlineData("claude-sonnet-5-5", "sonnet-5-5")]
    [InlineData("claude-haiku-5-5", "haiku-5-5")]
    [InlineData("claude-haiku-4-5", "haiku-4-5")]
    [InlineData("claude-fable-5-1", "fable-5-1")]
    [InlineData("claude-fable-5", "fable-5")]
    [InlineData("claude-opus-4-8", "opus-4-8")]
    [InlineData("claude-sonnet-5", "sonnet-5")]
    [InlineData("claude-fable-5-2", "fable-5-2")]
    [InlineData("claude-opus-4-5-20251101", "opus-4-5")]
    [InlineData("claude-sonnet-5-20260101", "sonnet-5")]
    public void ServedModelsMapToRateTags(string id, string tag) => Assert.Equal(tag, ClaudeModels.TagFromModel(id));

    // The pickers follow the Models API: Claude ids only, in the API's order, without repeats,
    // and a missing display name is derived from the id.
    [Fact]
    public void ServedCatalogBecomesPickerEntries()
    {
        var list = ClaudeModels.FromServed(new (string, string?)[]
        {
            ("claude-fable-5-2", "Claude Fable 5.2"), ("claude-opus-5-5", null), ("gpt-5", "GPT"),
            ("claude-opus-5-5", "dup"), ("claude-haiku-4-5", "Claude Haiku 4.5"),
        });
        Assert.Equal(new[] { "fable-5-2", "opus-5-5", "haiku-4-5" }, list.Select(m => m.Tag));
        Assert.Equal("Claude Opus 5.5", list[1].Name);
        Assert.Equal(new[] { "fable-5-2", "opus-5-5", "sonnet-5-5", "haiku-5-5" }, ClaudeModels.Current.Select(m => m.Tag));
    }

    [Theory]
    [InlineData("opus-5-5", true)]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("fable-5-2", true)]
    [InlineData("haiku-4-5", false)]
    [InlineData("opus-4-5-20251101", false)]
    [InlineData("sonnet-4-5", false)]
    public void AdaptiveThinkingFromThe46GenerationOn(string tag, bool expected) =>
        Assert.Equal(expected, ClaudeModels.SupportsAdaptiveThinking(tag));

    [Fact]
    public void PreservedThinkingModelsKeepAppendOnlyHistory()
    {
        foreach (var tag in new[] { "fable-5-2", "fable-5-1", "opus-5-5", "sonnet-5-5", "haiku-5-5", "claude-opus-5-5" })
            Assert.True(ClaudeModels.AppendOnlyHistory(tag), tag);
        foreach (var tag in new[] { "sonnet-5", "opus-5", "opus-4-8", "fable-5", "haiku-4-5", "auto" })
            Assert.False(ClaudeModels.AppendOnlyHistory(tag), tag);
    }

    [Fact]
    public void ServerFallbackOnlyWhereTheApiOffersIt()
    {
        Assert.True(ClaudeModels.SupportsServerFallback("opus-5-5"));
        Assert.True(ClaudeModels.SupportsServerFallback("sonnet-5-5"));
        Assert.False(ClaudeModels.SupportsServerFallback("haiku-5-5"));
        Assert.False(ClaudeModels.SupportsServerFallback("sonnet-5"));
    }

    [Fact]
    public void PricesMatchTheCurrentListAndUnknownTagsOverestimate()
    {
        Assert.True(ClaudeModels.TryPrice("opus-5", 1000, out var opus5));
        Assert.Equal(25m, ClaudeModels.Cost(opus5, 0, 1_000_000, 0, 0));
        Assert.True(ClaudeModels.TryPrice("sonnet-5", 1000, out var sonnet5));
        Assert.Equal(2m, ClaudeModels.Cost(sonnet5, 1_000_000, 0, 0, 0));
        // Fable 5.1 reads its cache at $0.25 per million, Opus 5.5 at $0.20.
        Assert.True(ClaudeModels.TryPrice("fable-5-1", 1000, out var fable51));
        Assert.Equal(0.25m, ClaudeModels.Cost(fable51, 0, 0, 0, 1_000_000));
        Assert.True(ClaudeModels.TryPrice("fable-5-2", 1000, out var fable52));
        Assert.Equal(40m, ClaudeModels.Cost(fable52, 0, 1_000_000, 0, 0));
        Assert.Equal(0.20m, ClaudeModels.Cost(fable52, 0, 0, 0, 1_000_000));
        Assert.True(ClaudeModels.TryPrice("opus-5-5", 1000, out var opus55));
        Assert.Equal(0.20m, ClaudeModels.Cost(opus55, 0, 0, 0, 1_000_000));
        // 1-hour cache writes are twice the base input rate.
        Assert.Equal(8m, ClaudeModels.Cost(opus55, 0, 0, 1_000_000, 0));
        Assert.False(ClaudeModels.TryPrice("mystery", 1000, out var unknown));
        Assert.Equal(50m, ClaudeModels.Cost(unknown, 0, 1_000_000, 0, 0));
    }

    [Fact]
    public void Haiku55IsPricedByEachRequestsPromptSize()
    {
        ClaudeModels.TryPrice("haiku-5-5", 100_000, out var small);
        ClaudeModels.TryPrice("haiku-5-5", 100_001, out var large);
        Assert.Equal(0.10m, ClaudeModels.Cost(small, 1_000_000, 0, 0, 0));
        Assert.Equal(0.50m, ClaudeModels.Cost(large, 1_000_000, 0, 0, 0));
        Assert.Equal(2.50m, ClaudeModels.Cost(large, 0, 1_000_000, 0, 0));
    }

    [Fact]
    public void StopNotesExplainAbnormalEndsOnly()
    {
        Assert.Null(ClaudeModels.StopNote("end_turn", null));
        Assert.Null(ClaudeModels.StopNote("tool_use", null));
        Assert.Null(ClaudeModels.StopNote(null, null));
        Assert.Contains("cyber", ClaudeModels.StopNote("refusal", "cyber"));
        Assert.Contains("not kept", ClaudeModels.StopNote("refusal", null));
        Assert.Contains("output limit", ClaudeModels.StopNote("max_tokens", null));
        Assert.Contains("context window", ClaudeModels.StopNote("model_context_window_exceeded", null));
    }

    [Fact]
    public void StrippingThinkingKeepsTextAndToolCallsAndDropsEmptiedTurns()
    {
        var history = new List<ApiTurn>
        {
            new() { Role = "user", Blocks = { new ChatTextBlock("Build a wall") } },
            new() { Role = "assistant", Blocks = { new ChatThinkingBlock("", "sig1"), new ChatToolUseBlock("t1", "create_wall", "{}") } },
            new() { Role = "user", Blocks = { new ChatToolResultBlock("t1", "ok", false) } },
            new() { Role = "assistant", Blocks = { new ChatRedactedThinkingBlock("x") } },
            new() { Role = "user", Blocks = { new ChatTextBlock("And a door") } },
            new() { Role = "assistant", Blocks = { new ChatThinkingBlock("plan", "sig2"), new ChatTextBlock("Done") } },
        };
        Assert.Equal(3, ChatHistoryEdits.StripThinking(history));
        Assert.Equal(5, history.Count);
        Assert.DoesNotContain(history.SelectMany(t => t.Blocks), b => b is ChatThinkingBlock or ChatRedactedThinkingBlock);
        Assert.IsType<ChatToolUseBlock>(history[1].Blocks.Single());
        Assert.Equal("Done", ((ChatTextBlock)history[^1].Blocks.Single()).Text);
    }
}
