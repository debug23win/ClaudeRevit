using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Creates a new wall type by duplicating an existing basic wall type and (optionally) setting
// its thickness and core material. This is what was missing when the model needed a "Б25
// 200мм" (concrete 200 mm) type that wasn't in the project — it had no tool and fell back to
// brittle C#.
public class CreateWallType : IRevitTool
{
    public string Name => "create_wall_type";

    public string Description =>
        "Creates a new wall type by duplicating an existing basic wall type, optionally setting its total " +
        "thickness (mm) and core material. Use this before create_wall when the type you want doesn't exist " +
        "yet. If a type with the given name already exists it is returned unchanged.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Name for the new wall type, e.g. 'Б25 200мм'."
            }),
            ["based_on"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Optional existing basic wall type to copy from (defaults to a suitable one)."
            }),
            ["thickness_mm"] = JsonSerializer.SerializeToElement(new
            {
                type = "number",
                description = "Optional total wall thickness in millimetres (sets the core layer width)."
            }),
            ["material_name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Optional core material name (e.g. 'Concrete')."
            })
        },
        Required = ["name"]
    };

    public bool RequiresTransaction => true;

    private const double MmToFeet = 1.0 / 304.8;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var name = input["name"].GetString();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("name is empty.");

        var allTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();

        var existing = allTypes.FirstOrDefault(t => t.Name == name);
        if (existing != null)
            return JsonSerializer.Serialize(new
            {
                created = false, id = existing.Id.Value, name,
                note = "A wall type with this name already exists; returned unchanged."
            });

        // Base type: the named one, else any basic wall with a compound structure (a
        // curtain/stacked wall can't be given a simple thickness).
        WallType baseType = input.TryGetValue("based_on", out var b) && b.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(b.GetString())
            ? NameResolve.ByName<WallType>(doc, b.GetString(), "Wall type")
            : allTypes.FirstOrDefault(t => t.Kind == WallKind.Basic && t.GetCompoundStructure() != null)
              ?? throw new InvalidOperationException("No basic wall type to copy from in this document.");

        var newType = baseType.Duplicate(name) as WallType
            ?? throw new InvalidOperationException("Failed to duplicate the wall type.");

        double? thicknessMm = ToolInput.OptionalDouble(input, "thickness_mm");
        string? appliedMaterial = null;

        var cs = newType.GetCompoundStructure();
        if (cs != null && (thicknessMm.HasValue ||
                           (input.TryGetValue("material_name", out var m) && m.ValueKind == JsonValueKind.String)))
        {
            var idx = cs.GetFirstCoreLayerIndex();
            if (thicknessMm.HasValue)
            {
                try { cs.SetLayerWidth(idx, thicknessMm.Value * MmToFeet); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Created the type but could not set thickness: {ex.Message}. The base type may have " +
                        "multiple layers — pick a single-layer base with 'based_on'.");
                }
            }
            if (input.TryGetValue("material_name", out var mat) && mat.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(mat.GetString()))
            {
                var material = NameResolve.ByName<Material>(doc, mat.GetString(), "Material");
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
