using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClaudeRevit.UI;

namespace ClaudeRevit.Services;

// Persists both the UI transcript (chat bubbles) and the full API conversation
// history (text + tool_use + tool_result blocks), so Claude keeps its working
// context — element IDs, tool results, decisions — across Revit restarts.
public static class HistoryStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeRevit", "conversation.json");

    public static void Save(IEnumerable<ChatMessage> uiMessages, IEnumerable<ApiTurn> apiHistory)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var dto = new FileDto(
                2,
                uiMessages.Select(m => new UiMessageDto(m.Role, m.ToolName, m.Text, m.IsError)).ToList(),
                apiHistory.Select(ToDto).ToList());
            File.WriteAllText(FilePath, JsonSerializer.Serialize(dto));
        }
        catch { /* best-effort */ }
    }

    public static List<ChatMessage> LoadUiMessages()
    {
        var dto = LoadFile();
        if (dto == null) return new();
        return dto.Ui.Select(d => new ChatMessage
        {
            Role = d.Role,
            ToolName = d.ToolName,
            Text = d.Text,
            IsError = d.IsError
        }).ToList();
    }

    public static List<ApiTurn> LoadApiHistory()
    {
        var dto = LoadFile();
        if (dto == null || dto.Api.Count == 0) return new();
        try
        {
            return Sanitize(dto.Api.Select(FromDto).ToList());
        }
        catch
        {
            return new();
        }
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { }
    }

    // The API rejects histories that don't start with a plain user turn, contain an
    // assistant tool_use without matching tool_results in the next user turn, or a
    // tool_result that doesn't answer a tool_use from the preceding assistant turn —
    // keep only the longest valid prefix.
    private static List<ApiTurn> Sanitize(List<ApiTurn> turns)
    {
        int start = 0;
        while (start < turns.Count &&
               (turns[start].Role != "user" || turns[start].Blocks.OfType<ChatToolResultBlock>().Any()))
            start++;
        turns = turns.Skip(start).ToList();

        var good = new List<ApiTurn>();
        for (int i = 0; i < turns.Count; i++)
        {
            var t = turns[i];
            if (t.Blocks.Count == 0) continue;

            // orphaned tool_result: must answer a tool_use from the previous kept turn
            var results = t.Blocks.OfType<ChatToolResultBlock>().Select(b => b.ToolUseId).ToList();
            if (results.Count > 0)
            {
                var prev = good.Count > 0 ? good[^1] : null;
                var uses = prev?.Blocks.OfType<ChatToolUseBlock>().Select(b => b.Id).ToHashSet()
                           ?? new HashSet<string>();
                if (t.Role != "user" || prev?.Role != "assistant" || !results.All(uses.Contains)) break;
            }

            // dangling tool_use: must be answered by the next turn
            var toolUses = t.Blocks.OfType<ChatToolUseBlock>().Select(b => b.Id).ToList();
            if (t.Role == "assistant" && toolUses.Count > 0)
            {
                var next = i + 1 < turns.Count ? turns[i + 1] : null;
                var nextResults = next?.Blocks.OfType<ChatToolResultBlock>().Select(b => b.ToolUseId).ToHashSet()
                                  ?? new HashSet<string>();
                if (next?.Role != "user" || !toolUses.All(nextResults.Contains)) break;
            }
            good.Add(t);
        }
        return good;
    }

    private static ApiMessageDto ToDto(ApiTurn t) => new(
        t.Role,
        t.Blocks.Select(b => b switch
        {
            ChatToolUseBlock x => new BlockDto("tool_use", null, x.Id, x.Name, x.InputJson, null, null, false, null, null),
            ChatToolResultBlock x => new BlockDto("tool_result", null, null, null, null, x.ToolUseId, x.Content, x.IsError, null, null),
            ChatThinkingBlock x => new BlockDto("thinking", null, null, null, null, null, null, false, x.Thinking, x.Signature),
            ChatRedactedThinkingBlock x => new BlockDto("redacted_thinking", null, null, null, null, null, null, false, x.Data, null),
            ChatTextBlock x => new BlockDto("text", x.Text, null, null, null, null, null, false, null, null),
            _ => new BlockDto("text", "", null, null, null, null, null, false, null, null)
        }).ToList());

    private static ApiTurn FromDto(ApiMessageDto d) => new()
    {
        Role = d.Role,
        Blocks = d.Blocks.Select(b => (ChatBlock)(b.Type switch
        {
            "tool_use" => new ChatToolUseBlock(b.Id ?? "", b.Name ?? "", b.InputJson ?? "{}"),
            "tool_result" => new ChatToolResultBlock(b.ToolUseId ?? "", b.Content ?? "", b.IsError),
            "thinking" => new ChatThinkingBlock(b.Thinking ?? "", b.Signature ?? ""),
            "redacted_thinking" => new ChatRedactedThinkingBlock(b.Thinking ?? ""),
            _ => (ChatBlock)new ChatTextBlock(b.Text ?? "")
        })).ToList()
    };

    private static FileDto? LoadFile()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var json = File.ReadAllText(FilePath);

            // v1 files were a bare array of UI messages — keep the transcript,
            // start the API history fresh
            if (json.TrimStart().StartsWith("["))
            {
                var legacy = JsonSerializer.Deserialize<List<UiMessageDto>>(json) ?? new();
                return new FileDto(1, legacy, new List<ApiMessageDto>());
            }
            return JsonSerializer.Deserialize<FileDto>(json);
        }
        catch
        {
            return null;
        }
    }

    private sealed record FileDto(int Version, List<UiMessageDto> Ui, List<ApiMessageDto> Api);
    private sealed record UiMessageDto(string Role, string? ToolName, string Text, bool IsError);
    private sealed record ApiMessageDto(string Role, List<BlockDto> Blocks);
    private sealed record BlockDto(
        string Type, string? Text, string? Id, string? Name,
        string? InputJson, string? ToolUseId, string? Content, bool IsError,
        string? Thinking, string? Signature);
}
