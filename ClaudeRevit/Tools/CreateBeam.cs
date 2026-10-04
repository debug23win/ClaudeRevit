using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class CreateBeam : IRevitTool
{
    public string Name => "create_beam";

    public string Description =>
        "Creates a native straight or curved structural beam. Use legacy plan endpoints in feet on a named level, " +
        "or curve_mm: one line/three-point arc segment with absolute XYZ coordinates in millimetres. " +
        "If type_name is omitted, the first available structural framing type is used.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["start_x"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Beam start X (feet)." }),
            ["start_y"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Beam start Y (feet)." }),
            ["end_x"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Beam end X (feet)." }),
            ["end_y"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Beam end Y (feet)." }),
            ["level_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Reference level name." }),
            ["type_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional beam type name." }),
            ["curve_mm"] = NativeToolUtil.Any("Optional native path: [{start_mm:[x,y,z],end_mm:[x,y,z],mid_mm:[x,y,z]}]. mid_mm makes an arc. Exactly one line/arc; do not combine with legacy endpoint fields.")
        },
        Required = ["level_name"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = ToolContext.UiDocument(app)?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var levelName = input["level_name"].GetString()!;

        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .FirstOrDefault(l => l.Name == levelName)
            ?? throw new InvalidOperationException($"Level '{levelName}' not found.");

        Curve path;
        if (input.TryGetValue("curve_mm", out var curve))
        {
            if (new[] { "start_x", "start_y", "end_x", "end_y" }.Any(input.ContainsKey))
                throw new ToolInputException("Use curve_mm or legacy endpoints, not both.");
            var curves = NativeCurveInput.Read(curve, false);
            if (curves.Count != 1) throw new ToolInputException("A native beam needs exactly one line or arc segment.");
            path = curves[0];
        }
        else
        {
            double Coordinate(string key) => input.TryGetValue(key, out var v) && v.TryGetDouble(out var d) && double.IsFinite(d)
                ? d : throw new ToolInputException("Supply finite " + key + " in feet, or curve_mm.");
            path = Line.CreateBound(new XYZ(Coordinate("start_x"), Coordinate("start_y"), level.Elevation),
                new XYZ(Coordinate("end_x"), Coordinate("end_y"), level.Elevation));
        }

        FamilySymbol symbol;
        if (input.TryGetValue("type_name", out var tn) && tn.ValueKind == JsonValueKind.String)
        {
            var name = tn.GetString();
            symbol = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralFraming)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s => s.Name == name)
                ?? throw new InvalidOperationException($"Beam type '{name}' not found.");
        }
        else
        {
            symbol = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralFraming)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault()
                ?? throw new InvalidOperationException("No structural framing types loaded.");
        }

        // Regenerate ONLY when the type actually had to be activated. It is needed to make a
        // freshly activated symbol usable, not to place an element — and it is super-linear in
        // document size, so an unconditional call made a 50-item run_batch pay for 50 full
        // regenerations of a model that had nothing new to activate after the first.
        if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }

        var instance = doc.Create.NewFamilyInstance(path, symbol, level, StructuralType.Beam);

        return Services.Json.Serialize(new
        {
            id = instance.Id.Value,
            type = "Beam",
            family = symbol.FamilyName,
            type_name = symbol.Name,
            level = level.Name,
            curve_kind = path.GetType().Name,
            length_ft = path.Length
        });
    }
}
