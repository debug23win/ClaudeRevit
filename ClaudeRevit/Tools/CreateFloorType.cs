using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Creates a new floor type by duplicating an existing one and optionally setting its
// thickness and core material — the floor counterpart to create_wall_type (the field log
// had "Floor type 'Б25 300мм' not found" failing repeatedly with no way to make it).
public class CreateFloorType : IRevitTool
{
    public string Name => "create_floor_type";

    public string Description =>
        "Creates a new floor type by duplicating an existing floor type, optionally setting its total " +
        "thickness (mm) and core material. Use before create_floor when the type you want doesn't exist. " +
        "If a type with the given name already exists it is returned unchanged.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Name for the new floor type, e.g. 'Б25 300мм'."
            }),
            ["based_on"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Optional existing floor type to copy from (defaults to a suitable one)."
            }),
            ["thickness_mm"] = JsonSerializer.SerializeToElement(new
            {
                type = "number", description = "Optional total thickness in millimetres (sets the core layer width)."
            }),
            ["material_name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Optional core material name (e.g. 'Concrete')."
            })
        },
        Required = ["name"]
    };

    public bool RequiresTransaction => true;

    private const double MmToFeet = 1.0 / Units.MmPerFoot;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var name = input["name"].GetString();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("name is empty.");

        var allTypes = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().ToList();

        var existing = allTypes.FirstOrDefault(t => t.Name == name);
        if (existing != null)
            return JsonSerializer.Serialize(new
            {
                created = false, id = existing.Id.Value, name,
                note = "A floor type with this name already exists; returned unchanged."
            });

        FloorType baseType = input.TryGetValue("based_on", out var b) && b.ValueKind == JsonValueKind.String
                             && !string.IsNullOrWhiteSpace(b.GetString())
            ? NameResolve.ByName<FloorType>(doc, b.GetString(), "Floor type")
            : allTypes.FirstOrDefault(t => t.GetCompoundStructure() != null)
              ?? allTypes.FirstOrDefault()
              ?? throw new InvalidOperationException("No floor type to copy from in this document.");

        var newType = baseType.Duplicate(name) as FloorType
            ?? throw new InvalidOperationException("Failed to duplicate the floor type.");

        double? thicknessMm = ToolInput.OptionalDouble(input, "thickness_mm");
        var wantsMaterial = input.TryGetValue("material_name", out var matEl) &&
                            matEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(matEl.GetString());
        string? appliedMaterial = null;

        if (thicknessMm.HasValue || wantsMaterial)
        {
            var cs = newType.GetCompoundStructure();
            // Fail rather than falsely report a thickness/material that couldn't be applied.
            if (cs == null)
                throw new InvalidOperationException(
                    $"Base floor type '{baseType.Name}' has no editable layer structure, so thickness/material " +
                    "can't be set. Pass a single-layer floor type via 'based_on'.");

            var idx = cs.GetFirstCoreLayerIndex();
            if (thicknessMm.HasValue)
            {
                try { cs.SetLayerWidth(idx, thicknessMm.Value * MmToFeet); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Could not set thickness: {ex.Message}. The base type may have multiple/locked layers " +
                        "— pick a single-layer base with 'based_on'.");
                }
            }
            if (wantsMaterial)
            {
                var material = NameResolve.ByName<Material>(doc, matEl.GetString(), "Material");
                cs.SetMaterialId(idx, material.Id);
                appliedMaterial = material.Name;
            }
            newType.SetCompoundStructure(cs);
        }

        return JsonSerializer.Serialize(new
        {
            created = true,
            id = newType.Id.Value,
            name,
            based_on = baseType.Name,
            thickness_mm = thicknessMm,
            material = appliedMaterial
        });
    }
}
