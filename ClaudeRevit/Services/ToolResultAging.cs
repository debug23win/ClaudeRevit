using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeRevit.Services;

// Headroom-style tool-result aging (github.com/headroomlabs-ai/headroom, ported ideas):
// in a long agentic session the dominant token cost is OLD tool results being replayed
// verbatim on every request, although the model consumed them in the round that
// produced them and almost never reads them again. So: a result is sent FULL during
// the turn it was produced in, and from the next user prompt onward it is truncated in
// place, with the original archived and retrievable via the get_full_result tool.
//
// Prompt-cache safety: an aged block never changes again (the length guard below makes
// aging idempotent), so the cached conversation prefix stays stable — only the tail
// blocks produced last turn flip once, exactly where the cache is being extended anyway.
public static class ToolResultAging
{
    // The aged replacement (KeepChars + truncation suffix + marker ≈ 550 chars) is
    // always shorter than MinChars — that inequality IS the idempotence guarantee.
    public const int MinChars = 700;
    public const int KeepChars = 400;

    // Ages every oversized tool result in the history IN PLACE. Returns how many blocks
    // were aged (used by tests; callers may ignore it).
    public static int AgeAll(IEnumerable<ApiTurn> history)
    {
        int aged = 0;
        foreach (var turn in history)
            if (turn.Role == "user") aged += AgeTurn(turn); // tool results live in user-role turns
        return aged;
    }

    // Intra-task aging (alt path only). During one long answer the tool results from ALL earlier
    // rounds pile up and — on providers with no prompt cache — are re-sent in full every round, so
    // a 20-round build re-bills 20 rounds of results at the last round (the L2 blow-up). Age the
    // results from OLDER rounds mid-loop, keeping the last `keepTailUserTurns` result-turns full so
    // the model still has what it just produced. Not used on the Claude path: there those old
    // results are cheap cache reads, and rewriting them mid-turn would only invalidate the cache.
    public static int AgeOlderTurns(IReadOnlyList<ApiTurn> history, int keepTailUserTurns)
    {
        // Protect the last N user-role turns (the recent results the model may still act on).
        var userTurnIdx = new List<int>();
        for (int i = 0; i < history.Count; i++)
            if (history[i].Role == "user") userTurnIdx.Add(i);
        var protectedIdx = new HashSet<int>(userTurnIdx.Skip(Math.Max(0, userTurnIdx.Count - keepTailUserTurns)));

        int aged = 0;
        for (int i = 0; i < history.Count; i++)
            if (history[i].Role == "user" && !protectedIdx.Contains(i)) aged += AgeTurn(history[i]);
        return aged;
    }

    private static int AgeTurn(ApiTurn turn)
    {
        int aged = 0;
        for (int i = 0; i < turn.Blocks.Count; i++)
        {
            if (turn.Blocks[i] is not ChatToolResultBlock tr) continue;
            if (tr.Content.Length <= MinChars) continue;

            ToolResultArchive.Record(tr.ToolUseId, tr.Content);
            turn.Blocks[i] = new ChatToolResultBlock(
                tr.ToolUseId,
                TextUtil.Truncate(tr.Content, KeepChars) +
                "\n[aged to save tokens — call get_full_result with id \"" + tr.ToolUseId +
                "\" if you truly need the full original output]",
                tr.IsError);
            aged++;
        }
        return aged;
    }
}
