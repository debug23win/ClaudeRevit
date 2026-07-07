using System.Collections.Generic;
using System.Linq;
using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

// Intra-task aging must trim OLD rounds' tool results while leaving the most recent ones intact,
// so the model still has what it just produced. (Alt path only in production — the logic is here.)
public class ToolResultAgingTests
{
    private static readonly string Big = new string('x', ToolResultAging.MinChars + 200);

    private static ApiTurn UserResult(string id) =>
        new() { Role = "user", Blocks = { new ChatToolResultBlock(id, Big, false) } };
    private static ApiTurn Assistant() =>
        new() { Role = "assistant", Blocks = { new ChatTextBlock("...") } };

    // task prompt, then three rounds each = assistant + a big result.
    private static List<ApiTurn> Build() => new()
    {
        new ApiTurn { Role = "user", Blocks = { new ChatTextBlock("task") } },
        Assistant(), UserResult("id1"),
        Assistant(), UserResult("id2"),
        Assistant(), UserResult("id3"),
    };

    private static bool IsAged(ApiTurn t) =>
        t.Blocks.OfType<ChatToolResultBlock>().Any(b => b.Content.Contains("[aged"));

    [Fact]
    public void AgeOlderTurns_keeps_the_last_two_result_turns_full()
    {
        var h = Build();
        var aged = ToolResultAging.AgeOlderTurns(h, keepTailUserTurns: 2);

        Assert.Equal(1, aged);                       // only id1's round is old enough
        Assert.True(IsAged(h[2]));                    // id1 → aged
        Assert.False(IsAged(h[4]));                   // id2 → intact (recent)
        Assert.False(IsAged(h[6]));                   // id3 → intact (just produced)
    }

    [Fact]
    public void AgeOlderTurns_is_idempotent_and_leaves_short_results_alone()
    {
        var h = Build();
        ToolResultAging.AgeOlderTurns(h, keepTailUserTurns: 2);
        var again = ToolResultAging.AgeOlderTurns(h, keepTailUserTurns: 2);
        Assert.Equal(0, again);                       // already aged → nothing more to do
    }

    [Fact]
    public void AgeAll_ages_every_oversized_result()
    {
        var h = Build();
        var aged = ToolResultAging.AgeAll(h);
        Assert.Equal(3, aged);
        Assert.True(IsAged(h[2]) && IsAged(h[4]) && IsAged(h[6]));
    }

    [Fact]
    public void AgeOlderTurns_with_large_keep_ages_nothing()
    {
        var h = Build();
        Assert.Equal(0, ToolResultAging.AgeOlderTurns(h, keepTailUserTurns: 10));
    }
}
