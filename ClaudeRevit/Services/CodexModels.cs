using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClaudeRevit.Services;

public sealed record CodexModel(string Id, string Name, string DefaultEffort,
    IReadOnlyList<string> Efforts, bool IsDefault);

public static class CodexModels
{
    public static IReadOnlyList<CodexModel> Parse(IEnumerable<JsonElement> entries)
    {
        var models = new List<CodexModel>();
        foreach (var entry in entries)
        {
            if (entry.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) continue;
            var id = Text(entry, "model");
            if (id.Length == 0 || models.Any(m => m.Id == id)) continue;
            var name = Text(entry, "displayName");
            var defaultEffort = Text(entry, "defaultReasoningEffort");
            var efforts = new List<string>();
            if (entry.TryGetProperty("supportedReasoningEfforts", out var supported) && supported.ValueKind == JsonValueKind.Array)
                foreach (var option in supported.EnumerateArray())
                {
                    var value = Text(option, "reasoningEffort");
                    if (value.Length > 0 && !efforts.Contains(value)) efforts.Add(value);
                }
            if (efforts.Count == 0 && defaultEffort.Length > 0) efforts.Add(defaultEffort);
            if (!efforts.Contains(defaultEffort)) defaultEffort = efforts.FirstOrDefault() ?? "";
            models.Add(new(id, name.Length > 0 ? name : id, defaultEffort, efforts,
                entry.TryGetProperty("isDefault", out var d) && d.ValueKind == JsonValueKind.True));
        }
        if (models.Count == 0) throw new IOException("Codex returned no available models. Check your Codex login.");
        return models;
    }

    public static (string Model, string? Effort) Select(IReadOnlyList<CodexModel> models, string? model, string? effort)
    {
        var selected = string.IsNullOrWhiteSpace(model)
            ? models.FirstOrDefault(m => m.IsDefault) ?? models.FirstOrDefault()
            : models.FirstOrDefault(m => m.Id == model.Trim());
        if (selected == null) throw new IOException("Selected Codex model is unavailable: " + model + ". Refresh models and select another.");
        var level = string.IsNullOrWhiteSpace(effort) ? selected.DefaultEffort : effort.Trim();
        if (level.Length > 0 && !selected.Efforts.Contains(level))
            throw new IOException(selected.Name + " does not support reasoning effort '" + level + "'. Select another level.");
        return (selected.Id, level.Length > 0 ? level : null);
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
}
