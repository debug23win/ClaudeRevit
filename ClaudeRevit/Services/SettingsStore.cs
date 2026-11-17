using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;

namespace ClaudeRevit.Services;

// Persisted user settings at %AppData%\ClaudeRevit\settings.json.
// Script execution is on by default and can be switched off (AllowCodeExecution); the legacy
// opt-in field is ignored, see AllowCodeExecution.
public static class SettingsStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeRevit", "settings.json");

    // Guards _cached and file writes: reads/writes are all-thread-safe even if a future
    // change moves usage tracking off the UI dispatcher.
    private static readonly object Gate = new();
    private static Settings? _cached;
    private static DateTime _lastSpendSaveUtc = DateTime.MinValue;

    // Arbitrary code (C#, Python, custom tools) — ON by default, and the user can switch it off.
    // Stored as a NEW "disabled" flag rather than the old AllowCodeExecution field: when execution
    // was opt-in, most settings files saved AllowCodeExecution=false without the user choosing it,
    // and honouring that would silently switch code off for them now that it is on by default.
    public static bool AllowCodeExecution
    {
        get => !Current.CodeExecutionDisabled;
        set { Current.CodeExecutionDisabled = !value; Save(); }
    }

    // Server-side refusal fallback (Fable 5 / 5.1, Opus 5): on by default, stored as a "disabled"
    // flag so existing settings files get the default.
    public static bool ServerFallback
    {
        get => !Current.ServerFallbackDisabled;
        set { Current.ServerFallbackDisabled = !value; Save(); }
    }

    // Optional confirmations apply to destructive native operations, not script permission.
    public static bool ConfirmOperations
    {
        get => Current.ConfirmOperations;
        set { Current.ConfirmOperations = value; Save(); }
    }

    // Alternative OpenAI-compatible provider (DeepSeek, Qwen, OpenRouter, local Ollama…):
    // preset id for the settings UI, endpoint base URL and model id. The key lives
    // encrypted in ApiKeyStore. Empty = not configured.
    public static string AltProvider
    {
        get => Current.AltProvider;
        set { Current.AltProvider = value; Save(); }
    }

    public static string AltBaseUrl
    {
        get => Current.AltBaseUrl;
        set { Current.AltBaseUrl = value; Save(); }
    }

    public static string AltModel
    {
        get => Current.AltModel;
        set { Current.AltModel = value; Save(); }
    }

    // Optional stronger "reasoning" model id for the alternative provider (e.g. xAI's
    // grok-4.20-…-reasoning). When set, ChatService keeps AltModel for routine work and
    // auto-switches to this one on complex tasks / errors. Empty = never escalate.
    public static string AltReasoningModel
    {
        get => Current.AltReasoningModel;
        set { Current.AltReasoningModel = value; Save(); }
    }

    // Context window of the alt model in thousands of tokens (0 = unknown → conservative
    // default). Drives the history-compaction threshold: an 8K local model must compact
    // long before a 1M Gemini.
    public static int AltContextK
    {
        get => Current.AltContextK;
        set { Current.AltContextK = value; Save(); }
    }

    // Compact the tool schemas sent to alternative (OpenAI-compatible) models to save tokens —
    // those providers don't cache the prompt, so the full tool list is re-billed every request.
    // On by default; turn off if a weaker model needs the full descriptions.
    public static bool AltCompactTools
    {
        get => Current.AltCompactTools;
        set { Current.AltCompactTools = value; Save(); }
    }

    // "Auto" model mode: how it reaches for Opus-level intelligence.
    //   true  (default) — advisor tool: Sonnet 5 runs the whole turn and consults Opus 4.8
    //                     mid-generation only when it needs a plan. Keeps Sonnet's prompt cache
    //                     warm all session; Opus billed only for the short advice sub-inference.
    //   false           — legacy model-switch: run Sonnet 5, switch the whole turn to Opus 4.8 on
    //                     a hard task / error / deep loop (invalidates the cache, bills Opus for
    //                     every remaining round). Kept as an alternative.
    public static bool AutoUseAdvisor
    {
        get => Current.AutoUseAdvisor;
        set { Current.AutoUseAdvisor = value; Save(); }
    }

    // "Auto" executor model: the cheap/fast model that runs every round.
    //   "sonnet-5-5" (default) — best balance.  "haiku-5-5" — cheapest, pairs with an advisor
    // for the hard moments. Values saved by older versions (Sonnet 5, Haiku 4.5) move to the
    // current generation of the same tier.
    public static string AutoExecutorModel
    {
        get => CurrentExecutor(Current.AutoExecutorModel);
        set { Current.AutoExecutorModel = value; Save(); }
    }

    // "Auto" advisor model consulted mid-turn.
    //   "opus-5-5" (default).  "fable-5-2" — Anthropic's most capable, for the hardest tasks
    // (only the short advice sub-inference is billed at its rate). Both are accepted as advisors
    // by both executors; Opus 4.8 and older are rejected by a Sonnet 5.5 executor, so older
    // saved values move to the current model of the same tier.
    public static string AutoAdvisorModel
    {
        get => CurrentAdvisor(Current.AutoAdvisorModel);
        set { Current.AutoAdvisorModel = value; Save(); }
    }

    public static string CurrentExecutor(string? saved) =>
        saved is "sonnet-5-5" or "haiku-5-5" ? saved
        : saved != null && saved.StartsWith("haiku", StringComparison.Ordinal) ? "haiku-5-5"
        : ClaudeModels.DefaultExecutor;

    public static string CurrentAdvisor(string? saved) =>
        saved is "opus-5-5" or "fable-5-2" ? saved
        : saved != null && (saved.StartsWith("fable", StringComparison.Ordinal) || saved.StartsWith("mythos", StringComparison.Ordinal)) ? "fable-5-2"
        : ClaudeModels.DefaultAdvisor;

    // Show a per-task diagnostics line (elapsed time, tokens, rounds) after each answer, to
    // compare how efficiently different models solve the same task. On by default.
    public static bool ShowTaskDiagnostics
    {
        get => Current.ShowTaskDiagnostics;
        set { Current.ShowTaskDiagnostics = value; Save(); }
    }

    // EXPERIMENTAL MCP server — expose Revit tools so Claude Code / Desktop (subscription-authed)
    // can drive Revit, putting cost on the subscription instead of the pay-per-token API.
    public static bool McpEnabled
    {
        get => Current.McpEnabled;
        set { Current.McpEnabled = value; Save(); }
    }

    public static int McpPort
    {
        get => Current.McpPort <= 0 ? 8788 : Current.McpPort;
        set { Current.McpPort = value; Save(); }
    }

    // A bearer token the MCP client must send — generated once, since the server exposes model
    // edits and arbitrary C# on a local port.
    public static string McpToken
    {
        get
        {
            if (string.IsNullOrEmpty(Current.McpToken))
            {
                Current.McpToken = Guid.NewGuid().ToString("N");
                Save();
            }
            return Current.McpToken;
        }
    }

    // The `claude` executable for the in-pane / benchmark Claude-Code path. Default "claude" (on
    // PATH); set a full path if PATH resolution fails.
    public static string ClaudeCodeExe
    {
        get => string.IsNullOrWhiteSpace(Current.ClaudeCodeExe) ? "claude" : Current.ClaudeCodeExe;
        set { Current.ClaudeCodeExe = value; Save(); }
    }

    // Path to the `codex` CLI, for the OpenAI-via-MCP path. Same reason as ClaudeCodeExe: Revit's
    // process often can't see the user's PATH, so a full path is usually required.
    public static string CodexExe
    {
        get => string.IsNullOrWhiteSpace(Current.CodexExe) ? "codex" : Current.CodexExe;
        set { Current.CodexExe = value; Save(); }
    }

    public static string QwenExe
    {
        get => string.IsNullOrWhiteSpace(Current.QwenExe) ? "qwen" : Current.QwenExe;
        set { lock (Gate) { Current.QwenExe = value; Save(); } }
    }

    public static string KimiExe
    {
        get => string.IsNullOrWhiteSpace(Current.KimiExe) ? "kimi" : Current.KimiExe;
        set { lock (Gate) { Current.KimiExe = value; Save(); } }
    }

    // Free-text model id for MCP-driven runs (Claude Code / Codex). The dropdowns only carry the
    // models shipped with a release; this lets a newly released id be used without an update. Empty
    // = use whatever the picker selected.
    public static string McpModelOverride
    {
        get => Current.McpModelOverride ?? "";
        set { Current.McpModelOverride = value; Save(); }
    }

    public static string ChatAgent
    {
        get => McpAgentSelection.IsSubscriptionAgent(Current.ChatAgent) ? Current.ChatAgent : "api";
        set { lock (Gate) { Current.ChatAgent = value; Save(); } }
    }

    public static string ChatUiMode
    {
        get => Current.ChatUiMode == "docked" ? "docked" : "window";
        set { lock (Gate) { Current.ChatUiMode = value == "docked" ? "docked" : "window"; Save(); } }
    }

    public static McpAgentSelection GetAgentSelection(string agent)
    {
        lock (Gate)
        {
            var (model, effort) = agent switch
            {
                "codex" => (Current.CodexModel, Current.CodexEffort),
                "claudecode" => (Current.ClaudeCodeModel, Current.ClaudeCodeEffort),
                "qwen" => (Current.QwenModel, Current.QwenEffort),
                "kimi" => (Current.KimiModel, Current.KimiEffort),
                _ => throw new ArgumentException("Unknown MCP agent: " + agent)
            };
            return new(agent, model ?? McpAgentSelection.MigrateOverride(agent, Current.McpModelOverride), effort);
        }
    }

    public static void SaveAgentSelection(McpAgentSelection selection)
    {
        lock (Gate)
        {
            switch (selection.Agent)
            {
                case "codex": Current.CodexModel = selection.Model ?? ""; Current.CodexEffort = selection.Effort ?? ""; break;
                case "claudecode": Current.ClaudeCodeModel = selection.Model ?? ""; Current.ClaudeCodeEffort = selection.Effort ?? ""; break;
                case "qwen": Current.QwenModel = selection.Model ?? ""; Current.QwenEffort = selection.Effort ?? ""; break;
                case "kimi": Current.KimiModel = selection.Model ?? ""; Current.KimiEffort = selection.Effort ?? ""; break;
                default: throw new ArgumentException("Unknown MCP agent: " + selection.Agent);
            }
            Save();
        }
    }

    // Max tool-call rounds the assistant may take within a single user prompt before it
    // stops and asks to continue. Clamped to a sane range so a stray value can't wedge a
    // turn into thousands of API calls. Default 24.
    public static int MaxToolRounds
    {
        get => Math.Clamp(Current.MaxToolRounds, 1, 200);
        set { Current.MaxToolRounds = Math.Clamp(value, 1, 200); Save(); }
    }

    // Tool groups (see ToolCatalog) the user switched OFF to shrink each request's token
    // cost. Empty = every group on (the default; no behaviour change).
    public static IReadOnlyList<string> DisabledToolGroups
    {
        get => Current.DisabledToolGroups;
        set { Current.DisabledToolGroups = value?.ToList() ?? new List<string>(); Save(); }
    }

    // Interface language for the settings window ("en"/"ru"). Empty = auto-detect from the
    // OS UI culture on first use.
    public static string UiLanguage
    {
        get
        {
            var v = Current.UiLanguage;
            if (!string.IsNullOrEmpty(v)) return v;
            try
            {
                return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"
                    ? "ru" : "en";
            }
            catch { return "en"; }
        }
        set { Current.UiLanguage = value; Save(); }
    }

    // User-entered account balance (from console.anthropic.com) and the estimated
    // spend accumulated since it was entered. The API exposes no balance endpoint,
    // so the pane shows balance − local spend estimate.
    public static decimal BalanceUsd => Current.BalanceUsd;
    public static decimal SpentUsd => Current.SpentUsd;

    public static void SetBalance(decimal balanceUsd)
    {
        lock (Gate)
        {
            Current.BalanceUsd = balanceUsd;
            Current.SpentUsd = 0;
            Save();
        }
    }

    // Called once per API message — debounced: the countdown is a display-only estimate,
    // so losing ≤30s of spend on a crash beats rewriting settings.json on every turn.
    public static void AddSpend(decimal usd)
    {
        if (usd <= 0) return;
        lock (Gate)
        {
            Current.SpentUsd += usd;
            if ((DateTime.UtcNow - _lastSpendSaveUtc).TotalSeconds >= 30)
                Save();
        }
    }

    // The debounce needs a final flush on add-in shutdown, or the tail of every session's
    // spend is silently dropped and the balance countdown drifts optimistic.
    public static void FlushSpend()
    {
        lock (Gate) Save();
    }

    private static Settings Current
    {
        get
        {
            lock (Gate)
            {
                if (_cached != null) return _cached;
                try
                {
                    if (File.Exists(FilePath))
                        _cached = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                }
                catch { /* fall through to defaults */ }
                return _cached ??= new Settings();
            }
        }
    }

    // Atomic write (temp + rename): a crash mid-write must never truncate settings.json —
    // it stores UI and provider preferences. Callers hold Gate or don't care (setters).
    private static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current));
            File.Move(tmp, FilePath, overwrite: true);
            _lastSpendSaveUtc = DateTime.UtcNow;
        }
        catch (Exception ex) { Log.Error("SettingsStore.Save failed", ex); }
    }

    private sealed class Settings
    {
        public bool ConfirmOperations { get; set; } = false;
        public bool CodeExecutionDisabled { get; set; } = false;
        public bool ServerFallbackDisabled { get; set; } = false;
        public string AltProvider { get; set; } = "";
        public string AltBaseUrl { get; set; } = "";
        public string AltModel { get; set; } = "";
        public string AltReasoningModel { get; set; } = "";
        public int AltContextK { get; set; } = 0;
        public bool AltCompactTools { get; set; } = true;
        public bool AutoUseAdvisor { get; set; } = true;
        public string AutoExecutorModel { get; set; } = ClaudeModels.DefaultExecutor;
        public string AutoAdvisorModel { get; set; } = ClaudeModels.DefaultAdvisor;
        public bool ShowTaskDiagnostics { get; set; } = true;
        public bool McpEnabled { get; set; }
        public int McpPort { get; set; } = 8788;
        public string McpToken { get; set; } = "";
        public string ClaudeCodeExe { get; set; } = "claude";
        public string CodexExe { get; set; } = "codex";
        public string QwenExe { get; set; } = "qwen";
        public string KimiExe { get; set; } = "kimi";
        public string McpModelOverride { get; set; } = "";
        public string ChatAgent { get; set; } = "api";
        public string ChatUiMode { get; set; } = "window";
        public string? CodexModel { get; set; }
        public string? CodexEffort { get; set; }
        public string? ClaudeCodeModel { get; set; }
        public string? ClaudeCodeEffort { get; set; }
        public string? QwenModel { get; set; }
        public string? QwenEffort { get; set; }
        public string? KimiModel { get; set; }
        public string? KimiEffort { get; set; }
        public int MaxToolRounds { get; set; } = 24;
        public List<string> DisabledToolGroups { get; set; } = new();
        public string UiLanguage { get; set; } = "";
        public decimal BalanceUsd { get; set; } = 0;
        public decimal SpentUsd { get; set; } = 0;
    }
}
