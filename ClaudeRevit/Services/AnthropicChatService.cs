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
    // (current document + selection) is appended as a trailing block on the LAST message, AFTER the
    // cache breakpoint, so the whole conversation prefix stays cached even when the selection changes.
    private const string BaseSystemPrompt =
        "You are Claude, integrated into Autodesk Revit 2027 as an AI assistant for architects and engineers. " +
        "You have tools to inspect AND modify the active model. Call them — don't narrate or ask permission.\n\n" +
        "TOOL CHOICE: Prefer a dedicated tool when one exists. For anything no dedicated tool covers, use " +
        "run_dynamo_python (runs Python via Dynamo, which manages its own transaction). It runs only when the " +
        "user has enabled code execution and approves each run, so do not reach for it lightly. If it is not " +
        "offered to you, code execution is disabled. Tell the user they can enable it via the gear icon.

" +
        "UNITS: All spatial inputs to tools are in feet (Revit's internal unit). Convert from user-given units " +
        "before calling: 1 m ≈ 3.28084 ft, 1 mm ≈ 0.00328084 ft, 1 in ≈ 0.0833333 ft.\n\n" +
        "CONVENTIONS: x = east, y = north. Plan coordinates only — z comes from the level. When the user is " +
        "vague about position, place geometry near the origin and pick sensible defaults. When they say " +
        "'this' / 'these' / 'the selected', call get_selection first.\n\n" +
        "MEMORY: When the user states a lasting preference or project standard, or corrects you in a way worth " +
        "remembering, call save_memory with one concise fact. Apply what you already remember (below) without " +
        "being reminded.\n\n" +
        "For destructive operations, briefly confirm with the user if intent is ambiguous. Otherwise just proceed. " +
        "If a tool returns an error, read it and adjust. All edits within one user prompt are bundled into a " +
        "single undo entry, so the user can ⌃Z to revert.";

    private const int MaxIterations = 24;
    private const int MaxOutputTokens = 8192;

    private const long CompactionThresholdTokens = 120_000;
    private const int CompactionKeepTailTurns = 3;
    private const string CompactionModel = "claude-haiku-4-5";

    private AnthropicClient? _client;
    private readonly List<ApiTurn> _history = HistoryStore.LoadApiHistory();
    private long _lastPromptTokens;

    // Set by the chat pane: asks the user to approve a tool call. Returns true to run it.
    public Func<string, string, Task<bool>>? ConfirmToolAsync;

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
        if (string.IsNullOrWhiteSpace(lastUser)) return;

        await CompactIfNeededAsync(client, conversation, ui, ct);

        // Dynamic-per-turn context (current document + selection). NOT cached — trails the prompt.
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

        var systemBlocks = BuildSystemBlocks();
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

                var effort = EffortFor(model);
                var parameters = new MessageCreateParams
                {
                    Model = ResolveModel(model),
                    MaxTokens = MaxOutputTokens,
                    Messages = BuildApiMessages(dynamicContext),
                    System = systemBlocks,
                    Tools = tools,
                    OutputConfig = effort != null ? new BetaOutputConfig { Effort = effort.Value } : null
                };

                var aggregated = await StreamOneTurnAsync(client, parameters, conversation, ui, ct);

                TrackUsage(model, aggregated);

                // Preserve blocks in order: thinking blocks must precede tool_use on replay.
                var assistantTurn = new ApiTurn { Role = "assistant" };
                var toolUseBlocks = new List<(string Id, string Name, IReadOnlyDictionary<string, JsonElement> Input)>();
                var sawText = false;
                foreach (var block in aggregated.Content)
                {
                    if (block.TryPickText(out var t))
                    {
                        assistantTurn.Blocks.Add(new ChatTextBlock(t.Text));
                        sawText = true;
                    }
                    else if (block.TryPickThinking(out var th))
                    {
                        assistantTurn.Blocks.Add(new ChatThinkingBlock(th.Thinking ?? "", th.Signature ?? ""));
                    }
                    else if (block.TryPickRedactedThinking(out var rt))
                    {
                        assistantTurn.Blocks.Add(new ChatRedactedThinkingBlock(rt.Data ?? ""));
                    }
                    else if (block.TryPickToolUse(out var tu))
                    {
                        assistantTurn.Blocks.Add(new ChatToolUseBlock(tu.ID, tu.Name, JsonSerializer.Serialize(tu.Input)));
                        toolUseBlocks.Add((tu.ID, tu.Name, tu.Input));
                    }
                }

                if (toolUseBlocks.Count == 0)
                {
                    if (assistantTurn.Blocks.Count > 0)
                        _history.Add(assistantTurn);
                    if (!sawText)
                    {
                        await ui.InvokeAsync(() => conversation.Add(new ChatMessage
                        {
                            Role = "assistant",
                            Text = "[The model returned no text — possibly a safety refusal. " +
                                   "Try rephrasing or switching the model.]"
                        }));
                    }
                    break;
                }

                var resultTurn = new ApiTurn { Role = "user" };
                foreach (var (id, name, inp) in toolUseBlocks)
                {
                    var tool = ToolRegistry.Instance.Get(name);

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

                    // Confirmation gate for destructive / arbitrary-code tools.
                    if (tool?.RequiresConfirmation == true && ConfirmToolAsync != null)
                    {
                        var approved = await ConfirmToolAsync(name, FormatInput(inp));
                        if (!approved)
                        {
                            await ui.InvokeAsync(() =>
                            {
                                toolMsg.Text = FormatInput(inp) + "\n⛔ denied by user";
                                toolMsg.IsError = true;
                            });
                            resultTurn.Blocks.Add(new ChatToolResultBlock(
                                id, "The user denied this operation. Do not retry it; ask how to proceed.", false));
                            continue;
                        }
                    }

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

    // System = BaseSystemPrompt (+ saved memory, if any). Both cached for 1h.
    private static List<BetaTextBlockParam> BuildSystemBlocks()
    {
        var blocks = new List<BetaTextBlockParam>
        {
            new BetaTextBlockParam
            {
                Text = BaseSystemPrompt,
                CacheControl = new BetaCacheControlEphemeral { Ttl = Ttl.Ttl1h }
            }
        };
        var memory = MemoryStore.Load();
        if (!string.IsNullOrWhiteSpace(memory))
        {
            blocks.Add(new BetaTextBlockParam
            {
                Text = "WHAT YOU REMEMBER (persisted notes from earlier sessions):\n" + memory,
                CacheControl = new BetaCacheControlEphemeral { Ttl = Ttl.Ttl1h }
            });
        }
        return blocks;
    }

    // Rebuilds the SDK message list from our own history model. The cache breakpoint sits
    // on the last block of the last stored message; the dynamic context is appended AFTER
    // it (uncached) so a changed selection never invalidates the cached conversation prefix.
    private List<BetaMessageParam> BuildApiMessages(string dynamicContext)
    {
        var msgs = new List<BetaMessageParam>(_history.Count);
        for (int i = 0; i < _history.Count; i++)
        {
            var turn = _history[i];
            bool isLast = i == _history.Count - 1;
            var blocks = new List<BetaContentBlockParam>(turn.Blocks.Count + 1);
            for (int j = 0; j < turn.Blocks.Count; j++)
            {
                var cache = isLast && j == turn.Blocks.Count - 1
                    ? new BetaCacheControlEphemeral { Ttl = Ttl.Ttl1h }
                    : null;
                blocks.Add(ToParam(turn.Blocks[j], cache));
            }
            if (isLast)
                blocks.Add(new BetaTextBlockParam { Text = dynamicContext }); // trailing, uncached
            msgs.Add(new BetaMessageParam
            {
                Role = turn.Role == "user" ? ApiRole.User : ApiRole.Assistant,
                Content = blocks
            });
        }
        return msgs;
    }

    private static BetaContentBlockParam ToParam(ChatBlock block, BetaCacheControlEphemeral? cache) => block switch
    {
        ChatTextBlock t => new BetaTextBlockParam { Text = t.Text, CacheControl = cache },
        ChatThinkingBlock th => new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature },
        ChatRedactedThinkingBlock rt => new BetaRedactedThinkingBlockParam { Data = rt.Data },
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

            TrackUsage("haiku-4-5", resp);
        }
        catch
        {
            return;
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

    private static List<BetaToolUnion> BuildToolDefs()
    {
        // Code-execution tools are hidden from Claude entirely unless the user opted in,
        // so they can't be invoked (or even suggested) by accident.
        var allowCode = SettingsStore.AllowCodeExecution;
        var allTools = ToolRegistry.Instance.All
            .Where(t => allowCode || !t.RequiresCodeExecutionOptIn)
            .ToList();
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

    // Effort is a direct cost/quality lever. Not supported on Haiku 4.5 — omit it there.
    private static Effort? EffortFor(string model) => model switch
    {
        "haiku-4-5" => null,
        "fable-5" => Effort.High,
        _ => Effort.Medium
    };

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
