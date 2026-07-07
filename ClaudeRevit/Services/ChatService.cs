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

// The agentic chat loop: history, tool execution, compaction. Provider-agnostic — each
// turn is streamed either through the Anthropic API (Claude models) or through any
// OpenAI-compatible endpoint (DeepSeek, Qwen, OpenRouter, local Ollama…, model tag "alt")
// into a common BackendTurn, and everything below that point is shared.
public class ChatService
{
    // The system prompt is intentionally large and static — on the Anthropic path it gets
    // cached via prompt caching so we don't re-process it every turn. Same goes for the
    // tools list. Dynamic per-turn context (current document + selection) is appended as a
    // trailing block on the LAST message, AFTER the cache breakpoint, so the whole
    // conversation prefix stays cached even when the selection changes.
    private const string SystemPromptBody =
        "You have tools to inspect AND modify the active model. Call them — don't narrate or ask permission.\n\n" +
        "TOOL CHOICE: Prefer a dedicated tool when one exists. For anything no dedicated tool covers, the " +
        "DEFAULT escape hatch is execute_csharp: C# directly against the Revit API, no Dynamo dependency, " +
        "runs inside a managed transaction that rolls back automatically on error. Use run_dynamo_python " +
        "only when Python is specifically better — a proven Python snippet from get_script_journal, code " +
        "adapted from the Dynamo community, or the user asked for Python. Both run only when the user has " +
        "enabled code execution, so do not reach for them lightly. If they are not offered to you, code " +
        "execution is disabled. Tell the user they can enable it via the gear icon. " +
        "UNITS: All spatial inputs to tools are in feet (Revit's internal unit). Convert from user-given units " +
        "before calling: 1 m ≈ 3.28084 ft, 1 mm ≈ 0.00328084 ft, 1 in ≈ 0.0833333 ft.\n\n" +
        "FAMILY EDITOR: when a family (.rfa) is open for editing, prefer the dedicated family tools " +
        "(get_family_parameters, add_family_parameter, set_family_parameter_formula, " +
        "set_family_parameter_value, set_family_parameter_instance, associate_family_parameter, " +
        "create_linear_array, create_family_dimension) over execute_csharp — they are far faster and " +
        "need no code opt-in. list_family_instances lists placed instances (family/type, position mm, " +
        "group); list_family_dimensions shows which dimensions drive which parameters; " +
        "list_reference_planes lists planes by axis + position; get_dependent_elements shows what depends " +
        "on an element (before deleting / to diagnose a failed delete); get_family_parameters also reports " +
        "errored_count (family health); get_element_locations reads positions/bboxes in mm. Family " +
        "length values in these tools are in millimetres; formulas use Revit's own syntax.\n\n" +
        "CONVENTIONS: x = east, y = north. Plan coordinates only — z comes from the level. When the user is " +
        "vague about position, place geometry near the origin and pick sensible defaults. When they say " +
        "'this' / 'these' / 'the selected', call get_selection first. When writing code against the Revit " +
        "2027 API: ElementId.Value (long) — ElementId.IntegerValue was removed.\n\n" +
        "MEMORY: When the user states a lasting preference or project standard, or corrects you in a way worth " +
        "remembering, call save_memory with one concise fact. Apply what you already remember (below) without " +
        "being reminded.\n\n" +
        "LEARNING: Scripts that worked before are journaled on disk and survive clearing the chat. Proven patterns " +
        "may be listed below — reuse them. get_script_journal shows full past runs; generate_diagnostic_report " +
        "summarizes recurring scripts as candidates for future dedicated tools (also written automatically when " +
        "Revit closes). PROMOTE ON THE SECOND USE: the first time you solve something with execute_csharp, " +
        "just run it. But when you are about to run essentially the SAME operation a second time — the same " +
        "kind of script you already ran successfully this session, or one shown in the proven-scripts list — " +
        "that is a reusable pattern: generalize it (turn the specific ids/sizes/names into input parameters) " +
        "and call save_tool ONCE to compile it into a persistent named tool, then call that tool for this and " +
        "every later use. A just-saved tool is available immediately, in the same turn. Do NOT promote a " +
        "genuine one-off, and delete_tool removes a bad one.\n\n" +
        "AGED RESULTS: To save tokens, tool results from earlier prompts are shown truncated with an " +
        "'[aged to save tokens …]' marker carrying an id. This is normal. If you genuinely need a full old " +
        "result AND it cannot have changed, call get_full_result with that id; if the model may have changed " +
        "since, re-query the live model instead.\n\n" +
        "For destructive operations, briefly confirm with the user if intent is ambiguous. Otherwise just proceed. " +
        "If a tool returns an error, read it and adjust. All edits within one user prompt are bundled into a " +
        "single undo entry, so the user can ⌃Z to revert.";

    private const string AnthropicPromptPrefix =
        "You are Claude, integrated into Autodesk Revit 2027 as an AI assistant for architects and engineers. ";

    private const string AltPromptPrefix =
        "You are an AI assistant integrated into Autodesk Revit 2027 to help architects and engineers. ";

    // Non-Claude models are generally shakier at tool use — spell the contract out.
    private const string AltPromptSuffix =
        "\n\nIMPORTANT: When an action is needed, respond with a tool call whose arguments are valid JSON " +
        "matching the tool's schema exactly. Never describe a tool call in plain text instead of making it.";

    // Default cap on tool-call rounds within a single user prompt; overridable in Settings.
    private const int DefaultMaxIterations = 24;
    private const int MaxOutputTokens = 8192;

    // Alt providers span 8K local models to 1M Gemini — when the user entered the model's
    // context size in Settings, compact at ~75% of it; otherwise assume a small context.
    private const long CompactionThresholdTokens = 120_000;
    private const long AltCompactionThresholdTokens = 48_000;

    private static long AltThreshold()
    {
        var contextK = SettingsStore.AltContextK;
        return contextK > 0 ? contextK * 1000L * 3 / 4 : AltCompactionThresholdTokens;
    }
    private const int CompactionKeepTailTurns = 3;
    private const string CompactionModel = "claude-haiku-4-5";

    private AnthropicClient? _client;
    private readonly OpenAIBackend _openAI = new();
    private readonly List<ApiTurn> _history = HistoryStore.LoadApiHistory();
    private long _lastPromptTokens;

    public ChatService()
    {
        // A restored long history must be eligible for compaction on the very FIRST send
        // after a restart — otherwise an oversized persisted conversation is replayed
        // uncompacted into a small-context alt model and every request fails.
        long chars = 0;
        foreach (var turn in _history)
            foreach (var block in turn.Blocks)
                chars += block switch
                {
                    ChatTextBlock b => b.Text.Length,
                    ChatToolUseBlock b => b.InputJson.Length,
                    ChatToolResultBlock b => b.Content.Length,
                    ChatThinkingBlock b => b.Thinking.Length,
                    _ => 0
                };
        _lastPromptTokens = chars / 4;
    }

    // Set by the chat pane: asks the user to approve a tool call. Returns true to run it.
    public Func<string, string, Task<bool>>? ConfirmToolAsync;

    public void RecreateClient() => _client = null;

    public void ClearHistory()
    {
        // Clears only the conversation. The learning layer (ScriptJournal, MemoryStore,
        // ExperienceStore digest) lives in its own files under %AppData%\ClaudeRevit and is
        // deliberately NOT touched here — accumulated experience must outlive a window clear,
        // and the digest already sits in the (session-stable) system prompt.
        _history.Clear();
        _lastPromptTokens = 0;
        HistoryStore.Clear();
    }

    public void SaveHistory(IEnumerable<ChatMessage> uiMessages) =>
        HistoryStore.Save(uiMessages, _history);

    private static bool IsAlt(string model) => model == "alt";

    private AnthropicClient GetClient()
    {
        if (_client != null) return _client;
        var apiKey = ApiKeyStore.Load();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Anthropic API key is not set. Click the gear icon to enter your key — or " +
                "pick the alternative model in the dropdown if you configured one.");
        return _client = new AnthropicClient(new ClientOptions { ApiKey = apiKey });
    }

    public async Task SendAsync(
        ObservableCollection<ChatMessage> conversation,
        string model,
        CancellationToken ct = default)
    {
        var ui = Dispatcher.CurrentDispatcher;
        bool alt = IsAlt(model);
        if (alt && !OpenAIBackend.IsConfigured)
            throw new InvalidOperationException(
                "The alternative model is not configured. Open Settings (gear icon) and fill " +
                "in the provider's base URL and model id (DeepSeek, Qwen, OpenRouter, Ollama…).");
        // Fail fast BEFORE the user turn is committed to _history: a missing-key error
        // thrown mid-loop would leave the prompt dangling in the saved history, to be
        // silently replayed (and acted on!) once a key appears.
        if (!alt) GetClient();

        var lastUser = conversation.LastOrDefault(m => m.Role == "user")?.Text ?? "";
        if (string.IsNullOrWhiteSpace(lastUser)) return;

        await CompactIfNeededAsync(conversation, ui, alt, ct);

        // Tool-result aging (Headroom-style): every tool result from a PRIOR user prompt
        // is truncated in place and its original archived (get_full_result retrieves it).
        // Run here — before the new user turn is added — so results from the turn just
        // finished are still full when the model consumed them, but stop being replayed
        // verbatim from now on. This is the single biggest token sink in long sessions.
        ToolResultAging.AgeAll(_history);

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

        // Everything constant for the whole turn is built ONCE here, not per loop
        // iteration: rebuilding the system blocks mid-turn would let a save_memory call
        // invalidate the 1h prompt-cache prefix for the rest of the turn, and rebuilding
        // the tool list would let a Settings change yank tools out from under the model
        // mid-plan (plus ~130 tool schemas re-serialized per round for nothing).
        var allowedTools = AllowedTools();
        List<BetaTextBlockParam>? systemBlocks = null;
        List<BetaToolUnion>? toolDefs = null;
        string? altSystem = null;
        System.Text.Json.Nodes.JsonArray? altTools = null;
        if (alt)
        {
            altSystem = BuildAltSystemPrompt();
            altTools = OpenAIBackend.BuildTools(allowedTools);
        }
        else
        {
            systemBlocks = BuildSystemBlocks();
            toolDefs = BuildToolDefs(allowedTools);
        }

        _history.Add(new ApiTurn { Role = "user", Blocks = { new ChatTextBlock(lastUser) } });

        var turnLabel = "Claude: " + Truncate(lastUser, 60);

        // Read once per prompt so a mid-turn Settings change doesn't shift the cap.
        var maxIterations = SettingsStore.MaxToolRounds;

        // Auto-escalation to a stronger "reasoning" model on the alt path: keep the fast model
        // for routine work, switch to the reasoning model when the task looks hard up front, or
        // once the fast model starts erroring / the task turns into a deep multi-step loop.
        var reasoningModel = alt ? SettingsStore.AltReasoningModel : "";
        var canEscalate = !string.IsNullOrWhiteSpace(reasoningModel);
        var escalate = canEscalate && LooksComplex(lastUser);
        const int EscalateAfterRounds = 6;

        await ToolDispatcher.Instance.BeginTurnAsync(turnLabel, ct);
        try
        {
            for (int iter = 0; ; iter++)
            {
                if (iter >= maxIterations)
                {
                    await ui.InvokeAsync(() => conversation.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Text = $"[Stopped after {maxIterations} tool-call rounds in one prompt. " +
                               "Say \"continue\" to keep going, or raise the limit in Settings (gear icon).]"
                    }));
                    break;
                }

                var altModelForTurn = escalate ? reasoningModel : null;
                var turn = alt
                    ? await StreamAltTurnAsync(altSystem!, altTools!, dynamicContext, conversation, ui, ct, altModelForTurn)
                    : await StreamAnthropicTurnAsync(model, systemBlocks!, toolDefs!, dynamicContext, conversation, ui, ct);

                TrackUsage(alt ? "alt:" + (altModelForTurn ?? SettingsStore.AltModel) : model, turn);

                // Preserve blocks in order: thinking blocks must precede tool_use on replay.
                var assistantTurn = new ApiTurn { Role = "assistant" };
                var toolUses = new List<ChatToolUseBlock>();
                var sawText = false;
                foreach (var block in turn.Blocks)
                {
                    assistantTurn.Blocks.Add(block);
                    if (block is ChatTextBlock) sawText = true;
                    if (block is ChatToolUseBlock tu) toolUses.Add(tu);
                }

                if (toolUses.Count == 0)
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
                // A successful save_tool/delete_tool changes the registered tool set; when it
                // does we rebuild the tool list at the end of this round so a just-created
                // tool is callable on the very next round of the SAME turn (not just the next
                // message). Rare, so the one-off cache re-warm it costs is fine.
                var toolSetChanged = false;
                foreach (var use in toolUses)
                {
                    var name = use.Name;

                    // Invalid-JSON arguments (typically an alt model whose output was cut
                    // by the length limit mid-arguments) must NOT run the tool with an
                    // empty input — that either errors misleadingly or executes with
                    // defaults instead of what the model intended.
                    var inp = TryParseInput(use.InputJson);
                    if (inp == null)
                    {
                        await ui.InvokeAsync(() => conversation.Add(new ChatMessage
                        {
                            Role = "tool",
                            ToolName = name,
                            Text = "✗ arguments were not valid JSON (output truncated?) — not executed",
                            IsError = true
                        }));
                        resultTurn.Blocks.Add(new ChatToolResultBlock(
                            use.Id,
                            "The tool-call arguments were not valid JSON — your output was probably cut " +
                            "off by the length limit. The tool was NOT executed. Re-issue the call with " +
                            "more compact arguments (e.g. split the work into smaller steps).",
                            true));
                        continue;
                    }

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

                    // Confirmation gate for destructive / arbitrary-code tools — only when
                    // the user re-enabled it in settings (off by default: every turn is one
                    // undo step, and code execution has its own opt-in).
                    if (SettingsStore.ConfirmOperations && tool?.RequiresConfirmation == true && ConfirmToolAsync != null)
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
                                use.Id, "The user denied this operation. Do not retry it; ask how to proceed.", false));
                            continue;
                        }
                    }

                    string content;
                    bool isError = false;
                    try
                    {
                        content = await ToolDispatcher.Instance.ExecuteAsync(name, inp, ct);
                        var display = FormatInput(inp) + "\n→ " + Truncate(FormatResult(content), 400);
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

                    resultTurn.Blocks.Add(new ChatToolResultBlock(use.Id, content, isError));
                    if (!isError && (name == "save_tool" || name == "delete_tool"))
                        toolSetChanged = true;
                }

                _history.Add(assistantTurn);
                _history.Add(resultTurn);

                // Make a freshly saved/removed tool visible to the model for the rest of this
                // turn. Rebuild only the tool list (not the system blocks) so the memory /
                // experience prompt cache is untouched.
                if (toolSetChanged)
                {
                    allowedTools = AllowedTools();
                    if (alt) altTools = OpenAIBackend.BuildTools(allowedTools);
                    else toolDefs = BuildToolDefs(allowedTools);
                }

                // Escalate to the reasoning model for the rest of the turn once the fast model
                // hits a tool error / Revit rollback, or the task has become a deep multi-step
                // loop. Sticky: once escalated we stay on the reasoning model.
                if (canEscalate && !escalate)
                {
                    var anyToolError = resultTurn.Blocks.OfType<ChatToolResultBlock>().Any(b => b.IsError);
                    if (anyToolError || iter >= EscalateAfterRounds - 1)
                        escalate = true;
                }
            }
        }
        finally
        {
            await ToolDispatcher.Instance.EndTurnAsync(CancellationToken.None);
        }
    }

    // Cheap heuristic for "this looks like a hard, multi-step or diagnostic task" — used only
    // to pick the reasoning model up front on the alt path. Escalation on errors/depth covers
    // whatever this misses.
    private static readonly string[] ComplexHints =
    {
        // en
        "calculate", "compute", "layout", "optimi", "debug", "why ", "diagnose", "reinforc",
        "constraint", "formula", "parametric", "step by step", "figure out", "plan ",
        // ru
        "рассчита", "посчита", "раскладк", "оптимиз", "отлад", "почему", "диагно", "армир",
        "хомут", "формул", "параметр", "по шагам", "разберись", "спроектир", "зон"
    };

    private static bool LooksComplex(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        if (prompt.Length > 400) return true; // long, detailed asks tend to be multi-step
        var p = prompt.ToLowerInvariant();
        return ComplexHints.Any(h => p.Contains(h));
    }

    // ---- Anthropic path -----------------------------------------------------------

    private async Task<BackendTurn> StreamAnthropicTurnAsync(
        string model,
        List<BetaTextBlockParam> systemBlocks,
        List<BetaToolUnion> toolDefs,
        string dynamicContext,
        ObservableCollection<ChatMessage> conversation,
        Dispatcher ui,
        CancellationToken ct)
    {
        var client = GetClient();
        var effort = EffortFor(model);
        var parameters = new MessageCreateParams
        {
            Model = ResolveModel(model),
            MaxTokens = MaxOutputTokens,
            Messages = BuildApiMessages(dynamicContext),
            System = systemBlocks,
            Tools = toolDefs,
            OutputConfig = effort != null ? new BetaOutputConfig { Effort = effort.Value } : null
        };

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

        return ToBackendTurn(aggregator.Message());
    }

    private static BackendTurn ToBackendTurn(BetaMessage message)
    {
        var turn = new BackendTurn();
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out var t))
                turn.Blocks.Add(new ChatTextBlock(t.Text));
            else if (block.TryPickThinking(out var th))
                turn.Blocks.Add(new ChatThinkingBlock(th.Thinking ?? "", th.Signature ?? ""));
            else if (block.TryPickRedactedThinking(out var rt))
                turn.Blocks.Add(new ChatRedactedThinkingBlock(rt.Data ?? ""));
            else if (block.TryPickToolUse(out var tu))
                turn.Blocks.Add(new ChatToolUseBlock(tu.ID, tu.Name, JsonSerializer.Serialize(tu.Input)));
        }
        try
        {
            var u = message.Usage;
            turn.InputTokens = u.InputTokens;
            turn.OutputTokens = u.OutputTokens;
            try { turn.CacheReadTokens = u.CacheReadInputTokens ?? 0; } catch { }
            try { turn.CacheCreationTokens = u.CacheCreationInputTokens ?? 0; } catch { }
        }
        catch { /* non-fatal */ }
        return turn;
    }

    // ---- OpenAI-compatible path ---------------------------------------------------

    private async Task<BackendTurn> StreamAltTurnAsync(
        string systemPrompt,
        System.Text.Json.Nodes.JsonArray toolsJson,
        string dynamicContext,
        ObservableCollection<ChatMessage> conversation,
        Dispatcher ui,
        CancellationToken ct,
        string? modelOverride = null)
    {
        ChatMessage? assistantBubble = null;
        // Deltas arrive on the HTTP read thread; awaiting the dispatcher operation gives
        // backpressure — a fast local provider can't flood Revit's UI thread (same
        // discipline as the Anthropic path).
        Task OnDelta(string piece) => ui.InvokeAsync(() =>
        {
            if (assistantBubble == null)
            {
                assistantBubble = new ChatMessage { Role = "assistant", Text = "" };
                conversation.Add(assistantBubble);
            }
            assistantBubble.Text += piece;
        }).Task;

        return await _openAI.StreamTurnAsync(
            systemPrompt, _history, dynamicContext, toolsJson, OnDelta, ct, modelOverride);
    }

    // The alt system prompt: same body as the Anthropic one, different framing, plus the
    // shared memory section (single-sourced in MemorySection so the two backends can't
    // silently drift apart).
    private static string BuildAltSystemPrompt()
    {
        var sys = new StringBuilder(AltPromptPrefix).Append(SystemPromptBody).Append(AltPromptSuffix);
        if (MemorySection() is { } memory)
            sys.Append("\n\n").Append(memory);
        if (ExperienceStore.Digest() is { } experience)
            sys.Append("\n\n").Append(experience);
        return sys.ToString();
    }

    private const string MemoryHeader = "WHAT YOU REMEMBER (persisted notes from earlier sessions):\n";

    private static string? MemorySection()
    {
        var memory = MemoryStore.Load();
        return string.IsNullOrWhiteSpace(memory) ? null : MemoryHeader + memory;
    }

    // ---- Shared helpers -----------------------------------------------------------

    private static List<IRevitTool> AllowedTools()
    {
        // Code-execution tools are hidden from the model entirely unless the user opted
        // in, so they can't be invoked (or even suggested) by accident.
        var allowCode = SettingsStore.AllowCodeExecution;
        var disabledGroups = new HashSet<string>(SettingsStore.DisabledToolGroups, StringComparer.OrdinalIgnoreCase);

        var tools = ToolRegistry.Instance.All
            .Where(t => allowCode || !t.RequiresCodeExecutionOptIn)
            .Where(t => disabledGroups.Count == 0 || !disabledGroups.Contains(ToolCatalog.CategoryOf(t)))
            .ToList();

        // Never send an empty tool list (a mis-set filter that disables everything would
        // leave the model unable to act) — fall back to the code-gated full set.
        if (tools.Count == 0)
            tools = ToolRegistry.Instance.All.Where(t => allowCode || !t.RequiresCodeExecutionOptIn).ToList();
        return tools;
    }

    // System = base prompt + one combined block for saved memory and learned experience.
    // The Anthropic API allows at most 4 cache_control breakpoints on a request, and we
    // already spend two more on the tools list and the trailing history block — so memory
    // and the experience digest MUST share a single breakpoint here (both are session-stable,
    // so caching them together loses nothing). Emitting a breakpoint per section overflowed
    // the limit once both existed ("maximum of 4 blocks with cache_control … Found 5").
    private static List<BetaTextBlockParam> BuildSystemBlocks()
    {
        var blocks = new List<BetaTextBlockParam>
        {
            new BetaTextBlockParam
            {
                Text = AnthropicPromptPrefix + SystemPromptBody,
                CacheControl = new BetaCacheControlEphemeral { Ttl = Ttl.Ttl1h }
            }
        };

        var extras = new StringBuilder();
        if (MemorySection() is { } memory) extras.Append(memory);
        if (ExperienceStore.Digest() is { } experience)
        {
            if (extras.Length > 0) extras.Append("\n\n");
            extras.Append(experience);
        }
        if (extras.Length > 0)
        {
            blocks.Add(new BetaTextBlockParam
            {
                Text = extras.ToString(),
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
        ObservableCollection<ChatMessage> conversation,
        Dispatcher ui,
        bool alt,
        CancellationToken ct)
    {
        var threshold = alt ? AltThreshold() : CompactionThresholdTokens;
        if (_lastPromptTokens < threshold) return;

        var userTurns = new List<int>();
        for (int i = 0; i < _history.Count; i++)
            if (_history[i].Role == "user" && _history[i].Blocks.All(b => b is ChatTextBlock))
                userTurns.Add(i);
        if (userTurns.Count <= CompactionKeepTailTurns) return;
        int cut = userTurns[userTurns.Count - CompactionKeepTailTurns];
        if (cut <= 0) return;

        var transcript = RenderTranscript(_history.Take(cut));
        const string instruction =
            "Summarize this conversation between a user and an AI assistant working " +
            "inside Autodesk Revit. Keep every fact needed to continue the work: what " +
            "was created or modified, element IDs, level/view/type names, decisions " +
            "made, unresolved errors and open tasks. Be dense; use bullet points.\n\n";

        string? summary;
        try
        {
            if (alt)
            {
                // Summarize on the same alt model — an Anthropic key may not exist at all.
                summary = await _openAI.CompleteOnceAsync(instruction + transcript, ct);
            }
            else
            {
                var resp = await GetClient().Beta.Messages.Create(new MessageCreateParams
                {
                    Model = CompactionModel,
                    MaxTokens = 2000,
                    Messages = new List<BetaMessageParam>
                    {
                        new BetaMessageParam
                        {
                            Role = ApiRole.User,
                            Content = instruction + transcript
                        }
                    }
                }, ct);

                var sb = new StringBuilder();
                foreach (var b in resp.Content)
                    if (b.TryPickText(out var t))
                        sb.Append(t.Text);
                summary = sb.ToString();

                TrackUsage("haiku-4-5", ToBackendTurn(resp));
            }
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

    private void TrackUsage(string modelTag, BackendTurn turn)
    {
        try
        {
            UsageTracker.Add(modelTag, turn.InputTokens, turn.OutputTokens,
                turn.CacheCreationTokens, turn.CacheReadTokens);
            _lastPromptTokens = turn.InputTokens + turn.CacheReadTokens + turn.CacheCreationTokens;
        }
        catch { /* non-fatal */ }
    }

    private static List<BetaToolUnion> BuildToolDefs(List<IRevitTool> allTools)
    {
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

    // Lenient parse for HISTORY REPLAY (Anthropic block params must always be buildable);
    // execution uses TryParseInput so truncated arguments never silently run a tool.
    private static IReadOnlyDictionary<string, JsonElement> ParseInput(string json) =>
        TryParseInput(json) ?? new Dictionary<string, JsonElement>();

    private static IReadOnlyDictionary<string, JsonElement>? TryParseInput(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                   ?? new Dictionary<string, JsonElement>();
        }
        catch
        {
            return null;
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
        // Script tools carry one big 'code' blob: show it verbatim — real newlines, real
        // characters — rather than a one-line JSON string with every quote, '>', '+' and
        // Cyrillic letter turned into \uXXXX. Any other args print compactly above it.
        if (input.TryGetValue("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
        {
            var extras = input.Where(kv => kv.Key != "code").ToDictionary(kv => kv.Key, kv => kv.Value);
            var head = extras.Count > 0 ? Json.Serialize(extras) + "\n" : "";
            return head + codeEl.GetString();
        }
        try { return Json.Serialize(input); }
        catch { return "(unprintable input)"; }
    }

    // Renders a tool result for the chat pane in a human-readable form: script tools wrap
    // their output in {"ok":…,"result":"…"} — show that text directly; other JSON is
    // re-emitted with the relaxed encoder so Cyrillic and symbols read normally; non-JSON is
    // shown as-is. Display only — the raw content still goes to the model unchanged.
    private static string FormatResult(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
                return r.GetString() ?? "";
            return Json.Serialize(root);
        }
        catch { return content; }
    }

    private static string Truncate(string s, int max) => TextUtil.Truncate(s, max);
}
