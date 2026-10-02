using System.Reflection;
using System.Text.Json;

namespace ClaudeRevit.Services;

public static class BimStarterKnowledge
{
    private static readonly Lazy<JsonElement> Catalog = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeRevit.Standards.BimStarterTools.json")
            ?? throw new InvalidOperationException("BIMStarter tool catalog is missing.");
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.Clone();
    });
    public static JsonElement Source => Catalog.Value;
    public static IReadOnlyList<JsonElement> Commands => Source.GetProperty("commands").EnumerateArray().ToArray();
}
