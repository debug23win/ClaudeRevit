using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class CreateAreaReinforcement : IRevitTool
{
    public string Name => "create_area_reinforcement";

    public string Description =>
        "Creates area (mesh) reinforcement over the whole face of a structural wall or floor — " +
        "top/bottom (or exterior/interior) bars in both directions. The host must be structural. " +
        "Spacing and bar types per layer come from the area reinforcement type defaults and can " +
        "be adjusted afterwards with set_parameter. Use list_rebar_types to pick a bar type.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["host_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Element id of the structural wall or floor." }),
            ["bar_type_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional rebar bar type name (defaults to the first available)." }),
            ["major_dir_x"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Optional major direction X. Defaults: wall — along the wall; floor — model X axis." }),
            ["major_dir_y"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Optional major direction Y." }),
            ["major_dir_z"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Optional major direction Z (walls only)." })
        },
        Required = ["host_id"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var host = doc.GetElement(new ElementId(input["host_id"].GetInt64()))
            ?? throw new InvalidOperationException("Host element not found.");
        if (host is not Wall && host is not Floor)
            throw new InvalidOperationException(
                $"Element {host.Id.Value} ({host.Category?.Name}) is not a wall or floor. " +
                "Area reinforcement supports structural walls and floors only.");

        var hostData = RebarHostData.GetRebarHostData(host);
        if (hostData == null || !hostData.IsValidHost())
            throw new InvalidOperationException(
                $"Element {host.Id.Value} is not a valid rebar host — make sure it is structural.");

        RebarBarType barType;
        if (input.TryGetValue("bar_type_name", out var bt) && bt.ValueKind == JsonValueKind.String)
        {
            var wanted = bt.GetString();
            barType = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarBarType))
                .Cast<RebarBarType>()
                .FirstOrDefault(t => t.Name == wanted)
                ?? throw new InvalidOperationException(
                    $"Rebar bar type '{wanted}' not found. Call list_rebar_types to see available types.");
        }
        else
        {
            barType = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarBarType))
                .Cast<RebarBarType>()
                .FirstOrDefault()
                ?? throw new InvalidOperationException("No rebar bar types are loaded in this project.");
        }

        var areaType = new FilteredElementCollector(doc)
            .OfClass(typeof(AreaReinforcementType))
            .Cast<AreaReinforcementType>()
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No area reinforcement types exist in this project.");

        XYZ majorDir;
        if (input.ContainsKey("major_dir_x") || input.ContainsKey("major_dir_y") || input.ContainsKey("major_dir_z"))
        {
            majorDir = new XYZ(
                input.TryGetValue("major_dir_x", out var mx) ? mx.GetDouble() : 0,
                input.TryGetValue("major_dir_y", out var my) ? my.GetDouble() : 0,
                input.TryGetValue("major_dir_z", out var mz) ? mz.GetDouble() : 0);
            if (majorDir.GetLength() < 1e-9)
                throw new InvalidOperationException("Major direction vector must be non-zero.");
            majorDir = majorDir.Normalize();
        }
        else if (host is Wall wall && wall.Location is LocationCurve lc)
        {
            var curve = lc.Curve;
            majorDir = (curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize();
        }
        else
        {
            majorDir = XYZ.BasisX;
        }

        var area = AreaReinforcement.Create(
            doc, host, majorDir, areaType.Id, barType.Id, ElementId.InvalidElementId);

        return JsonSerializer.Serialize(new
        {
            id = area.Id.Value,
            type = "AreaReinforcement",
            area_type = areaType.Name,
            bar_type = barType.Name,
            host_id = host.Id.Value,
            note = "Layer spacings/bar types use the type defaults — adjust with set_parameter on this element if needed."
        });
    }
}
