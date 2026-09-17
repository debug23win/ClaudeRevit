using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Assemblies — the fabrication/shop-drawing workflow. Group a set of elements into an assembly and
// Revit can generate its own dedicated views (plan/elevations/3D) and a part list, which is exactly
// how a steel connection or a precast unit gets detailed. Identical assemblies share a type, so the
// second identical unit reuses the first one's drawings instead of duplicating them.
public class CreateAssembly : IRevitTool
{
    public string Name => "create_assembly";

    public string Description =>
        "Create an assembly from elements (steel connections, precast units, rebar cages) and optionally " +
        "generate its shop-drawing views: 3D, plan, elevations and a part list. All members must share one " +
        "category context — pass the elements you want detailed together. " +
        "Revit reuses the assembly TYPE for identical member sets, so repeated units share drawings. " +
        "Returns the assembly id, its type name and every view created.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["element_ids"] = JsonSerializer.SerializeToElement(new
            {
                type = "array", minItems = 1,
                description = "Elements to assemble.",
                items = new { type = "integer" }
            }),
            ["assembly_name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Name for the assembly type. Omit to let Revit name it."
            }),
            ["create_views"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean", description = "Generate 3D / plan / elevation / part-list views. Default false."
            })
        },
        Required = ["element_ids"]
    };

    public bool RequiresTransaction => true;


    // Adds or renames something the project catalog lists.

    public bool InvalidatesCatalog => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var ids = input["element_ids"].EnumerateArray()
            .Select(e => new ElementId(e.GetInt64())).ToList();
        if (ids.Count == 0) throw new InvalidOperationException("element_ids is empty.");

        var elements = ids.Select(doc.GetElement).Where(e => e != null).ToList()!;
        if (elements.Count == 0) throw new InvalidOperationException("None of the given ids exist.");

        // Revit derives the assembly's "naming category" from a member; use the most common one so a
        // mixed set still resolves to something sensible.
        var catId = elements
            .Where(e => e!.Category != null)
            .GroupBy(e => e!.Category!.Id)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? ElementId.InvalidElementId;

        if (catId == ElementId.InvalidElementId)
            throw new InvalidOperationException("The elements have no usable category for an assembly.");

        var memberIds = elements.Select(e => e!.Id).ToList();
        if (!AssemblyInstance.IsValidNamingCategory(doc, catId, memberIds))
            throw new InvalidOperationException(
                "That category can't name an assembly for this member set — try elements of one primary category.");
        if (!AssemblyInstance.AreElementsValidForAssembly(doc, memberIds, ElementId.InvalidElementId))
            throw new InvalidOperationException(
                "These elements can't form an assembly (already in one, in different documents/design options, " +
                "or an unsupported category).");

        var assembly = AssemblyInstance.Create(doc, memberIds, catId);

        // The type must exist before it can be renamed, so commit the creation first.
        doc.Regenerate();

        string? typeName = null;
        if (input.TryGetValue("assembly_name", out var an) && an.ValueKind == JsonValueKind.String)
        {
            var wanted = an.GetString();
            if (!string.IsNullOrWhiteSpace(wanted))
            {
                // No validity pre-check exists for assembly type names; Revit rejects a clash by
                // throwing, so attempt it and keep the auto-generated name on failure.
                try
                {
                    assembly.AssemblyTypeName = wanted;
                    typeName = wanted;
                }
                catch { /* name taken by a different member set — keep Revit's */ }
            }
        }
        typeName ??= SafeTypeName(assembly);

        var views = new List<object>();
        if (input.TryGetValue("create_views", out var cv) && cv.ValueKind == JsonValueKind.True)
        {
            doc.Regenerate();
            Add(views, "3d", () => AssemblyViewUtils.Create3DOrthographic(doc, assembly.Id));
            Add(views, "plan", () => AssemblyViewUtils.CreateDetailSection(doc, assembly.Id, AssemblyDetailViewOrientation.HorizontalDetail));
            Add(views, "elevation_front", () => AssemblyViewUtils.CreateDetailSection(doc, assembly.Id, AssemblyDetailViewOrientation.ElevationFront));
            Add(views, "elevation_left", () => AssemblyViewUtils.CreateDetailSection(doc, assembly.Id, AssemblyDetailViewOrientation.ElevationLeft));
            Add(views, "part_list", () => AssemblyViewUtils.CreatePartList(doc, assembly.Id));
        }

        return Services.Json.Serialize(new
        {
            assembly_id = assembly.Id.Value,
            assembly_type = typeName,
            members = memberIds.Count,
            naming_category = doc.GetElement(catId)?.Name ?? elements[0]!.Category?.Name,
            views,
            note = "Identical member sets share the assembly type, so their shop drawings are reused."
        });
    }

    private static void Add(List<object> sink, string kind, Func<View> make)
    {
        try
        {
            var v = make();
            if (v != null) sink.Add(new { kind, id = v.Id.Value, name = v.Name });
        }
        catch (Exception ex)
        {
            sink.Add(new { kind, error = ex.Message.Length > 120 ? ex.Message.Substring(0, 120) + "…" : ex.Message });
        }
    }

    private static string? SafeTypeName(AssemblyInstance a)
    {
        try { return a.AssemblyTypeName; } catch { return null; }
    }
}
