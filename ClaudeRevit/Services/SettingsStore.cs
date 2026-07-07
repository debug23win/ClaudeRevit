using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;

namespace ClaudeRevit.Services;

// Persisted user settings at %AppData%\ClaudeRevit\settings.json.
// Code execution is OFF by default: nothing can run C#/Dynamo until the user
// explicitly ticks the box in the settings window.
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

    public static bool AllowCodeExecution
    {
        get => Current.AllowCodeExecution;
        set { Current.AllowCodeExecution = value; Save(); }
    }

    // Per-operation Allow/Deny dialogs. Off by default: every mutation is already
    // grouped into one undo step (Ctrl+Z), and the code-execution opt-in still gates
    // the script tools.
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
    //   "sonnet-5" (default) — best balance.  "haiku-4-5" — cheapest/fastest, pairs with an
    // advisor for the hard moments (Haiku executor + Opus/Fable advisor is a valid pattern).
    public static string AutoExecutorModel
    {
        get => string.IsNullOrWhiteSpace(Current.AutoExecutorModel) ? "sonnet-5" : Current.AutoExecutorModel;
        set { Current.AutoExecutorModel = value; Save(); }
    }

    // "Auto" advisor model consulted mid-turn.
    //   "opus-4-8" (default).  "fable-5" — Anthropic's most capable, for the hardest tasks
    // (2x Opus's price, but only the short advice sub-inference is billed at that rate).
    public static string AutoAdvisorModel
    {
        get => string.IsNullOrWhiteSpace(Current.AutoAdvisorModel) ? "opus-4-8" : Current.AutoAdvisorModel;
        set { Current.AutoAdvisorModel = value; Save(); }
    }

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
    // edits (and, with code execution on, arbitrary C#) on a local port.
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
    // it also stores the code-execution opt-in. Callers hold Gate or don't care (setters).
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
        public bool AllowCodeExecution { get; set; } = false;
        public bool ConfirmOperations { get; set; } = false;
        public string AltProvider { get; set; } = "";
        public string AltBaseUrl { get; set; } = "";
        public string AltModel { get; set; } = "";
        public string AltReasoningModel { get; set; } = "";
        public int AltContextK { get; set; } = 0;
        public bool AltCompactTools { get; set; } = true;
        public bool AutoUseAdvisor { get; set; } = true;
        public string AutoExecutorModel { get; set; } = "sonnet-5";
        public string AutoAdvisorModel { get; set; } = "opus-4-8";
        public bool ShowTaskDiagnostics { get; set; } = true;
        public bool McpEnabled { get; set; }
        public int McpPort { get; set; } = 8788;
        public string McpToken { get; set; } = "";
        public int MaxToolRounds { get; set; } = 24;
        public List<string> DisabledToolGroups { get; set; } = new();
        public string UiLanguage { get; set; } = "";
        public decimal BalanceUsd { get; set; } = 0;
        public decimal SpentUsd { get; set; } = 0;
    }
}
