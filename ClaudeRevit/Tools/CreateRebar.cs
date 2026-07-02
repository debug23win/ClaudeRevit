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
        "Creates rebar (a single bar or a linear set) inside a structural host element — wall, " +
        "floor, column, beam or foundation. By default the bar is straight from start to end " +
        "point (feet, model coordinates). Pass shape_name to place a catalog rebar shape " +
        "(e.g. a GOST bent shape) instead: the shape is placed at the start point, its X axis " +
        "along start→end, and its dimensions can be set via shape_parameters. With count > 1 " +
        "the set is distributed along the distribution vector with the given spacing. " +
        "Use list_rebar_types first to pick bar type and shape names. Note: rebar is " +
        "associative to its host — when the host's type or size changes, Revit recalculates " +
        "bar lengths and counts automatically; do not recreate the rebar.";

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
            ["shape_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional rebar shape name from the catalog (see list_rebar_types). When set, the shape is placed at the start point with its X axis along start→end instead of creating a straight bar from curves." }),
            ["shape_parameters"] = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                description = "Optional shape dimension values, keyed by the parameter name as shown on the rebar instance (e.g. \"A\", \"B\" or localized names). Values are lengths in FEET.",
                additionalProperties = new { type = "number" }
            }),
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

        Rebar rebar;
        string? shapeName = null;
        if (input.TryGetValue("shape_name", out var sn) && sn.ValueKind == JsonValueKind.String)
        {
            shapeName = sn.GetString();
            var shape = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarShape))
                .Cast<RebarShape>()
                .FirstOrDefault(s => s.Name == shapeName)
                ?? throw new InvalidOperationException(
                    $"Rebar shape '{shapeName}' not found. Call list_rebar_types to see available shapes.");
            // The shape plane: X along the bar run, Y along the distribution normal.
            rebar = Rebar.CreateFromRebarShape(doc, shape, barType, host, start, dir, norm);
        }
        else
        {
            var curves = new List<Curve> { Line.CreateBound(start, end) };
            // Default BarTerminationsData = straight bar, no hooks (Revit 2027 API)
            using var terminations = new BarTerminationsData(doc);
            rebar = Rebar.CreateFromCurves(
                doc, RebarStyle.Standard, barType, host, norm, curves,
                terminations, useExistingShapeIfPossible: true, createNewShape: true);
        }

        // Shape dimension parameters (A, B, hook lengths…) live on the instance once a
        // shape-driven rebar exists. Applied by name; unknown/read-only names are reported
        // back instead of failing the whole call.
        var paramWarnings = new List<string>();
        if (input.TryGetValue("shape_parameters", out var shapeParams) && shapeParams.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in shapeParams.EnumerateObject())
            {
                var p = rebar.LookupParameter(prop.Name);
                if (p == null)
                    paramWarnings.Add($"Parameter '{prop.Name}' not found on the rebar instance.");
                else if (p.IsReadOnly)
                    paramWarnings.Add($"Parameter '{prop.Name}' is read-only.");
                else if (p.StorageType == StorageType.Double)
                    p.Set(prop.Value.GetDouble());
                else
                    paramWarnings.Add($"Parameter '{prop.Name}' is not a number ({p.StorageType}).");
            }
        }

        int count = input.TryGetValue("count", out var c) ? c.GetInt32() : 1;
        double? spacing = input.TryGetValue("spacing_ft", out var sp) ? sp.GetDouble() : null;
        if (count > 1)
        {
            if (spacing is null or <= 0)
                throw new InvalidOperationException("spacing_ft is required when count > 1.");
            rebar.GetShapeDrivenAccessor().SetLayoutAsNumberWithSpacing(
                count, spacing.Value, true, true, true);
        }

        // Regenerate inside the transaction so invalid geometry throws here (→ rollback)
        // rather than corrupting the element and crashing Revit on the next redraw.
        try
        {
            doc.Regenerate();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Revit rejected this rebar (invalid geometry during regeneration): " + ex.Message);
        }

        return JsonSerializer.Serialize(new
        {
            id = rebar.Id.Value,
            type = "Rebar",
            bar_type = barType.Name,
            shape = shapeName,
            host_id = host.Id.Value,
            bars = count,
            length_ft = start.DistanceTo(end),
            spacing_ft = count > 1 ? spacing : null,
            warnings = paramWarnings.Count > 0 ? paramWarnings : null
        });
    }
}
