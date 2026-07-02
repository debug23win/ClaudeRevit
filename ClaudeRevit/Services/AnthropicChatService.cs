using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Anthropic;
using Anthropic.Core;
using Anthropic.Helpers;
using Anthropic.Models.Beta.Messages;
using ClaudeRevit.Tools;
using ClaudeRevit.UI;
using ApiRole = Anthropic.Models.Beta.Messages.Role;

namespace ClaudeRevit.Services;

public class AnthropicChatService
{
    // The BaseSystemPrompt is intentionally large and static — it gets cached via prompt caching
    // so we don't re-process it every turn. Same goes for the tools list. Dynamic per-turn context
    // (current document + selection) is appended as a SECOND system block that is NOT cached.
    private const string BaseSystemPrompt =
        "You are Claude, integrated into Autodesk Revit 2027 as an AI assistant for architects and engineers. " +
        "You have tools to inspect AND modify the active model. Call them — don't narrate or ask permission.\n\n" +
        "UNITS: All spatial inputs to tools are in feet (Revit's internal unit). Convert from user-given units " +
        "before calling: 1 m ≈ 3.28084 ft, 1 mm ≈ 0.00328084 ft, 1 in ≈ 0.0833333 ft.\n\n" +
        "CONVENTIONS: x = east, y = north. Plan coordinates only — z comes from the level. When the user is " +
        "vague about position, place geometry near the origin and pick sensible defaults. When they say " +
        "'this' / 'these' / 'the selected', call get_selection first.\n\n" +
        "For destructive operations (delete_elements affecting many items, set_parameter on critical fields), " +
        "briefly confirm with the user before acting if intent is ambiguous. Otherwise just proceed.\n\n" +
        "If a tool returns an error, read it and adjust — try a different level name, fix coordinates, etc. " +
        "All edits within one user prompt are bundled into a single undo entry, so the user can ⌃Z to revert.";

    private const int MaxIterations = 24;
    private const int MaxOutputTokens = 8192;

    // Client-side compaction: when the previous request's total prompt size crosses the
    // threshold, everything except the last few user turns is summarized into a single
    // message before the next request is sent.
    private const long CompactionThresholdTokens = 120_000;
    private const int CompactionKeepTailTurns = 3;
    private const string CompactionModel = "claude-haiku-4-5";

    private AnthropicClient? _client;
    private readonly List<ApiTurn> _history = HistoryStore.LoadApiHistory();
    private long _lastPromptTokens;

    public void RecreateClient() => _client = null;

    public void ClearHistory()
    {
        _history.Clear();
        _lastPromptTokens = 0;
        HistoryStore.Clear();
    }

    public void SaveHistory(IEnumerable<ChatMessage> uiMessages) =>
        HistoryStore.Save(uiMessages, _history);

    private AnthropicClient GetClient()
    {
        if (_client != null) return _client;
        var apiKey = ApiKeyStore.Load();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "API key is not set. Click the gear icon in the chat pane to enter your key.");
        return _client = new AnthropicClient(new ClientOptions { ApiKey = apiKey });
    }

    public async Task SendAsync(
        ObservableCollection<ChatMessage> conversation,
        string model,
        CancellationToken ct = default)
    {
        var ui = Dispatcher.CurrentDispatcher;
        var client = GetClient();

        var lastUser = conversation.LastOrDefault(m => m.Role == "user")?.Text ?? "";

        await CompactIfNeededAsync(client, conversation, ui, ct);

        // Dynamic-per-turn context (current document + selection). NOT cached.
        var contextJson = await ToolDispatcher.Instance.GetProjectContextAsync(ct);
        var dynamicContext = "CURRENT DOCUMENT:\n" + contextJson;

        var sel = SelectionService.Current;
        if (sel.Ids.Count > 0)
        {
            var idList = sel.Ids.Count > 30
                ? string.Join(", ", sel.Ids.Take(30)) + $", … +{sel.Ids.Count - 30} more"
                : string.Join(", ", sel.Ids);
            dynamicContext += $"\n\nCURRENT SELECTION: {sel.Description}. Element IDs: [{idList}]";
        }

        // System content: BaseSystemPrompt (CACHED, 1h) + dynamic context (NOT cached).
        var systemBlocks = new List<BetaTextBlockParam>
        {
            new BetaTextBlockParam
            {
                Text = BaseSystemPrompt,
                CacheControl = new BetaCacheControlEphemeral { Ttl = Ttl.Ttl1h }
            },
            new BetaTextBlockParam { Text = dynamicContext }
        };

        var tools = BuildToolDefs();

        _history.Add(new ApiTurn { Role = "user", Blocks = { new ChatTextBlock(lastUser) } });

        var turnLabel = "Claude: " + Truncate(lastUser, 60);

        await ToolDispatcher.Instance.BeginTurnAsync(turnLabel, ct);
        try
        {
            for (int iter = 0; ; iter++)
            {
                if (iter >= MaxIterations)
                {
                    await ui.InvokeAsync(() => conversation.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Text = $"[Stopped after {MaxIterations} tool-call rounds in one prompt. " +
                               "Say \"continue\" to keep going.]"
                    }));
                    break;
                }

                var parameters = new MessageCreateParams
                {
                    Model = ResolveModel(model),
                    MaxTokens = MaxOutputTokens,
                    Messages = BuildApiMessages(),
                    System = systemBlocks,
                    Tools = tools
                };

                var aggregated = await StreamOneTurnAsync(client, parameters, conversation, ui, ct);

                TrackUsage(model, aggregated);

                var aggregatedText = "";
                var toolUseBlocks = new List<(string Id, string Name, IReadOnlyDictionary<string, JsonElement> Input)>();
                foreach (var block in aggregated.Content)
                {
                    if (block.TryPickText(out var t))
                        aggregatedText += t.Text;
                    else if (block.TryPickToolUse(out var tu))
                        toolUseBlocks.Add((tu.ID, tu.Name, tu.Input));
                }

                if (toolUseBlocks.Count == 0)
                {
                    if (!string.IsNullOrEmpty(aggregatedText))
                    {
                        _history.Add(new ApiTurn { Role = "assistant", Blocks = { new ChatTextBlock(aggregatedText) } });
                    }
                    else
                    {
                        // e.g. a safety refusal on Fable 5 returns an empty content array
                        await ui.InvokeAsync(() => conversation.Add(new ChatMessage
                        {
                            Role = "assistant",
                            Text = "[The model returned no content — possibly a safety refusal. " +
                                   "Try rephrasing or switching the model.]"
                        }));
                    }
                    break;
                }

                var assistantTurn = new ApiTurn { Role = "assistant" };
                if (!string.IsNullOrEmpty(aggregatedText))
                    assistantTurn.Blocks.Add(new ChatTextBlock(aggregatedText));
                var resultTurn = new ApiTurn { Role = "user" };

                foreach (var (id, name, inp) in toolUseBlocks)
                {
                    assistantTurn.Blocks.Add(new ChatToolUseBlock(id, name, JsonSerializer.Serialize(inp)));

                    ChatMessage toolMsg = null!;
                    await ui.InvokeAsync(() =>
                    {
                        toolMsg = new ChatMessage
                        {
                            Role = "tool",
                            ToolName = name,
                            Text = FormatInput(inp) + "\n…running"
                        };
                        conversation.Add(toolMsg);
                    });

                    string content;
                    bool isError = false;
                    try
                    {
                        content = await ToolDispatcher.Instance.ExecuteAsync(name, inp, ct);
                        var display = FormatInput(inp) + "\n→ " + Truncate(content, 400);
                        await ui.InvokeAsync(() => toolMsg.Text = display);
                    }
                    catch (OperationCanceledException)
                    {
                        // Don't record a half-finished exchange: the assistant/result pair
                        // below is only appended once every tool in the round has run, so
                        // the history can never end on a dangling tool_use.
                        await ui.InvokeAsync(() =>
                        {
                            toolMsg.Text = FormatInput(inp) + "\n✗ cancelled";
                            toolMsg.IsError = true;
                        });
                        throw;
                    }
                    catch (Exception ex)
                    {
                        content = $"Error: {ex.Message}";
                        isError = true;
                        var display = $"{FormatInput(inp)}\n✗ {ex.Message}";
                        await ui.InvokeAsync(() =>
                        {
                            toolMsg.Text = display;
                            toolMsg.IsError = true;
                        });
                    }

                    resultTurn.Blocks.Add(new ChatToolResultBlock(id, content, isError));
                }

                _history.Add(assistantTurn);
                _history.Add(resultTurn);
            }
        }
        finally
        {
            await ToolDispatcher.Instance.EndTurnAsync(CancellationToken.None);
        }
    }

    private async Task<BetaMessage> StreamOneTurnAsync(
        AnthropicClient client,
        MessageCreateParams parameters,
        ObservableCollection<ChatMessage> conversation,
        Dispatcher ui,
        CancellationToken ct)
    {
        var aggregator = new BetaMessageContentAggregator();
        var stream = client.Beta.Messages.CreateStreaming(parameters, ct);

        ChatMessage? assistantBubble = null;

        await foreach (var ev in stream.CollectAsync(aggregator).WithCancellation(ct))
        {
            if (ev.TryPickContentBlockDelta(out var bd) &&
                bd.Delta.TryPickText(out var td) &&
                !string.IsNullOrEmpty(td.Text))
            {
                if (assistantBubble == null)
                {
                    var bubble = new ChatMessage { Role = "assistant", Text = "" };
                    assistantBubble = bubble;
                    await ui.InvokeAsync(() => conversation.Add(bubble));
                }
                var append = td.Text;
                var existing = assistantBubble;
                await ui.InvokeAsync(() => existing.Text += append);
            }
        }

        return aggregator.Message();
    }

    // Rebuilds the SDK message list from our own history model on every request.
    // The last block of the last message carries the cache breakpoint, so the whole
    // conversation prefix is served from cache on the next turn.
    private List<BetaMessageParam> BuildApiMessages()
    {
        var msgs = new List<BetaMessageParam>(_history.Count);
        for (int i = 0; i < _history.Count; i++)
        {
            var turn = _history[i];
            var blocks = new List<BetaContentBlockParam>(turn.Blocks.Count);
            for (int j = 0; j < turn.Blocks.Count; j++)
            {
                var cache = i == _history.Count - 1 && j == turn.Blocks.Count - 1
                    ? new BetaCacheControlEphemeral { Ttl = Ttl.Ttl1h }
                    : null;
                BetaContentBlockParam p = turn.Blocks[j] switch
                {
                    ChatTextBlock t => new BetaTextBlockParam { Text = t.Text, CacheControl = cache },
                    ChatToolUseBlock tu => new BetaToolUseBlockParam
                    {
                        ID = tu.Id,
                        Name = tu.Name,
                        Input = ParseInput(tu.InputJson),
                        CacheControl = cache
                    },
                    ChatToolResultBlock tr => new BetaToolResultBlockParam
                    {
                        ToolUseID = tr.ToolUseId,
                        Content = tr.Content,
                        IsError = tr.IsError,
                        CacheControl = cache
                    },
                    _ => throw new InvalidOperationException("Unknown history block type.")
                };
                blocks.Add(p);
            }
            msgs.Add(new BetaMessageParam
            {
                Role = turn.Role == "user" ? ApiRole.User : ApiRole.Assistant,
                Content = blocks
            });
        }
        return msgs;
    }

    // Summarizes everything except the last few user turns into one message when the
    // conversation outgrows the threshold. The cut always lands just before a plain-text
    // user turn, so assistant tool_use / tool_result pairs are never split.
    private async Task CompactIfNeededAsync(
        AnthropicClient client,
        ObservableCollection<ChatMessage> conversation,
        Dispatcher ui,
        CancellationToken ct)
    {
        if (_lastPromptTokens < CompactionThresholdTokens) return;

        var userTurns = new List<int>();
        for (int i = 0; i < _history.Count; i++)
            if (_history[i].Role == "user" && _history[i].Blocks.All(b => b is ChatTextBlock))
                userTurns.Add(i);
        if (userTurns.Count <= CompactionKeepTailTurns) return;
        int cut = userTurns[userTurns.Count - CompactionKeepTailTurns];
        if (cut <= 0) return;

        var transcript = RenderTranscript(_history.Take(cut));

        string summary;
        try
        {
            var resp = await client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = CompactionModel,
                MaxTokens = 2000,
                Messages = new List<BetaMessageParam>
                {
                    new BetaMessageParam
                    {
                        Role = ApiRole.User,
                        Content =
                            "Summarize this conversation between a user and an AI assistant working " +
                            "inside Autodesk Revit. Keep every fact needed to continue the work: what " +
                            "was created or modified, element IDs, level/view/type names, decisions " +
                            "made, unresolved errors and open tasks. Be dense; use bullet points.\n\n" +
                            transcript
                    }
                }
            }, ct);

            var sb = new StringBuilder();
            foreach (var b in resp.Content)
                if (b.TryPickText(out var t))
                    sb.Append(t.Text);
            summary = sb.ToString();
        }
        catch
        {
            return; // compaction is best-effort; keep the full history on failure
        }
        if (string.IsNullOrWhiteSpace(summary)) return;

        var removed = cut;
        _history.RemoveRange(0, cut);
        _history.Insert(0, new ApiTurn
        {
            Role = "user",
            Blocks =
            {
                new ChatTextBlock(
                    "[Summary of the earlier part of this conversation — older messages were " +
                    "compacted to save context:]\n" + summary)
            }
        });
        _lastPromptTokens = 0;

        await ui.InvokeAsync(() => conversation.Add(new ChatMessage
        {
            Role = "tool",
            ToolName = "compact_history",
            Text = $"Compacted {removed} older messages into a summary to stay within the context window."
        }));
    }

    private static string RenderTranscript(IEnumerable<ApiTurn> turns)
    {
        var sb = new StringBuilder();
        foreach (var turn in turns)
        {
            foreach (var block in turn.Blocks)
            {
                switch (block)
                {
                    case ChatTextBlock t:
                        sb.Append(turn.Role == "user" ? "USER: " : "ASSISTANT: ").AppendLine(t.Text);
                        break;
                    case ChatToolUseBlock tu:
                        sb.Append("TOOL CALL ").Append(tu.Name).Append(' ')
                          .AppendLine(Truncate(tu.InputJson, 300));
                        break;
                    case ChatToolResultBlock tr:
                        sb.Append("TOOL RESULT: ").AppendLine(Truncate(tr.Content, 300));
                        break;
                }
            }
        }
        // hard cap so the summarization request itself can't overflow
        var s = sb.ToString();
        return s.Length <= 400_000 ? s : s[^400_000..];
    }

    private void TrackUsage(string model, BetaMessage aggregated)
    {
        try
        {
            var u = aggregated.Usage;
            long cacheRead = 0, cacheCreation = 0;
            try { cacheRead = u.CacheReadInputTokens ?? 0; } catch { }
            try { cacheCreation = u.CacheCreationInputTokens ?? 0; } catch { }
            UsageTracker.Add(model, u.InputTokens, u.OutputTokens, cacheCreation, cacheRead);
            _lastPromptTokens = u.InputTokens + cacheRead + cacheCreation;
        }
        catch { /* non-fatal */ }
    }

    // Tools list is large and never changes. Mark the LAST tool with a cache breakpoint
    // so the whole tools array is cached together.
    private static List<BetaToolUnion> BuildToolDefs()
    {
        var allTools = ToolRegistry.Instance.All.ToList();
        var toolDefs = new List<BetaTool>(allTools.Count);
        for (int i = 0; i < allTools.Count; i++)
        {
            var t = allTools[i];
            toolDefs.Add(new BetaTool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = t.InputSchema,
                CacheControl = i == allTools.Count - 1
                    ? new BetaCacheControlEphemeral { Ttl = Ttl.Ttl1h }
                    : null
            });
        }
        return toolDefs.Select(t => (BetaToolUnion)t).ToList();
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseInput(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                   ?? new Dictionary<string, JsonElement>();
        }
        catch
        {
            return new Dictionary<string, JsonElement>();
        }
    }

    private static string ResolveModel(string model) => model switch
    {
        "opus-4-8" => "claude-opus-4-8",
        "fable-5" => "claude-fable-5",
        "haiku-4-5" => "claude-haiku-4-5",
        "sonnet-4-6" => "claude-sonnet-4-6",
        "opus-4-7" => "claude-opus-4-7",
        _ => "claude-sonnet-5"
    };

    private static string FormatInput(IReadOnlyDictionary<string, JsonElement> input)
    {
        if (input.Count == 0) return "(no arguments)";
        try { return JsonSerializer.Serialize(input, new JsonSerializerOptions { WriteIndented = false }); }
        catch { return "(unprintable input)"; }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + $"… ({s.Length - max} more chars)";
}
