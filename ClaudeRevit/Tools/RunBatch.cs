using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Runs ONE built-in tool many times in a single round and a single transaction. This is the fix
// for repetitive work (place 20 columns, create 10 walls, set a parameter on many elements): doing
// it one call per round re-sends the whole growing prompt every round — brutal on alt providers,
// which don't cache — whereas run_batch does the lot in one round. All items share the dispatcher's
// transaction and its failure suppression, so it's also one undo step.
public class RunBatch : IRevitTool
{
    public string Name => "run_batch";

    public string Description =>
        "Runs one other tool repeatedly in a SINGLE round and transaction — the way to do repetitive " +
        "work cheaply (e.g. place 20 columns, create 10 walls/beams/grids/levels, place many family " +
        "instances, set a parameter on many elements). Pass 'tool' (the tool's name) and 'items' (an " +
        "array where each entry is exactly the arguments that tool takes for one call). Returns one " +
        "result per item; a failing item is reported but doesn't abort the others. MUCH cheaper and " +
        "faster than calling the tool once per element. Only wraps tools that create/modify through a " +
        "normal transaction — not execute_csharp / run_dynamo_python (call those directly).";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["tool"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Name of the tool to run for each item (e.g. \"create_structural_column\")."
            }),
            ["items"] = JsonSerializer.SerializeToElement(new
            {
                type = "array",
                items = new { type = "object" },
                description = "One object per call — each is the full argument set that 'tool' expects."
            })
        },
        Required = ["tool", "items"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var toolName = input.TryGetValue("tool", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(toolName))
            throw new InvalidOperationException("'tool' (the name of the tool to batch) is required.");

        if (toolName is "run_batch" or "execute_csharp" or "run_dynamo_python")
            throw new InvalidOperationException($"run_batch can't wrap '{toolName}' — call it directly.");

        var inner = ToolRegistry.Instance.Get(toolName)
            ?? throw new InvalidOperationException($"Unknown tool '{toolName}'.");

        if (!inner.RequiresTransaction)
            throw new InvalidOperationException(
                $"run_batch only wraps tools that create/modify through a normal transaction. " +
                $"'{toolName}' manages its own — call it directly, once per item.");

        if (inner.RequiresCodeExecutionOptIn && !Services.SettingsStore.AllowCodeExecution)
            throw new InvalidOperationException("Code execution is disabled for that tool.");

        if (!input.TryGetValue("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("'items' must be an array of argument objects (one per call).");

        var results = new List<object>();
        int ok = 0, index = 0;
        foreach (var item in itemsEl.EnumerateArray())
        {
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                results.Add(new { index, ok = false, error = "item is not a JSON object" });
                continue;
            }

            var args = new Dictionary<string, JsonElement>();
            foreach (var p in item.EnumerateObject()) args[p.Name] = p.Value;

            try
            {
                var raw = inner.Execute(args, app);
                results.Add(new { index, ok = true, result = AsJson(raw) });
                ok++;
            }
            catch (Exception ex)
            {
                results.Add(new { index, ok = false, error = ex.Message });
            }
        }

        return Services.Json.Serialize(new { tool = toolName, count = index, ok, failed = index - ok, results });
    }

    // Nest the inner tool's JSON result as structured data rather than an escaped string; fall back
    // to the raw text if it isn't valid JSON.
    private static object AsJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        try { using var d = JsonDocument.Parse(raw); return d.RootElement.Clone(); }
        catch { return raw; }
    }
}
