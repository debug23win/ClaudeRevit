using System.Text.Json.Nodes;

namespace ModPlus.Headless;

// Keep control fields outside a bounded payload, so large preview results cannot lose their
// rollback status in the host's generic output cap. This helper never changes a success flag.
public static class HeadlessResultLimits
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new()
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Compact(string raw)
    {
        if (raw.Length <= 30_000) return raw;
        var source = JsonNode.Parse(raw) as JsonObject ?? throw new InvalidOperationException("Expected a structured headless result.");
        var result = new JsonObject { ["truncated"] = true, ["total_chars"] = raw.Length };
        foreach (var name in new[] { "ok", "preview", "committed", "rolledBack", "rejected", "idsTemporary", "modelModified", "module", "operation", "error", "errorCode" })
            if (source[name] is { } value) result[name] = value.ToJsonString(Options).Length <= 2_000 ? value.DeepClone() : JsonValue.Create(value.ToJsonString(Options)[..2_000] + "…");
        result["note"] = "Payload shortened; control fields retain their original status. Narrow the request or set emit=false on intermediate pipeline steps.";
        // JSON escaping can expand each Cyrillic/control character to six characters.
        // Budget the serialized envelope, rather than only the source substring.
        var length = Math.Min(10_000, raw.Length);
        while (true)
        {
            result["head"] = raw[..length];
            var envelope = result.ToJsonString(Options);
            if (envelope.Length <= 30_000) return envelope;
            if (length == 0) throw new InvalidOperationException("Headless control fields exceed the transport budget.");
            length /= 2;
        }
    }
}
