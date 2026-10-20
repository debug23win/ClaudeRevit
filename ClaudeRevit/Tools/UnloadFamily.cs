using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class UnloadFamily : IRevitTool
{
    public string Name => "unload_family";

    public string Description =>
        "Removes a loaded family from the document. All instances of this family must already be deleted, " +
        "otherwise the operation is refused before deletion. Use list_loaded_families to find the family_id.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["family_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Family id (from list_loaded_families)." })
        },
        Required = ["family_id"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = ToolContext.UiDocument(app)?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var id = new ElementId(ToolInput.RequiredLong(input, "family_id"));
        var family = doc.GetElement(id) as Family
            ?? throw new InvalidOperationException($"Element {id.Value} is not a Family.");

        var symbols = family.GetFamilySymbolIds().ToHashSet();
        var instances = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
            .Where(i => symbols.Contains(i.GetTypeId())).Select(i => i.Id.Value).ToArray();
        if (instances.Length > 0) throw new ToolInputException($"Family has {instances.Length} placed instances; nothing was deleted.");
        // Profiles, grid and level heads and view titles are used through a type parameter, not
        // through instances. Revit resets such a reference to <none> instead of deleting the
        // type, so the deletion rehearsal below cannot see it: look for it directly.
        var referencing = new FilteredElementCollector(doc).WhereElementIsElementType()
            .Where(t => !symbols.Contains(t.Id))
            .Where(t => t.Parameters.Cast<Parameter>().Any(p => p.StorageType == StorageType.ElementId && symbols.Contains(p.AsElementId())))
            .Select(t => $"{(t as ElementType)?.FamilyName}: {t.Name}".TrimStart(':', ' '))
            .Take(20).ToArray();
        if (referencing.Length > 0)
            throw new ToolInputException($"Types reference this family's types through a parameter (profile, head, title…): {string.Join(", ", referencing)}. Nothing was deleted.");
        var allowed = symbols.Append(id).ToHashSet();
        using (var preview = new SubTransaction(doc))
        {
            preview.Start();
            var cascade = doc.Delete(id).ToArray();
            preview.RollBack();
            if (cascade.Any(x => !allowed.Contains(x)))
                throw new ToolInputException("Family deletion would remove other dependent elements; nothing was deleted.");
        }
        var familyName = ((Family)doc.GetElement(id)).Name;
        var deleted = doc.Delete(id);

        return Services.Json.Serialize(new
        {
            removed_family = familyName,
            deleted_count = deleted.Count
        });
    }
}
