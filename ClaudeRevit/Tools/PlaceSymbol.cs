using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class PlaceSymbol : IRevitTool
{
    public string Name => "place_symbol";

    public string Description =>
        "Places an annotation symbol (e.g. north arrow, graphic scale, custom symbol family) in a view at " +
        "a plan-coordinate point (in feet). Use list_family_types to find symbol family types first.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["symbol_type_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Annotation symbol FamilySymbol id." }),
            ["x"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Position X (feet)." }),
            ["y"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Position Y (feet)." }),
            ["view_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Optional target view (default active view)." })
        },
        Required = ["symbol_type_id", "x", "y"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        View view;
        if (input.TryGetValue("view_id", out var vid) && vid.ValueKind == JsonValueKind.Number)
            view = doc.GetElement(new ElementId(vid.GetInt64())) as View
                ?? throw new InvalidOperationException("view_id is not a view.");
        else
            view = doc.ActiveView ?? throw new InvalidOperationException("No active view.");

        var typeId = new ElementId(input["symbol_type_id"].GetInt64());
        var symbol = doc.GetElement(typeId) as FamilySymbol
            ?? throw new InvalidOperationException($"Element {typeId.Value} is not a FamilySymbol.");

        if (!symbol.IsActive) symbol.Activate();
        doc.Regenerate();

        var x = input["x"].GetDouble();
        var y = input["y"].GetDouble();
        var instance = doc.Create.NewFamilyInstance(new XYZ(x, y, 0), symbol, view);

        return JsonSerializer.Serialize(new
        {
            id = instance.Id.Value,
            type = "AnnotationSymbol",
            family = symbol.FamilyName,
            type_name = symbol.Name,
            view = view.Name,
            position_ft = new { x, y }
        });
    }
}
