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
using Anthropic.Models.Beta;
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
        "TOOLSET IS LAZY-LOADED: to save tokens you start with a CORE toolset only — model inspection, the " +
        "common modelling verbs (walls, floors, roofs, levels, grids, columns, beams, rooms, family " +
        "placement, materials, direct-shape/mesh, set_parameter, transforms, delete), and the code/learning " +
        "tools. Specialised groups are NOT loaded yet: rebar/reinforcement, MEP (duct/pipe), schedules, " +
        "sheets, annotation (tags/dimensions/text/spot/revision), view creation (sections/elevations/" +
        "callouts/3D/camera/crop/view range), family-editor authoring (parameters/formulas/arrays), export " +
        "(PDF/DWG/image), groups, and visibility/filters. When a task needs one of those, call find_tools " +
        "with a short query (e.g. find_tools(\"section view\"), find_tools(\"place rebar\"), find_tools(\"tag " +
        "doors\")) — the matching tools load instantly and you call them on the next step. Search FIRST; " +
        "don't fall back to execute_csharp for something a dedicated tool covers.\n\n" +
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
        "FREEFORM GEOMETRY: curved or sculptural shapes that walls/floors/roofs cannot express — domes, " +
        "shells, vaults, canopies, sweeping roofs, the Sydney Opera House sails (which are sections of one " +
        "common sphere, radius ~75 m) — are built with create_direct_shape from a mesh. Generate the vertices " +
        "PARAMETRICALLY (e.g. a sphere patch is two nested loops over polar angles), pass triangles/quads, and " +
        "prefer it over hand-writing geometry in execute_csharp. Build big forms one piece at a time and verify " +
        "each before the next. KEEP MESHES COARSE: a DirectShape (or any geometry) with more than a few thousand " +
        "faces builds and renders on Revit's UI thread and can FREEZE the app — a ~90k-face mesh hung it. Never " +
        "clone a high-poly reference mesh; use a low grid resolution (e.g. 12×8 per patch) and only refine if needed. " +
        "CRITICAL: never ElementTransformUtils.Rotate/Move/scale a heavy mesh after creating it — SetShape is cheap " +
        "but transforming re-processes every face and freezes Revit (this is exactly what hung it). Instead bake the " +
        "rotation/scale/offset into the VERTICES as you compute them, then create the shape already in place. To reuse " +
        "an EXISTING mesh (e.g. an imported reference model) in a different orientation, use clone_element_geometry " +
        "(it bakes the rotation/scale/move into the copied vertices safely) — never rotate the original or a copy of it.\n\n" +
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
        "single undo entry, so the user can ⌃Z to revert.\n\n" +
        "BE CONCISE: output tokens are billed and are the same price whatever the task. Do the work through " +
        "tool calls; don't narrate a play-by-play. Skip preambles like 'Начинаю моделировать…' / 'Let me…' and " +
        "don't restate the plan before each step or re-summarize what a tool just did — the user sees the tool " +
        "results. A short sentence before a batch of calls and a brief result at the end is enough; no filler.\n\n" +
        "BATCH YOUR WORK: every tool round is a full billed request, so do as much per round as you can. Most " +
        "tools that act on elements take a LIST of ids (move_elements, delete_elements, set_parameter targets, " +
        "tag_elements, copy_elements…) — pass all the ids at once instead of one call per element. When several " +
        "independent operations don't depend on each other's output, emit them as parallel tool calls in the " +
        "same round rather than one per round. Fewer, fuller rounds = lower cost and faster.";

    private const string AnthropicPromptPrefix =
        "You are Claude, integrated into Autodesk Revit 2027 as an AI assistant for architects and engineers. ";

    private const string AltPromptPrefix =
        "You are an AI assistant integrated into Autodesk Revit 2027 to help architects and engineers. ";

    // Non-Claude models are generally shakier at tool use — spell the contract out.
    private const string AltPromptSuffix =
        "\n\nIMPORTANT: When an action is needed, respond with a tool call whose arguments are valid JSON " +
        "matching the tool's schema exactly. Never describe a tool call in plain text instead of making it. " +
        "When no dedicated tool fits the request and execute_csharp is offered to you, USE execute_csharp — " +
        "write C# against the Revit API and run it, rather than explaining what could be done or asking the " +
        "user to do it manually. Only if execute_csharp is not in your tool list is code execution disabled; " +
        "then say so and suggest enabling it via the gear icon.";

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
    private readonly List<ApiTurn> _history = new();
    private long _lastPromptTokens;

    // Ephemeral instances (the benchmark) keep their conversation in memory only: they never load
    // from or write to the persisted chat history, so a benchmark run can't touch the user's chat.
    private readonly bool _ephemeral;

    // Metrics of the most recently completed SendAsync — the benchmark reads these; the chat pane
    // shows them as the per-task diagnostics line. Populated even when the diagnostics UI is off.
    public sealed record TaskMetrics(
        string Model, int Rounds, long InputTokens, long OutputTokens, int AdvisorConsults, double Seconds);
    public TaskMetrics? LastTask { get; private set; }

    // Active auto-promotion: the system prompt already asks the model to turn a repeated
    // execute_csharp pattern into a saved tool, but field logs showed that instruction ignored
    // (one pattern ran 300+ times, nothing saved). So we also remind it inline — once per
    // session, the moment it has run C# successfully more than once — right in the tool result
    // it reads next, where a nudge actually lands (same trick as the loop guard).
    private int _execCsharpOk;
    private bool _promoteNudged;

    // Progressive tool loading: only the core toolset is sent every request; a specialised group
    // is added here (in reveal order) once the user's message points at it or the model calls
    // find_tools. Session-scoped — cleared on ClearHistory. See ToolCatalog for the mechanism.
    private readonly List<string> _revealedCategories = new();

    private bool Reveal(string category)
    {
        if (_revealedCategories.Contains(category)) return false;
        _revealedCategories.Add(category);
        return true;
    }

    public ChatService() : this(false) { }

    public ChatService(bool ephemeral)
    {
        _ephemeral = ephemeral;
        if (!ephemeral) _history.AddRange(HistoryStore.LoadApiHistory());

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

    // Set by the chat pane: progress ping each tool-call round (current, max) for the status line.
    public Action<int, int>? OnRound;

    public void RecreateClient() => _client = null;

    public void ClearHistory()
    {
        // Clears only the conversation. The learning layer (ScriptJournal, MemoryStore,
        // ExperienceStore digest) lives in its own files under %AppData%\ClaudeRevit and is
        // deliberately NOT touched here — accumulated experience must outlive a window clear,
        // and the digest already sits in the (session-stable) system prompt.
        _history.Clear();
        _lastPromptTokens = 0;
        _execCsharpOk = 0;
        _promoteNudged = false;
        _revealedCategories.Clear();
        if (!_ephemeral) HistoryStore.Clear();
    }

    public void SaveHistory(IEnumerable<ChatMessage> uiMessages)
    {
        if (_ephemeral) return;
        HistoryStore.Save(uiMessages, _history);
    }

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

    // One-shot, non-streaming, tool-less completion. Used by the benchmark's impartial judge so a
    // fixed model can grade results independently of the model under test. No history, no tools.
    public async Task<string> RawCompleteAsync(string modelTag, string systemPrompt, string userText, CancellationToken ct = default)
    {
        var client = GetClient();
        var parameters = new MessageCreateParams
        {
            Model = ResolveModel(modelTag),
            MaxTokens = 1500,
            System = new List<BetaTextBlockParam> { new() { Text = systemPrompt } },
            Messages = new List<BetaMessageParam>
            {
                new() { Role = ApiRole.User, Content = new List<BetaContentBlockParam> { new BetaTextBlockParam { Text = userText } } }
            }
        };
        var msg = await client.Beta.Messages.Create(parameters, ct);
        return string.Concat(msg.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
    }

    public async Task SendAsync(
        ObservableCollection<ChatMessage> conversation,
        string model,
        CancellationToken ct = default,
        string? imageBase64 = null,
        string? imageMime = null)
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

        // Progressive tool loading: pre-load any specialised group the message obviously needs
        // ("add rebar", "make a section") so those tools are present on the first round and the
        // model doesn't have to spend a find_tools round first. It can still find_tools for the
        // rest. Purely additive to what's already revealed this session.
        foreach (var cat in ToolCatalog.PrewarmCategories(lastUser)) Reveal(cat);

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
        // "Auto" model mode. DEFAULT (advisorMode): Sonnet 5 runs every round and consults Opus
        // 4.8 via the advisor tool only when it needs a plan — Sonnet's prompt cache stays warm
        // all session, Opus is billed only for the short advice sub-inference. The legacy
        // alternative (Settings → AutoUseAdvisor off) switches the whole turn to Opus on a hard
        // task/error/deep-loop; see the escalation vars below.
        var claudeAuto = !alt && model == "auto";
        var advisorMode = claudeAuto && SettingsStore.AutoUseAdvisor;
        var advisorConsults = 0;             // total consults across the turn (cost backstop)
        const int MaxAdvisorPerTurn = 4;     // stop offering the advisor once this many have run

        var allowedTools = AllowedTools();
        List<BetaTextBlockParam>? systemBlocks = null;
        List<BetaToolUnion>? toolDefs = null;
        string? altSystem = null;
        System.Text.Json.Nodes.JsonArray? altTools = null;

        // Assembles the Claude tool list, appending the advisor tool while Auto is in advisor mode
        // and the per-turn backstop hasn't been hit. Used for the initial build AND every rebuild
        // (a find_tools reveal or a save_tool) so the advisor tool isn't dropped mid-turn.
        var advisorModelId = ResolveModel(SettingsStore.AutoAdvisorModel);
        List<BetaToolUnion> ClaudeTools()
        {
            var defs = BuildToolDefs(allowedTools);
            if (advisorMode && advisorConsults < MaxAdvisorPerTurn) defs.Add(AdvisorTool(advisorModelId));
            return defs;
        }

        if (alt)
        {
            altSystem = BuildAltSystemPrompt();
            altTools = OpenAIBackend.BuildTools(allowedTools);
        }
        else
        {
            systemBlocks = BuildSystemBlocks(advisorMode);
            toolDefs = ClaudeTools();
        }

        var userTurn = new ApiTurn { Role = "user", Blocks = { new ChatTextBlock(lastUser) } };
        if (!string.IsNullOrEmpty(imageBase64))
            userTurn.Blocks.Add(new ChatImageBlock(imageMime ?? "image/png", imageBase64));
        _history.Add(userTurn);

        var turnLabel = "Claude: " + Truncate(lastUser, 60);

        // Read once per prompt so a mid-turn Settings change doesn't shift the cap.
        var maxIterations = SettingsStore.MaxToolRounds;

        // Auto-escalation to a stronger model: keep a cheap/fast model for routine work, switch
        // to a stronger one when the task looks hard up front, or once the fast model starts
        // erroring / the task turns into a deep multi-step loop. Two paths share this machinery:
        //   • Alt path — fast alt model → the configured "reasoning" alt model.
        //   • Claude "Auto" — Sonnet 5 (cheap) → Opus 4.8 (strong). This is the big cost lever:
        //     Sonnet is $3/$15 vs Opus $5/$25 per 1M, so routine turns run at ~60% of the price
        //     and Opus is reserved for the turns that actually need it.
        // Legacy model-switch path (alt reasoning model, or Auto with the advisor turned off).
        var reasoningModel = alt ? SettingsStore.AltReasoningModel : "";
        var canEscalate = alt ? !string.IsNullOrWhiteSpace(reasoningModel) : (claudeAuto && !advisorMode);
        var escalate = canEscalate && LooksComplex(lastUser);
        const int EscalateAfterRounds = 6;

        // Loop guard, two tiers. Count identical tool errors across the turn (the field log had
        // one call fail 50× with the same message). On the 2nd/3rd repeat we DON'T stop — we
        // append an explicit "stop retrying, change approach" directive to that error result so
        // the model course-corrects ITSELF (verify a name with a list_*/get_* tool, switch
        // tools, use execute_csharp, or ask). Only if it STILL repeats do we hard-stop the turn.
        var errorStreak = new Dictionary<string, int>();
        const int SoftNudgeAt = 2;   // start telling it to change approach here
        const int MaxSameError = 4;  // give up (hard-stop) here

        // Spin guard: the same tool called with the SAME arguments repeatedly makes no progress
        // even when each call "succeeds" (a weaker model re-created the same floor type and
        // material 6× each, getting created:false every time, and never moved on). The error
        // guard above can't catch this because the calls aren't errors. Keyed by tool+arguments.
        var callStreak = new Dictionary<string, int>();

        // Auto-continue guard: models frequently end a turn with a "next step: …" narration and
        // NO tool call — the loop then breaks and waits for the user, so a long build stalls
        // halfway (one uploaded session stopped like this 11 times). When the final text clearly
        // announces more work rather than finishing or asking a question, we inject a "do it now"
        // nudge and keep going instead of stopping. Bounded so pure narration can't loop forever.
        var autoContinues = 0;
        const int MaxAutoContinues = 4;

        // Per-task diagnostics: wall-clock + token totals across the whole answer, shown as a
        // trailing line when enabled, so different models can be compared on the same task.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long taskInTok = 0, taskOutTok = 0;
        var taskRounds = 0;
        var modelsUsed = new HashSet<string>(StringComparer.Ordinal);

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

                if (OnRound != null)
                {
                    var r = iter + 1;
                    await ui.InvokeAsync(() => OnRound(r, maxIterations));
                }

                var altModelForTurn = escalate ? reasoningModel : null;
                // Claude Auto: in advisor mode the executor is the configured cheap/fast model
                // (Sonnet 5 default, or Haiku 4.5 for the cheapest tier); the advisor is reached
                // via the tool, not by switching. In legacy mode: Sonnet until escalated to Opus.
                var claudeModelForTurn = advisorMode
                    ? SettingsStore.AutoExecutorModel
                    : (claudeAuto ? (escalate ? "opus-4-8" : "sonnet-5") : model);
                var turn = alt
                    ? await StreamAltTurnAsync(altSystem!, altTools!, dynamicContext, conversation, ui, ct, altModelForTurn)
                    : await StreamAnthropicTurnAsync(claudeModelForTurn, systemBlocks!, toolDefs!, dynamicContext, conversation, ui, ct, advisorMode);

                TrackUsage(alt ? "alt:" + (altModelForTurn ?? SettingsStore.AltModel) : claudeModelForTurn, turn);

                // Advisor sub-inference: bill each consult at the advisor model's own rate, and
                // count consults so the per-turn backstop can retire the advisor tool once hit.
                foreach (var au in turn.AdvisorUsages)
                    TrackUsage(au.ModelTag, au.InputTokens, au.OutputTokens, au.CacheCreationTokens, au.CacheReadTokens);
                if (advisorMode && turn.AdvisorConsults > 0)
                {
                    var wasUnder = advisorConsults < MaxAdvisorPerTurn;
                    advisorConsults += turn.AdvisorConsults;
                    // Crossing the cap drops the advisor tool for the rest of the turn.
                    if (wasUnder && advisorConsults >= MaxAdvisorPerTurn) toolDefs = ClaudeTools();
                }

                // Per-task diagnostics accumulation (executor + advisor tokens).
                taskRounds++;
                taskInTok += turn.InputTokens + turn.CacheReadTokens + turn.CacheCreationTokens;
                taskOutTok += turn.OutputTokens;
                foreach (var au in turn.AdvisorUsages)
                {
                    taskInTok += au.InputTokens + au.CacheReadTokens + au.CacheCreationTokens;
                    taskOutTok += au.OutputTokens;
                }
                modelsUsed.Add(alt ? "alt:" + (altModelForTurn ?? SettingsStore.AltModel) : claudeModelForTurn);

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
                        break;
                    }

                    // The model produced text but no tool call. If that text announces more work
                    // ("next step…", "продолжаю", "сейчас создам") rather than finishing or asking
                    // the user something, don't stop — nudge it to actually perform the step. This
                    // is added to the API history only (not shown as a fake user chat message).
                    var finalText = string.Concat(turn.Blocks.OfType<ChatTextBlock>().Select(b => b.Text));
                    if (autoContinues < MaxAutoContinues && LooksUnfinished(finalText))
                    {
                        autoContinues++;
                        _history.Add(new ApiTurn
                        {
                            Role = "user",
                            Blocks =
                            {
                                new ChatTextBlock(
                                    "Continue now: actually perform the next step you just described by CALLING " +
                                    "the appropriate tools — do not restate the plan, apologize, or wait for me. " +
                                    "Keep going until the whole task is done. Only stop if the task is genuinely " +
                                    "complete or you truly need a decision from me (then ask one concrete question).")
                            }
                        });
                        continue;
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

                    // Progressive tool loading: find_tools is handled here, not on the Revit
                    // thread — it only searches the catalogue and reveals a group for the rest of
                    // the session. Revealing sets toolSetChanged so the tool list is rebuilt at the
                    // end of this round and the newly-loaded tools are callable on the next one.
                    if (name == "find_tools")
                    {
                        var query = inp.TryGetValue("query", out var qEl) && qEl.ValueKind == JsonValueKind.String
                            ? qEl.GetString() ?? "" : "";
                        var search = ToolCatalog.Search(ToolRegistry.Instance.All, query);
                        var revealedAny = false;
                        foreach (var c in search.Categories) revealedAny |= Reveal(c);
                        if (revealedAny) toolSetChanged = true;

                        await ui.InvokeAsync(() => conversation.Add(new ChatMessage
                        {
                            Role = "tool",
                            ToolName = name,
                            Text = FormatInput(inp) + "\n→ " + Truncate(search.Message, 400)
                        }));
                        resultTurn.Blocks.Add(new ChatToolResultBlock(use.Id, search.Message, false));
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

                    // Nudge the model to promote a repeated C# pattern into a saved tool — once
                    // per session, from the 2nd successful execute_csharp on. Appended to the
                    // model-facing content only (the user's chat already shows the real result).
                    if (!isError && name == "execute_csharp")
                    {
                        _execCsharpOk++;
                        if (SettingsStore.AllowCodeExecution && !_promoteNudged && _execCsharpOk >= 2)
                        {
                            content += "\n\n[SYSTEM: you have run execute_csharp successfully " +
                                _execCsharpOk + " times this session. If any of these is essentially " +
                                "the SAME kind of operation as one you already did (a repeatable " +
                                "pattern, not a genuine one-off), generalize it now — turn the specific " +
                                "ids/sizes/names into input parameters — and call save_tool ONCE to " +
                                "compile it into a persistent, 0-token tool the user can rerun from the " +
                                "▶ Run tool window. Skip this only if it was truly one-off.]";
                            _promoteNudged = true;
                        }
                    }

                    resultTurn.Blocks.Add(new ChatToolResultBlock(use.Id, content, isError));
                    if (!isError && (name == "save_tool" || name == "delete_tool"))
                    {
                        toolSetChanged = true;
                        _promoteNudged = true; // it's already promoting — stop reminding
                    }
                }

                _history.Add(assistantTurn);
                _history.Add(resultTurn);

                // Make a freshly saved/removed tool — or a group just revealed by find_tools —
                // visible to the model for the rest of this turn. Rebuild only the tool list (not
                // the system blocks) so the memory / experience prompt cache is untouched.
                if (toolSetChanged)
                {
                    allowedTools = AllowedTools();
                    if (alt) altTools = OpenAIBackend.BuildTools(allowedTools);
                    else toolDefs = ClaudeTools();
                }

                var erroredThisRound = resultTurn.Blocks.OfType<ChatToolResultBlock>()
                    .Where(b => b.IsError).ToList();

                // Escalate to the reasoning model for the rest of the turn once the fast model
                // hits a tool error / Revit rollback, or the task has become a deep multi-step
                // loop. Sticky: once escalated we stay on the reasoning model.
                if (canEscalate && !escalate && (erroredThisRound.Count > 0 || iter >= EscalateAfterRounds - 1))
                    escalate = true;

                // Loop guard: if the same error keeps coming back, first nudge the model to
                // change approach on its own (by appending a directive to that error result the
                // model reads next round); only hard-stop if it still won't. resultTurn is
                // already in _history, so replacing the block here reaches the model next round.
                var stuck = false;
                var countedThisRound = new HashSet<string>();
                for (int bi = 0; bi < resultTurn.Blocks.Count; bi++)
                {
                    if (resultTurn.Blocks[bi] is not ChatToolResultBlock b || !b.IsError) continue;
                    var sig = Truncate(b.Content, 160);
                    // Count each distinct error at most ONCE per round. A burst of identical
                    // parallel failures (e.g. placing four corner columns that all hit the same
                    // "type not found") is a single problem to solve, not four retries — counting
                    // per call would trip the hard stop within one round, before the model ever
                    // sees the "change approach" nudge on a later round.
                    int n = countedThisRound.Add(sig)
                        ? (errorStreak[sig] = errorStreak.GetValueOrDefault(sig) + 1)
                        : errorStreak[sig];
                    if (n >= MaxSameError) { stuck = true; }
                    else if (n >= SoftNudgeAt)
                    {
                        resultTurn.Blocks[bi] = b with
                        {
                            Content = b.Content +
                                "\n\n[SYSTEM: this exact error has now happened " + n + " times. Retrying the " +
                                "same call will NOT work — do not repeat it. Instead: look up the EXACT name/id " +
                                "with a list_* or get_* tool, or use a different tool or approach (execute_csharp " +
                                "is a fallback if code execution is enabled), or ask the user how to proceed.]"
                        };
                    }
                }
                // Spin guard: same tool + same arguments repeated across rounds (progress-free
                // even when it "succeeds"). Nudge to move on, then hard-stop. Counted once per
                // round per signature so a legitimate batch in one round doesn't trip it.
                var callSigsThisRound = new HashSet<string>();
                foreach (var use in toolUses)
                {
                    var csig = use.Name + "|" + Truncate(use.InputJson, 400);
                    if (!callSigsThisRound.Add(csig)) continue;
                    var n = callStreak[csig] = callStreak.GetValueOrDefault(csig) + 1;
                    if (n >= MaxSameError) { stuck = true; }
                    else if (n >= SoftNudgeAt)
                    {
                        var idx = resultTurn.Blocks.FindIndex(
                            x => x is ChatToolResultBlock r && r.ToolUseId == use.Id);
                        if (idx >= 0 && resultTurn.Blocks[idx] is ChatToolResultBlock rb)
                            resultTurn.Blocks[idx] = rb with
                            {
                                Content = rb.Content +
                                    "\n\n[SYSTEM: you have now made this EXACT same call (" + use.Name +
                                    ") " + n + " times. It is already done — repeating it changes nothing. " +
                                    "Do NOT call it again; move on to the NEXT distinct step. If every step is " +
                                    "done, verify with get_model_statistics and finish; if you are stuck, say so.]"
                            };
                    }
                }

                if (stuck)
                {
                    await ui.InvokeAsync(() => conversation.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Text = "[Stopped: the same tool call kept repeating with no progress. Either the task " +
                               "is already done, or the approach isn't working — check the model and take a " +
                               "different step, or switch to Claude for this one (it handles long builds better).]"
                    }));
                    break;
                }
            }
        }
        finally
        {
            await ToolDispatcher.Instance.EndTurnAsync(CancellationToken.None);

            sw.Stop();
            var mdl = string.Join("+", modelsUsed);
            LastTask = new TaskMetrics(mdl, taskRounds, taskInTok, taskOutTok, advisorConsults, sw.Elapsed.TotalSeconds);

            if (SettingsStore.ShowTaskDiagnostics && taskRounds > 0)
            {
                var advisorNote = advisorConsults > 0
                    ? $" · advisor {SettingsStore.AutoAdvisorModel}×{advisorConsults}" : "";
                var line = $"{mdl}{advisorNote} · {taskRounds} rounds · " +
                           $"{taskInTok + taskOutTok:N0} tokens (in {taskInTok:N0} / out {taskOutTok:N0}) · " +
                           $"{sw.Elapsed.TotalSeconds:0.0}s";
                await ui.InvokeAsync(() => conversation.Add(new ChatMessage { Role = "diag", Text = line }));
            }
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

    // Phrases that mean "I'm about to keep working" — a turn ending on one of these with NO tool
    // call is a premature stop, not a finished answer. EN + RU.
    private static readonly string[] ContinueCues =
    {
        // en
        "next step", "next, ", "let me ", "i'll now", "i will now", "now i'll", "now let me",
        "continuing", "proceeding", "i'll continue", "i will continue", "let's start", "let's begin",
        "moving on", "then i'll", "after that i'll",
        // ru
        "следующий шаг", "следующий этап", "далее", "продолжаю", "продолжу", "сейчас созда",
        "сейчас сдела", "сейчас постро", "начинаю", "приступа", "буду использовать", "давай начн",
        "давайте начн", "теперь созда", "теперь сдела", "далее созда", "затем"
    };

    // True when the final turn text announces more work AND isn't a question to the user or a
    // clear completion — the signal to auto-continue instead of stopping.
    private static bool LooksUnfinished(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.TrimEnd();
        // A question means it's waiting on the user's decision — respect that and stop.
        if (t.EndsWith("?")) return false;
        var lower = t.ToLowerInvariant();
        // A clear "all done" at the END shouldn't be overridden. Check only the tail so an
        // "step 1 completed. Next step: …" message still counts as unfinished.
        var tail = lower.Length > 120 ? lower[^120..] : lower;
        if (tail.Contains("готово") || tail.Contains("завершен") || tail.Contains("сделал")
            || tail.Contains("all done") || tail.Contains("completed") || tail.Contains("finished"))
            return false;
        return ContinueCues.Any(c => lower.Contains(c));
    }

    // ---- Anthropic path -----------------------------------------------------------

    // advisor-tool beta header — enabled only when the tool list actually carries the advisor.
    private const string AdvisorBeta = "advisor-tool-2026-03-01";

    private async Task<BackendTurn> StreamAnthropicTurnAsync(
        string model,
        List<BetaTextBlockParam> systemBlocks,
        List<BetaToolUnion> toolDefs,
        string dynamicContext,
        ObservableCollection<ChatMessage> conversation,
        Dispatcher ui,
        CancellationToken ct,
        bool useAdvisorBeta = false)
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
            OutputConfig = effort != null ? new BetaOutputConfig { Effort = effort.Value } : null,
            Betas = useAdvisorBeta ? new List<ApiEnum<string, AnthropicBeta>> { AdvisorBeta } : null
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
            // Advisor consult: count it. The server_tool_use / advisor_tool_result pair is NOT
            // added to turn.Blocks — dropping both keeps history valid on replay; the advice has
            // already shaped this response's own text/tool_use, which we do keep.
            else if (block.TryPickAdvisorToolResult(out _))
                turn.AdvisorConsults++;
        }
        try
        {
            var u = message.Usage;
            turn.InputTokens = u.InputTokens;
            turn.OutputTokens = u.OutputTokens;
            try { turn.CacheReadTokens = u.CacheReadInputTokens ?? 0; } catch { }
            try { turn.CacheCreationTokens = u.CacheCreationInputTokens ?? 0; } catch { }
            // Advisor sub-inferences are reported as separate usage "iterations", each tagged with
            // the advisor model — capture them so the caller bills each at that model's rate.
            try
            {
                foreach (var iter in u.Iterations ?? Enumerable.Empty<BetaIterationsUsageItems>())
                    if (iter.TryPickAdvisorMessageIterationUsage(out var adv))
                        turn.AdvisorUsages.Add(new AdvisorUsage(
                            TagFromModel(adv.Model.ToString() ?? ""),
                            adv.InputTokens, adv.OutputTokens,
                            adv.CacheCreationInputTokens, adv.CacheReadInputTokens));
            }
            catch { /* usage iterations are best-effort */ }
        }
        catch { /* non-fatal */ }
        return turn;
    }

    // Normalise an advisor/iteration model id (either "claude-opus-4-8" or an enum form like
    // "ClaudeOpus4_8") to our internal UsageTracker rate tag. Defaults to opus-4-8, the advisor.
    private static string TagFromModel(string s)
    {
        s = s.ToLowerInvariant().Replace("_", "-");
        if (s.Contains("opus") && s.Contains("4-7")) return "opus-4-7";
        if (s.Contains("opus")) return "opus-4-8";
        if (s.Contains("sonnet") && s.Contains("4-6")) return "sonnet-4-6";
        if (s.Contains("sonnet")) return "sonnet-5";
        if (s.Contains("haiku")) return "haiku-4-5";
        if (s.Contains("fable")) return "fable-5";
        return "opus-4-8";
    }

    // The advisor tool definition for Auto mode: the executor consults the advisor model at most
    // once per request (a per-turn total cap is enforced by the caller), with the advisor's own
    // transcript cached for an hour so repeated consults in a session don't re-bill the whole
    // prefix. Advisor model is user-configurable (Opus 4.8 default, or Fable 5 for hardest tasks).
    private static BetaToolUnion AdvisorTool(string advisorModelId) => new BetaAdvisorTool20260301
    {
        Model = advisorModelId,
        MaxUses = 1,
        Caching = new BetaCacheControlEphemeral { Ttl = Ttl.Ttl1h }
    };

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

    // The tools actually SENT to the model this turn — a small subset, not the ~180-tool catalogue.
    // Order is deterministic (core sorted, then each revealed group in reveal order, then custom
    // tools) so the cached tool prefix stays byte-identical between messages that reveal nothing,
    // and only the appended tail changes when a new group loads. That order is the whole reason
    // progressive loading also helps the cached Claude path, not just the uncached alt path.
    private List<IRevitTool> AllowedTools()
    {
        // Code-execution tools are hidden from the model entirely unless the user opted
        // in, so they can't be invoked (or even suggested) by accident.
        var allowCode = SettingsStore.AllowCodeExecution;
        var disabledGroups = new HashSet<string>(SettingsStore.DisabledToolGroups, StringComparer.OrdinalIgnoreCase);

        bool Ok(IRevitTool t) =>
            (allowCode || !t.RequiresCodeExecutionOptIn) &&
            (disabledGroups.Count == 0 || !disabledGroups.Contains(ToolCatalog.CategoryOf(t)));

        var all = ToolRegistry.Instance.All.Where(Ok).ToList();

        var core = all.Where(t => ToolCatalog.CoreToolNames.Contains(t.Name))
            .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

        var revealed = new List<IRevitTool>();
        foreach (var cat in _revealedCategories) // reveal order → append-only growth
            revealed.AddRange(all
                .Where(t => !ToolCatalog.IsCore(t) && ToolCatalog.CategoryOf(t) == cat)
                .OrderBy(t => t.Name, StringComparer.Ordinal));

        var custom = all.Where(ToolCatalog.IsCustom)
            .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

        var tools = core.Concat(revealed).Concat(custom).ToList();

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
    // When Auto is in advisor mode the model tends to under-use the advisor tool (field runs showed
    // 0 consults even on 20-round struggles). This directive, added to the cached prompt only in
    // advisor mode, tells it WHEN to reach for the advisor. Kept in the same block (no extra
    // cache_control breakpoint — the request already spends all four).
    private const string AdvisorDirective =
        "\n\nADVISOR: you have an 'advisor' tool — a stronger model that returns a plan or course " +
        "correction. CALL IT (don't just push on) when: the task needs real up-front decomposition " +
        "(a multi-part structure, a freeform shape, reinforcement layout, a steel connection); a tool " +
        "has failed twice the same way; or you're several rounds in without clear progress. Ask it a " +
        "specific question, then act on the advice. One good consult is far cheaper than a dozen wrong " +
        "rounds.";

    private static List<BetaTextBlockParam> BuildSystemBlocks(bool advisorMode = false)
    {
        var blocks = new List<BetaTextBlockParam>
        {
            new BetaTextBlockParam
            {
                Text = AnthropicPromptPrefix + SystemPromptBody + (advisorMode ? AdvisorDirective : ""),
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
        ChatImageBlock im => new BetaImageBlockParam
        {
            Source = new BetaBase64ImageSource { Data = im.Base64, MediaType = MediaTypeFor(im.MediaType) },
            CacheControl = cache
        },
        _ => throw new InvalidOperationException("Unknown history block type.")
    };

    private static MediaType MediaTypeFor(string mime) => mime switch
    {
        "image/jpeg" => MediaType.ImageJpeg,
        "image/gif" => MediaType.ImageGif,
        "image/webp" => MediaType.ImageWebP,
        _ => MediaType.ImagePng
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

    // Raw overload for advisor sub-inference usage (billed at the advisor model's own rate).
    // Does NOT touch _lastPromptTokens — that tracks the executor's prompt size, not the advisor's.
    private void TrackUsage(string modelTag, long input, long output, long cacheCreate, long cacheRead)
    {
        try { UsageTracker.Add(modelTag, input, output, cacheCreate, cacheRead); }
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
