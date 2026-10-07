using System.Collections.Generic;
using System.Linq;

namespace ClaudeRevit.Services;

// In-memory representation of the API conversation history. Kept independent of the
// Anthropic SDK types so it can be serialized to disk and rebuilt into request params
// (with a cache breakpoint on the last block) on every call.
public sealed class ApiTurn
{
    public string Role { get; init; } = "user"; // "user" | "assistant"
    public List<ChatBlock> Blocks { get; init; } = new();
}

public abstract record ChatBlock;

// Opaque provider reasoning is replayed only to the model that produced it.
public sealed record ChatOpenAIReasoningBlock(string Model, string ItemJson) : ChatBlock;

public sealed record ChatTextBlock(string Text) : ChatBlock;

// Thinking blocks must be replayed verbatim (text + signature) when the conversation
// continues on the same model — the API rejects modified or dropped thinking blocks
// that precede a tool_use.
public sealed record ChatThinkingBlock(string Thinking, string Signature) : ChatBlock;

public sealed record ChatRedactedThinkingBlock(string Data) : ChatBlock;

public sealed record ChatToolUseBlock(string Id, string Name, string InputJson) : ChatBlock;

// A server-side compaction summary (Anthropic compact-2026-01-12). Must be sent back exactly as
// received: the API uses it to stand in for the history it summarised.
public sealed record ChatCompactionBlock(string Content, string? EncryptedContent) : ChatBlock;

public sealed record ChatToolResultBlock(string ToolUseId, string Content, bool IsError) : ChatBlock;

// A user-attached image (base64-encoded), sent to a vision-capable model. MediaType is a MIME
// type like "image/png".
public sealed record ChatImageBlock(string MediaType, string Base64) : ChatBlock;

// One streamed assistant turn in backend-neutral form: content blocks plus token usage.
// Both the Anthropic and the OpenAI-compatible backend produce this, so the agentic tool
// loop in ChatService doesn't care which provider generated the turn.
public sealed class BackendTurn
{
    public List<ChatBlock> Blocks { get; } = new();
    public long InputTokens;
    public long OutputTokens;
    public long CacheCreationTokens;
    public long CacheReadTokens;

    // Advisor tool (Auto mode): how many times the executor consulted the advisor this response,
    // and the advisor sub-inference token usage (one entry per consult) so it can be billed at the
    // advisor model's own rate. Empty on every non-advisor turn.
    public int AdvisorConsults;
    public List<AdvisorUsage> AdvisorUsages { get; } = new();
}

public readonly record struct AdvisorUsage(
    string ModelTag, long InputTokens, long OutputTokens, long CacheCreationTokens, long CacheReadTokens);

// Block-order rules the API enforces on a user turn that answers tool calls.
public static class ChatBlockOrder
{
    // Every tool_result must come before any other block in the same user message. Images from
    // export_image/read_attachment used to be inserted right after their own result, so a round
    // with two calls produced [result, image, result] and a 400 — and because that turn was saved,
    // every later request failed the same way until the chat was cleared. Applied when the request
    // is built, so histories saved in the old order are repaired too. Stable within each group.
    public static List<ChatBlock> ToolResultsFirst(IReadOnlyList<ChatBlock> blocks)
    {
        if (!blocks.Any(b => b is ChatToolResultBlock)) return blocks.ToList();
        return blocks.Where(b => b is ChatToolResultBlock)
            .Concat(blocks.Where(b => b is not ChatToolResultBlock))
            .ToList();
    }

    // Images larger than this are not attached to the model's context (the file is still written).
    // A tool-result image rides in every later request of the conversation, so one oversized
    // export could push requests past the API size limit.
    public const int MaxInlineImageBase64 = 1_500_000;
}

