using System.Reflection;
using System.Text.Json;

namespace ClaudeRevit.Services;

public static class StandardWorkflows
{
    private static readonly Lazy<JsonElement> Reference = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeRevit.Standards.Workflows.json")
            ?? throw new InvalidOperationException("Standard workflows are missing.");
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    });
    public static JsonElement Data => Reference.Value;
}
