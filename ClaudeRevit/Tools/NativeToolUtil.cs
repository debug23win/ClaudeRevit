using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

internal static class NativeToolUtil
{
    public static JsonElement Field(string type, string description) => JsonSerializer.SerializeToElement(new { type, description });
    public static JsonElement Any(string description) => JsonSerializer.SerializeToElement(new { description });
    public static JsonElement Array(string type, string description) => JsonSerializer.SerializeToElement(new { type = "array", items = new { type }, description });
    public static InputSchema Schema(Dictionary<string, JsonElement> fields, params string[] required) => new() { Properties = fields, Required = required.ToList() };
    public static Document Doc(UIApplication app) => ToolContext.UiDocument(app)?.Document ?? throw new InvalidOperationException("No active document.");
    public static string Text(IReadOnlyDictionary<string, JsonElement> input, string name, string fallback = "") =>
        input.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    public static bool Preview(IReadOnlyDictionary<string, JsonElement> input) => !input.TryGetValue("preview", out var v) || v.ValueKind == JsonValueKind.Null || ToolInput.Flag(input, "preview");
    public static XYZ Point(JsonElement v, bool mm = true)
    {
        var a = v.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (a.Length != 3 || a.Any(x => !double.IsFinite(x))) throw new ToolInputException("A point/vector must contain three finite coordinates [x,y,z].");
        return new XYZ(a[0], a[1], a[2]).Multiply(mm ? 1 / Units.MmPerFoot : 1);
    }
    public static double[] Mm(XYZ p) => [p.X * Units.MmPerFoot, p.Y * Units.MmPerFoot, p.Z * Units.MmPerFoot];
    public static double[] Vector(XYZ p) => [p.X, p.Y, p.Z];
    public static List<ElementId> Ids(JsonElement a, int max = 10000)
    {
        var ids = a.EnumerateArray().Select(v => new ElementId(v.GetInt64())).Distinct().ToList();
        if (ids.Count == 0 || ids.Count > max) throw new ToolInputException($"Supply between 1 and {max} element IDs.");
        return ids;
    }
    public static Element Element(Document doc, long id) => doc.GetElement(new ElementId(id)) ?? throw new ToolInputException($"Element {id} does not exist in this document.");
    public static Parameter? Parameter(Element element, string name, string guid)
    {
        if (guid.Length > 0)
            return element.get_Parameter(Guid.TryParse(guid, out var g) && g != Guid.Empty ? g : throw new ToolInputException("Invalid parameter GUID."));
        if (name.Length == 0) throw new ToolInputException("Supply parameter_name or parameter_guid.");
        var ps = element.GetParameters(name);
        if (ps.Count > 1) throw new ToolInputException($"Parameter '{name}' is ambiguous; use its shared GUID.");
        return ps.SingleOrDefault();
    }
    // No element objects escape this helper. Capture IDs/value snapshots before rollback.
    public static (T Value, List<string> Warnings) Commit<T>(Document doc, string label, bool preview, Func<T> action, Action? validateCommitted = null)
    {
        if (doc.IsModifiable) throw new InvalidOperationException("This operation requires no open transaction.");
        using var group = new TransactionGroup(doc, label);
        group.Start();
        var failures = new NativeFailures();
        try
        {
            T value;
            using (var tx = new Transaction(doc, label))
            {
                tx.Start();
                tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failures)
                    .SetClearAfterRollback(true).SetForcedModalHandling(false));
                value = action();
                ToolContext.ThrowIfCancelled();
                if (tx.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Revit rejected the operation: " + string.Join("; ", failures.Messages));
            }
            ToolContext.ThrowIfCancelled();
            validateCommitted?.Invoke();
            if (preview) group.RollBack(); else group.Assimilate();
            return (value, failures.Messages);
        }
        catch { if (group.GetStatus() == TransactionStatus.Started) group.RollBack(); throw; }
    }
}

internal sealed class NativeFailures : IFailuresPreprocessor
{
    public List<string> Messages { get; } = new();
    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        var error = false;
        foreach (var failure in accessor.GetFailureMessages())
        {
            Messages.Add(failure.GetDescriptionText());
            if (failure.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(failure);
            else error = true;
        }
        return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }
}
