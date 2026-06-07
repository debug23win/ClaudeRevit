using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class CreateIsolatedFoundation : IRevitTool
{
    public string Name => "create_isolated_foundation";

    public string Description =>
        "Places an isolated structural foundation (footing) at a plan-coordinate point on a level. " +
        "Needs a structural foundation family loaded (e.g. 'Footing-Rectangular'). " +
        "If type_name is omitted, the first available foundation type is used.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["x"] = JsonSerializer.SerializeToElement(new { type = "number", description = "X (feet)." }),
            ["y"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Y (feet)." }),
            ["level_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Reference level name." }),
            ["type_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional foundation type name." })
        },
        Required = ["x", "y", "level_name"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var x = input["x"].GetDouble();
        var y = input["y"].GetDouble();
        var levelName = input["level_name"].GetString()!;

        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .FirstOrDefault(l => l.Name == levelName)
            ?? throw new InvalidOperationException($"Level '{levelName}' not found.");

        FamilySymbol symbol;
        if (input.TryGetValue("type_name", out var tn) && tn.ValueKind == JsonValueKind.String)
        {
            var name = tn.GetString();
            symbol = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(s => s.Name == name)
                ?? throw new InvalidOperationException($"Foundation type '{name}' not found.");
        }
        else
        {
            symbol = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault()
                ?? throw new InvalidOperationException("No structural foundation types loaded.");
        }

        if (!symbol.IsActive) symbol.Activate();
        doc.Regenerate();

        var instance = doc.Create.NewFamilyInstance(
            new XYZ(x, y, level.Elevation), symbol, level, StructuralType.Footing);

        return JsonSerializer.Serialize(new
        {
            id = instance.Id.Value,
            type = "IsolatedFoundation",
            family = symbol.FamilyName,
            type_name = symbol.Name,
            level = level.Name,
            position_ft = new { x, y }
        });
    }
}
