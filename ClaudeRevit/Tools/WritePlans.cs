using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeRevit.Tools;

// One contract for bulk writes, layered on the existing preview=true/false convention without
// changing it: a preview returns a plan_token naming exactly that call (tool, inputs, document
// state) and what it changed before rolling back; applying with that token is refused if the
// inputs differ or the model changed since — other agent edits, manual edits, undo — and the
// applied result is compared with the plan. Calls without a token behave exactly as before.
// (Idea from HorizunGroup/horizun-revit-mcp rehearsal → token → apply → re-read; Apache-2.0.)
internal static class WritePlans
{
    private sealed record Plan(string DocumentKey, string Tool, string InputHash, long Version, int Added, int Modified, int Deleted, DateTime Utc);
    private static readonly ConcurrentDictionary<string, Plan> Plans = new();
    private static readonly ConcurrentDictionary<string, long> Versions = new();
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    // The document changed in a way a preview could not have foreseen.
    public static void Bump(string documentKey) => Versions.AddOrUpdate(documentKey, 1, (_, v) => v + 1);
    private static long Version(string documentKey) => Versions.GetValueOrDefault(documentKey);

    public static string InputHash(IReadOnlyDictionary<string, JsonElement> input)
    {
        var canonical = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in input)
            if (k is not ("preview" or "plan_token")) canonical[k] = v.GetRawText();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical))))[..16];
    }

    // Before an apply call that carries a plan_token: the plan it names, or a reason to refuse.
    public static object? Check(string documentKey, string tool, IReadOnlyDictionary<string, JsonElement> input)
    {
        if (!input.TryGetValue("plan_token", out var t) || t.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(t.GetString())) return null;
        if (input.TryGetValue("preview", out var p) && p.ValueKind != JsonValueKind.False)
            throw new ToolInputException("plan_token applies a previewed plan: call with preview=false.");
        var token = t.GetString()!;
        if (!Plans.TryGetValue(token, out var plan) || DateTime.UtcNow - plan.Utc > Lifetime)
            throw new ToolInputException("Unknown or expired plan_token. Run the preview again. No changes were made.");
        if (plan.DocumentKey != documentKey || plan.Tool != tool)
            throw new ToolInputException($"This plan_token belongs to {plan.Tool} in another document. No changes were made.");
        if (plan.InputHash != InputHash(input))
            throw new ToolInputException("The inputs differ from the previewed plan. Preview the changed inputs first. No changes were made.");
        if (plan.Version != Version(documentKey))
            throw new ToolInputException("The model changed since this preview (another edit, a manual change or an undo). Preview again. No changes were made.");
        return plan;
    }

    // After the call: a preview gets its token; an apply with a token gets the comparison.
    public static string Annotate(string documentKey, string tool, IReadOnlyDictionary<string, JsonElement> input, string result,
        (int Added, int Modified, int Deleted) changes, object? applied)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(result) as JsonObject; } catch (JsonException) { return result; }
        if (obj == null) return result;
        var isPreview = obj["preview"] is JsonValue pv && pv.TryGetValue<bool>(out var b) && b;
        if (isPreview)
        {
            foreach (var old in Plans.Where(kv => DateTime.UtcNow - kv.Value.Utc > Lifetime).Select(kv => kv.Key).ToList()) Plans.TryRemove(old, out _);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            Plans[token] = new(documentKey, tool, InputHash(input), Version(documentKey), changes.Added, changes.Modified, changes.Deleted, DateTime.UtcNow);
            obj["plan_token"] = token;
            obj["plan"] = new JsonObject { ["added"] = changes.Added, ["modified"] = changes.Modified, ["deleted"] = changes.Deleted };
            return obj.ToJsonString();
        }
        if (applied is Plan plan)
        {
            Plans.TryRemove(input["plan_token"].GetString()!, out _);
            var matches = plan.Added == changes.Added && plan.Deleted == changes.Deleted && plan.Modified == changes.Modified;
            obj["verification"] = new JsonObject
            {
                ["planned"] = new JsonObject { ["added"] = plan.Added, ["modified"] = plan.Modified, ["deleted"] = plan.Deleted },
                ["actual"] = new JsonObject { ["added"] = changes.Added, ["modified"] = changes.Modified, ["deleted"] = changes.Deleted },
                ["matches_plan"] = matches
            };
            return obj.ToJsonString();
        }
        return result;
    }

    public static Anthropic.Models.Beta.Messages.InputSchema Schema(IRevitTool tool)
    {
        var schema = tool.InputSchema;
        if (!Services.PlanTokenSchema.Applies(schema.Properties)) return schema;
        return new Anthropic.Models.Beta.Messages.InputSchema
        {
            Properties = new Dictionary<string, JsonElement>(schema.Properties!) { ["plan_token"] = Services.PlanTokenSchema.Property },
            Required = schema.Required
        };
    }
}
