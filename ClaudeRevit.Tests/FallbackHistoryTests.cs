using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

// Server-side fallback and server compaction add assistant blocks the API validates by position;
// a restart must bring them back unchanged and in order.
public class FallbackHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cr-fallback-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void FallbackAndCompactionBlocksSurviveARestartInPlace()
    {
        const string marker = """{"type":"fallback","from":{"model":"claude-fable-5-1"},"to":{"model":"claude-opus-4-8"},"trigger":{"type":"refusal","category":"cyber"}}""";
        var history = new List<ApiTurn>
        {
            new() { Role = "user", Blocks = [new ChatTextBlock("Проверь модель")] },
            new() { Role = "assistant", Blocks = [new ChatCompactionBlock("summary", "enc"), new ChatFallbackBlock(marker), new ChatThinkingBlock("t", "sig"), new ChatTextBlock("Готово")] },
        };
        using (var ws = ConversationWorkspace.Acquire(_root, "project"))
            HistoryStore.Save(ws, [], history);
        using var again = ConversationWorkspace.Acquire(_root, "project");
        var restored = HistoryStore.LoadApiHistory(again);
        var blocks = restored[1].Blocks;
        Assert.Equal(new[] { typeof(ChatCompactionBlock), typeof(ChatFallbackBlock), typeof(ChatThinkingBlock), typeof(ChatTextBlock) }, blocks.Select(b => b.GetType()).ToArray());
        Assert.Equal(marker, ((ChatFallbackBlock)blocks[1]).Json);
        Assert.Equal(("summary", "enc"), (((ChatCompactionBlock)blocks[0]).Content, ((ChatCompactionBlock)blocks[0]).EncryptedContent));
    }
}
