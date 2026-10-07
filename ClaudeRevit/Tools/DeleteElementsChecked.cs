using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class DeleteElementsChecked : IRevitTool
{
    public string Name => "delete_elements_checked";
    public string Description => "Preview actual Revit deletion including every dependent element by committing then rolling back. preview defaults true. To apply, send preview=false, document_key and expected_deleted_ids from the preview; refuse if the cascade changed. No preview IDs are retained in the model.";
    public bool RequiresTransaction => false;
    public bool RequiresConfirmation => true;
    public bool RequiresNoTurnGroup => true;
    public bool MutatesWithoutTransaction => true;
    public bool InvalidatesCatalog => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["element_ids"] = NativeToolUtil.Array("integer", "Requested IDs to delete (max 10000)."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true. False applies only the previously inspected cascade."),
        ["document_key"] = NativeToolUtil.Field("string", "Document key returned by preview; required to apply."),
        ["expected_deleted_ids"] = NativeToolUtil.Array("integer", "Complete deleted_ids from preview; required to apply.")
    }, "element_ids");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var preview = NativeToolUtil.Preview(input);
        var key = Services.DocumentSessions.Key(doc);
        var ids = NativeToolUtil.Ids(ToolInput.Required(input, "element_ids"));
        foreach (var id in ids) NativeToolUtil.Element(doc, id.Value);
        HashSet<long>? expected = null;
        if (!preview)
        {
            if (NativeToolUtil.Text(input, "document_key") != key) throw new ToolInputException("The deletion preview belongs to another document. Preview again.");
            if (!input.TryGetValue("expected_deleted_ids", out var e)) throw new ToolInputException("Preview first, then supply its complete expected_deleted_ids.");
            expected = NativeToolUtil.Ids(e, 100000).Select(id => id.Value).ToHashSet();
        }
        var (deleted, warnings) = NativeToolUtil.Commit(doc, "Claude: checked deletion", preview, () =>
        {
            var result = doc.Delete(ids).Select(id => id.Value).Order().ToArray();
            if (expected != null && !expected.SetEquals(result)) throw new InvalidOperationException("Deletion cascade changed since preview; nothing was deleted. Preview again.");
            return result;
        });
        return Services.Json.Serialize(new { preview, applied = !preview, document_key = key, requested_ids = ids.Select(id => id.Value), deleted_ids = deleted,
            dependent_ids = deleted.Except(ids.Select(id => id.Value)), warnings });
    }
}
