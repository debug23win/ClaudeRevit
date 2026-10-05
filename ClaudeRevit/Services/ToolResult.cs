using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeRevit.Services;

// Preserve existing payload fields for older clients. Every response remains ONE JSON value.
public static class ToolResult
{
    public static string? ErrorMessage(string? raw)
    {
        if(raw==null)return null;
        try { using var d=JsonDocument.Parse(raw);var r=d.RootElement;return r.ValueKind==JsonValueKind.Object&&r.TryGetProperty("error",out var e)?e.ValueKind==JsonValueKind.String?e.GetString():e.GetRawText():null; }
        catch(JsonException){return null;}
    }
    public static readonly JsonSerializerOptions Options=new(JsonSerializerOptions.Default) { Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Complete(string raw, IEnumerable<string>? warnings = null)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(raw); }
        catch (JsonException) { parsed = JsonValue.Create(raw); }
        var result = parsed as JsonObject ?? new JsonObject { ["result"] = parsed };
        var messages = new JsonArray();
        if (result["warnings"] is JsonArray existing)
            foreach (var warning in existing) messages.Add(warning?.DeepClone());
        else if(result["warnings"] is { } warning) messages.Add(warning.DeepClone());
        foreach (var warning in warnings ?? []) messages.Add(warning);
        result["warnings"] = messages;
        result["errors"] ??= new JsonArray();
        if(result["ok"] is JsonValue state && state.TryGetValue<bool>(out var success) && !success &&
            result["errors"] is JsonArray errors && errors.Count==0 && result["error"] is { } error)
            errors.Add(new JsonObject { ["code"]="tool_error",["message"]=error.DeepClone() });
        // Some batch tools use an integer 'ok' count. Do not reinterpret that contract.
        if (!result.ContainsKey("ok")) result["ok"] = true;
        return result.ToJsonString(Options);
    }

    public static string Failure(string code, string message) => new JsonObject
    {
        ["ok"] = false, ["error"] = message, ["warnings"] = new JsonArray(),
        ["errors"] = new JsonArray(new JsonObject { ["code"] = code, ["message"] = message })
    }.ToJsonString(Options);
}
