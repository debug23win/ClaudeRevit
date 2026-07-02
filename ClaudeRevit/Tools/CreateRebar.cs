using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class CreateRebar : IRevitTool
{
    public string Name => "create_rebar";

    public string Description =>
        "Creates straight rebar (a single bar or a linear set) inside a structural host " +
        "element — wall, floor, column, beam or foundation. The bar runs from start to end " +
        "point (feet, model coordinates). With count > 1 the set is distributed along the " +
        "distribution vector with the given spacing. The host must be a valid rebar host " +
        "(structural). Use list_rebar_types first to pick a bar type name.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["host_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Element id of the structural host." }),
            ["start_x"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Bar start X in feet." }),
            ["start_y"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Bar start Y in feet." }),
            ["start_z"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Bar start Z in feet." }),
            ["end_x"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Bar end X in feet." }),
            ["end_y"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Bar end Y in feet." }),
            ["end_z"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Bar end Z in feet." }),
            ["bar_type_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional rebar bar type name (defaults to the first available)." }),
            ["count"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Number of bars in the set (default 1).", minimum = 1 }),
            ["spacing_ft"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Spacing between bars in feet (required when count > 1).", minimum = 0.01 }),
            ["dist_x"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Optional distribution direction X (must be perpendicular to the bar). Default: computed automatically." }),
            ["dist_y"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Optional distribution direction Y." }),
            ["dist_z"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Optional distribution direction Z." })
        },
        Required = ["host_id", "start_x", "start_y", "start_z", "end_x", "end_y", "end_z"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var host = doc.GetElement(new ElementId(input["host_id"].GetInt64()))
            ?? throw new InvalidOperationException("Host element not found.");

        var hostData = RebarHostData.GetRebarHostData(host);
        if (hostData == null || !hostData.IsValidHost())
            throw new InvalidOperationException(
                $"Element {host.Id.Value} ({host.Category?.Name}) is not a valid rebar host. " +
                "The element must be structural (e.g. structural wall/floor/column/framing/foundation).");

        var start = new XYZ(input["start_x"].GetDouble(), input["start_y"].GetDouble(), input["start_z"].GetDouble());
        var end = new XYZ(input["end_x"].GetDouble(), input["end_y"].GetDouble(), input["end_z"].GetDouble());
        if (start.DistanceTo(end) < 0.01)
            throw new InvalidOperationException("Start and end points are (nearly) identical.");

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

        var dir = (end - start).Normalize();

        // The normal must be perpendicular to the bar; for a set it is also the
        // distribution direction.
        XYZ norm;
        if (input.TryGetValue("dist_x", out var dx) || input.TryGetValue("dist_y", out _) || input.TryGetValue("dist_z", out _))
        {
            norm = new XYZ(
                input.TryGetValue("dist_x", out dx) ? dx.GetDouble() : 0,
                input.TryGetValue("dist_y", out var dy) ? dy.GetDouble() : 0,
                input.TryGetValue("dist_z", out var dz) ? dz.GetDouble() : 0);
            if (norm.GetLength() < 1e-9)
                throw new InvalidOperationException("Distribution vector must be non-zero.");
            norm = norm.Normalize();
            if (Math.Abs(norm.DotProduct(dir)) > 0.01)
                throw new InvalidOperationException(
                    "Distribution vector must be perpendicular to the bar direction.");
        }
        else
        {
            // pick a sensible perpendicular: vertical bars distribute along X,
            // horizontal bars distribute along Z
            var seed = Math.Abs(dir.DotProduct(XYZ.BasisZ)) > 0.9 ? XYZ.BasisX : XYZ.BasisZ;
            norm = (seed - dir.Multiply(seed.DotProduct(dir))).Normalize();
        }

        var curves = new List<Curve> { Line.CreateBound(start, end) };
        // Default BarTerminationsData = straight bar, no hooks (Revit 2027 API)
        using var terminations = new BarTerminationsData(doc);
        var rebar = Rebar.CreateFromCurves(
            doc, RebarStyle.Standard, barType, host, norm, curves,
            terminations, useExistingShapeIfPossible: true, createNewShape: true);

        int count = input.TryGetValue("count", out var c) ? c.GetInt32() : 1;
        double? spacing = input.TryGetValue("spacing_ft", out var sp) ? sp.GetDouble() : null;
        if (count > 1)
        {
            if (spacing is null or <= 0)
                throw new InvalidOperationException("spacing_ft is required when count > 1.");
            rebar.GetShapeDrivenAccessor().SetLayoutAsNumberWithSpacing(
                count, spacing.Value, true, true, true);
        }

        return JsonSerializer.Serialize(new
        {
            id = rebar.Id.Value,
            type = "Rebar",
            bar_type = barType.Name,
            host_id = host.Id.Value,
            bars = count,
            length_ft = start.DistanceTo(end),
            spacing_ft = count > 1 ? spacing : null
        });
    }
}
