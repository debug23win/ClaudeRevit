using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace ClaudeRevit.Services;

// Official subscription clients may repeat a tool call after losing its HTTP response.
// Receipts belong to the turn, not the MCP connection: reconnecting must not execute it again.
// A closed turn is rejected by McpServer before admission, including after a Revit restart.
public sealed class McpOperationReplay
{
    public const string Instructions = "For EVERY Revit MCP call, include a unique operation_id in the OUTER arguments object " +
        "(1-128 ASCII letters, digits, dash, underscore or dot). Use a new ID for each intended operation, including reads. " +
        "Reuse the same ID and identical arguments only to recover the result of that same operation after a transport error. " +
        "For invoke_revit_tool, operation_id belongs beside name and arguments, not inside the native arguments. " +
        "Never repeat an uncertain model edit with a new ID; inspect the model first.";

    private const int MaxOperations = 2048;
    private const int MaxReceiptChars = 8 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _receiptChars;

    private sealed record Entry(string Fingerprint, TaskCompletionSource<(string? Value, string? Error)> Completion);

    public static void AddSchema(JsonObject schema)
    {
        var props = schema["properties"] as JsonObject ?? throw new ArgumentException("Expected object properties.");
        props["operation_id"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Unique ID for this intended operation (1-128 ASCII letters, digits, dash, underscore or dot). " +
                "Reuse only when recovering the same call after a transport error."
        };
        var required = schema["required"] as JsonArray ?? new JsonArray();
        if (schema["required"] == null) schema["required"] = required;
        if (!required.Any(n => n?.GetValue<string>() == "operation_id")) required.Add("operation_id");
    }

    public async Task<(JsonNode? value, JsonObject? error)> ExecuteAsync(string? operationId, JsonObject parameters,
        Func<Task<(JsonNode? value, JsonObject? error)>> execute)
    {
        if (string.IsNullOrEmpty(operationId) || operationId.Length > 128 ||
            operationId.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.')))
            throw new ArgumentException("A valid operation_id is required for every subscription tool call. " + Instructions);

        var canonical = Canonical(parameters)!.ToJsonString();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        Entry entry;
        bool owner;
        lock (_gate)
        {
            owner = !_entries.TryGetValue(operationId, out entry!);
            if (!owner && entry.Fingerprint != fingerprint)
                throw new ArgumentException("operation_id was already used with different arguments. The new operation was not executed.");
            if (owner)
            {
                if (_entries.Count >= MaxOperations)
                    throw new InvalidOperationException("This turn reached its operation receipt limit. Start a new user turn; no operation was executed.");
                entry = new Entry(fingerprint, new(TaskCreationOptions.RunContinuationsAsynchronously));
                _entries.Add(operationId, entry);
            }
        }

        if (owner)
        {
            try
            {
                var result = await execute().ConfigureAwait(false);
                var value = result.value?.ToJsonString();
                var error = result.error?.ToJsonString();
                lock (_gate)
                {
                    int length = (value?.Length ?? 0) + (error?.Length ?? 0);
                    if (length > MaxReceiptChars - _receiptChars)
                    {
                        // Keep a tombstone instead of evicting an ID and making it executable again.
                        entry.Completion.TrySetResult((null, new JsonObject
                        {
                            ["code"] = -32001,
                            ["message"] = "This operation already finished, but its response is too large to retain. " +
                                "It was not repeated. Inspect the model before another edit."
                        }.ToJsonString()));
                        return result;
                    }
                    _receiptChars += length;
                    entry.Completion.TrySetResult((value, error));
                }
            }
            catch (Exception ex)
            {
                // Retain failure/cancellation too: an exception is not proof that a write did not occur.
                entry.Completion.TrySetException(ex);
            }
        }
        var receipt = await entry.Completion.Task.ConfigureAwait(false);
        // Each HTTP response owns its JSON nodes; replay must not re-parent a previous response.
        return (receipt.Value == null ? null : JsonNode.Parse(receipt.Value),
            receipt.Error == null ? null : JsonNode.Parse(receipt.Error)!.AsObject());
    }

    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, Canonical(kv.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
        _ => node?.DeepClone()
    };
}
