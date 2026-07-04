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
    public static int AgeAll(System.Collections.Generic.IEnumerable<ApiTurn> history)
    {
        int aged = 0;
        foreach (var turn in history)
        {
            if (turn.Role != "user") continue; // tool results live in user-role turns
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
        }
        return aged;
    }
}
