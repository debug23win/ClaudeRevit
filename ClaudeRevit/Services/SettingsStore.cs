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

    private static Settings? _cached;

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

    // User-entered account balance (from console.anthropic.com) and the estimated
    // spend accumulated since it was entered. The API exposes no balance endpoint,
    // so the pane shows balance − local spend estimate.
    public static decimal BalanceUsd => Current.BalanceUsd;
    public static decimal SpentUsd => Current.SpentUsd;

    public static void SetBalance(decimal balanceUsd)
    {
        Current.BalanceUsd = balanceUsd;
        Current.SpentUsd = 0;
        Save();
    }

    public static void AddSpend(decimal usd)
    {
        if (usd <= 0) return;
        Current.SpentUsd += usd;
        Save();
    }

    private static Settings Current
    {
        get
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

    private static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current));
        }
        catch (Exception ex) { Log.Error("SettingsStore.Save failed", ex); }
    }

    private sealed class Settings
    {
        public bool AllowCodeExecution { get; set; } = false;
        public bool ConfirmOperations { get; set; } = false;
        public decimal BalanceUsd { get; set; } = 0;
        public decimal SpentUsd { get; set; } = 0;
    }
}
