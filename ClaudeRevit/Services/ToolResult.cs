using System.Linq;
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
        RoundNumbers(result);
        return result.ToJsonString(Options);
    }

    // Every fractional number is cut to 7 significant digits: 32.808398950131235 becomes
    // 32.8084 — the same value to a hundredth of a millimetre in feet, at a fraction of the
    // tokens, on every coordinate, length and area a tool returns. Integers (element ids,
    // counts) are never touched.
    public const int SignificantDigits = 7;
    public static double Round(double d)
    {
        if (d == 0 || !double.IsFinite(d) || d == System.Math.Floor(d)) return d;
        var magnitude = (int)System.Math.Floor(System.Math.Log10(System.Math.Abs(d)));
        var decimals = SignificantDigits - 1 - magnitude;
        return decimals <= 0 ? System.Math.Round(d) : System.Math.Round(d, System.Math.Min(15, decimals));
    }
    private static void RoundNumbers(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (o[key] is JsonValue v && Rounded(v) is { } r) o[key] = r; else RoundNumbers(o[key]);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && Rounded(v) is { } r) a[i] = r; else RoundNumbers(a[i]);
                }
                break;
        }
    }
    private static JsonNode? Rounded(JsonValue v)
    {
        if (v.GetValueKind() != JsonValueKind.Number || v.TryGetValue<long>(out _)) return null;
        if (!v.TryGetValue<double>(out var d)) return null;
        var r = Round(d);
        return r == d ? null : JsonValue.Create(r);
    }

    public static string Failure(string code, string message) => new JsonObject
    {
        ["ok"] = false, ["error"] = message, ["warnings"] = new JsonArray(),
        ["errors"] = new JsonArray(new JsonObject { ["code"] = code, ["message"] = message })
    }.ToJsonString(Options);
}
