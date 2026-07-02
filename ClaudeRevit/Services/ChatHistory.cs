using System.Collections.Generic;

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

public sealed record ChatTextBlock(string Text) : ChatBlock;

// Thinking blocks must be replayed verbatim (text + signature) when the conversation
// continues on the same model — the API rejects modified or dropped thinking blocks
// that precede a tool_use.
public sealed record ChatThinkingBlock(string Thinking, string Signature) : ChatBlock;

public sealed record ChatRedactedThinkingBlock(string Data) : ChatBlock;

public sealed record ChatToolUseBlock(string Id, string Name, string InputJson) : ChatBlock;

public sealed record ChatToolResultBlock(string ToolUseId, string Content, bool IsError) : ChatBlock;
