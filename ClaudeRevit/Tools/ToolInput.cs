using System.Collections.Generic;
using System.Text.Json;

namespace ClaudeRevit.Tools;

// Optional-field readers for tool inputs. LLMs routinely send explicit nulls for optional
// fields they don't use ("top_mm": null); JsonElement.GetDouble()/GetInt32() throw on a
// Null-kind element, so absence and null must be treated the same.
internal static class ToolInput
{
    public static double? OptionalDouble(IReadOnlyDictionary<string, JsonElement> input, string name) =>
        input.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static int? OptionalInt(IReadOnlyDictionary<string, JsonElement> input, string name) =>
        input.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    // Required fields. Reading them as input["start_x"].GetDouble() throws a
    // NullReferenceException on a missing key and a plain InvalidOperationException on a
    // wrong type, and neither message names the parameter — so the model gets "Object
    // reference not set to an instance of an object", guesses, and burns a round. These
    // say which parameter, what was expected, and what arrived, which is usually enough
    // for the model to fix the call on the next try.
    //
    // Numbers are accepted from a JSON string too ("3000" for 3000): models produce that
    // often enough, the intent is unambiguous, and the alternative is a failed round over
    // a pair of quotes.
    public static double RequiredDouble(IReadOnlyDictionary<string, JsonElement> input, string name)
    {
        if (!input.TryGetValue(name, out var v) || v.ValueKind == JsonValueKind.Null)
            throw new ToolInputException($"Missing required parameter '{name}' (a number).");
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        throw new ToolInputException(
            $"Parameter '{name}' must be a number, got {Describe(v)}.");
    }

    public static int RequiredInt(IReadOnlyDictionary<string, JsonElement> input, string name)
    {
        if (!input.TryGetValue(name, out var v) || v.ValueKind == JsonValueKind.Null)
            throw new ToolInputException($"Missing required parameter '{name}' (an integer).");
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.Number) return (int)System.Math.Round(v.GetDouble());
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var parsed))
            return parsed;
        throw new ToolInputException(
            $"Parameter '{name}' must be an integer, got {Describe(v)}.");
    }

    public static long RequiredLong(IReadOnlyDictionary<string, JsonElement> input, string name)
    {
        if (!input.TryGetValue(name, out var v) || v.ValueKind == JsonValueKind.Null)
            throw new ToolInputException($"Missing required parameter '{name}' (an element id).");
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var parsed))
            return parsed;
        throw new ToolInputException(
            $"Parameter '{name}' must be an element id (a number), got {Describe(v)}.");
    }

    public static string RequiredString(IReadOnlyDictionary<string, JsonElement> input, string name)
    {
        if (!input.TryGetValue(name, out var v) || v.ValueKind == JsonValueKind.Null)
            throw new ToolInputException($"Missing required parameter '{name}' (a string).");
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s!;
            throw new ToolInputException($"Parameter '{name}' is empty.");
        }
        throw new ToolInputException($"Parameter '{name}' must be a string, got {Describe(v)}.");
    }

    private static string Describe(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => $"the string \"{Trim(v.GetString())}\"",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Number => "a number",
        _ => v.ValueKind.ToString().ToLowerInvariant()
    };

    private static string Trim(string? s) =>
        s == null ? "" : s.Length <= 40 ? s : s.Substring(0, 40) + "…";

    // Boolean flags: LLMs also emit "true"/1 — honor the intent instead of silently
    // treating anything but a JSON true as false.
    public static bool Flag(IReadOnlyDictionary<string, JsonElement> input, string name)
    {
        if (!input.TryGetValue(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => string.Equals(v.GetString(), "true", System.StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number => v.TryGetInt32(out var n) && n != 0,
            _ => false
        };
    }
}
