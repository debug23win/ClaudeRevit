using System.Reflection;
using System.Text.Json;

namespace ClaudeRevit.Services;

// Explicit allowlist: never reflect secrets, endpoint URLs, executable paths or spend.
public static class AgentSettings
{
    private static readonly string[] Names = ["AllowCodeExecution", "ConfirmOperations", "ServerFallback", "ShowTaskDiagnostics",
        "AltProvider", "AltModel", "AltReasoningModel", "AltContextK", "AltCompactTools", "AutoUseAdvisor",
        "AutoExecutorModel", "AutoAdvisorModel", "ChatAgent", "ChatUiMode", "MaxToolRounds", "DisabledToolGroups", "UiLanguage"];
    public static Dictionary<string, object?> Read() => Names.ToDictionary(n => n, n => typeof(SettingsStore).GetProperty(n)!.GetValue(null));
    public static Dictionary<string, object?> Update(JsonElement changes)
    {
        if (changes.ValueKind != JsonValueKind.Object) throw new ArgumentException("changes must be an object.");
        var pending = new Dictionary<string, (PropertyInfo Property, object? Value)>();
        foreach (var entry in changes.EnumerateObject())
        {
            if (!Names.Contains(entry.Name, StringComparer.Ordinal)) throw new ArgumentException("Unsupported setting: " + entry.Name);
            var property = typeof(SettingsStore).GetProperty(entry.Name)!;
            object value;
            try { value = JsonSerializer.Deserialize(entry.Value.GetRawText(), property.PropertyType) ?? throw new ArgumentException("Null setting: " + entry.Name); }
            catch (JsonException ex) { throw new ArgumentException("Invalid value for " + entry.Name, ex); }
            if (value is string text && (text.Length > 200 || text.Any(char.IsControl))) throw new ArgumentException("Invalid text: " + entry.Name);
            if (entry.Name == "ChatAgent" && value is not ("api" or "codex" or "claudecode")) throw new ArgumentException("Invalid ChatAgent.");
            if (entry.Name == "ChatUiMode" && value is not ("window" or "docked")) throw new ArgumentException("Invalid ChatUiMode.");
            if (entry.Name == "MaxToolRounds" && (int)value is < 1 or > 200) throw new ArgumentException("MaxToolRounds must be 1..200.");
            if (entry.Name == "AltContextK" && (int)value is < 0 or > 10000) throw new ArgumentException("AltContextK must be 0..10000.");
            if (entry.Name == "DisabledToolGroups" && ((IEnumerable<string>)value).Any(g => g == null || g.Length > 100)) throw new ArgumentException("Invalid tool groups.");
            if (!pending.TryAdd(entry.Name, (property, value))) throw new ArgumentException("Duplicate setting: " + entry.Name);
        }
        // Validate the entire request before applying any preference.
        foreach (var (_, item) in pending) item.Property.SetValue(null, item.Value);
        return Read();
    }
}
