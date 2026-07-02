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

    // "dynamo" (preferred, safer) or "csharp"
    public static string CodeBackend
    {
        get => string.IsNullOrWhiteSpace(Current.CodeBackend) ? "dynamo" : Current.CodeBackend;
        set { Current.CodeBackend = value; Save(); }
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
        public string CodeBackend { get; set; } = "dynamo";
    }
}
