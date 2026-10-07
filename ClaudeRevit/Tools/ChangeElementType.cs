using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class ChangeElementType : IRevitTool
{
    public string Name => "change_element_type";

    public string Description =>
        "Swaps the type of one or more elements to a new type id. The new type must be compatible with the " +
        "element's category — e.g. you can swap a Wall to a different WallType, but not to a FloorType. " +
        "Use list_family_types to find the right new_type_id. By default restore compatible writable instance parameters " +
        "after the swap and report changed parameters and replacement IDs. preserve_parameters selects critical names; " +
        "failure to retain them rolls that item back. Type-owned properties come from the new type. preview=true rolls back the whole call.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["element_ids"] = JsonSerializer.SerializeToElement(new
            {
                type = "array", minItems = 1,
                description = "Elements whose type should change.",
                items = new { type = "integer" }
            }),
            ["new_type_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "New ElementType id." }),
            ["preserve_instance_parameters"] = NativeToolUtil.Field("boolean", "Default true. Restore compatible writable instance values."),
            ["preserve_parameters"] = NativeToolUtil.Array("string", "Optional critical instance parameter names. All must survive the swap or it rolls back."),
            ["preview"] = NativeToolUtil.Field("boolean", "Default false for compatibility; true tests then rolls back.")
        },
        Required = ["element_ids", "new_type_id"]
    };

    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = ToolContext.UiDocument(app)?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var newTypeId = new ElementId(ToolInput.RequiredLong(input, "new_type_id"));
        var newType = doc.GetElement(newTypeId) as ElementType
            ?? throw new InvalidOperationException($"Element {newTypeId.Value} is not an ElementType.");

        var elementIds = ToolInput.RequiredArray(input, "element_ids").EnumerateArray()
            .Select(e => new ElementId(e.GetInt64())).ToList();

        var changed = new List<long>();
        var skipped = new List<object>();
        var reports = new List<object>();
        bool preserve = !input.TryGetValue("preserve_instance_parameters", out var keep) || keep.GetBoolean();
        var names = input.TryGetValue("preserve_parameters", out var ps) ? ps.EnumerateArray().Select(p=>p.GetString()??"").ToArray() : null;
        bool preview=input.TryGetValue("preview",out var pv)&&pv.GetBoolean();

        var (_, warnings) = NativeToolUtil.Commit(doc,"Claude: change element types",preview,()=>
        {
        foreach (var id in elementIds)
        {
            ToolContext.ThrowIfCancelled();
            var el = doc.GetElement(id);
            if (el == null) { skipped.Add(new { id = id.Value, reason = "not found" }); continue; }
            using var sub = new SubTransaction(doc); sub.Start();
            try
            {
                var result=TypeChangePreservation.Change(el,newTypeId,preserve,names);
                sub.Commit(); changed.Add(result.Element.Id.Value);
                reports.Add(new { previous_id=id.Value,current_id=result.Element.Id.Value,parameter_changes=result.Changes });
            }
            catch (OperationCanceledException) { sub.RollBack(); throw; }
            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
            catch (Exception ex) { sub.RollBack(); skipped.Add(new { id = id.Value, reason = ex.Message }); }
        }
        return true;
        });

        // A swapped title block can have a different printable area, leaving viewports under
        // the new frame or outside it — the caller should re-check the sheet layout.
        string? note = null;
        if (changed.Count > 0 && newType.Category != null &&
            newType.Category.BuiltInCategory == BuiltInCategory.OST_TitleBlocks)
        {
            note = "Title block changed. The new frame's printable area may differ from the old " +
                   "one — check viewport positions with get_sheet_views (and export_image to " +
                   "verify visually), then adjust with move_viewport_on_sheet if needed.";
        }

        return Services.Json.Serialize(new
        {
            // ok:false when nothing actually changed — otherwise the empty transaction reads as
            // success in a truncated preview and the model moves on.
            ok = changed.Count > 0,
            new_type = newType.Name,
            changed_count = changed.Count,
            skipped_count = skipped.Count,
            skipped,
            preview, changed_ids=changed, reports, warnings,
            preview_ids_are_temporary=preview,
            note
        });
    }
}
