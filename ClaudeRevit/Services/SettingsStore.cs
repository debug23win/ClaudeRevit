using System;
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
        public decimal BalanceUsd { get; set; } = 0;
        public decimal SpentUsd { get; set; } = 0;
    }
}
