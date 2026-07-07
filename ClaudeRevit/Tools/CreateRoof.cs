using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class CreateRoof : IRevitTool
{
    public string Name => "create_roof";

    public string Description =>
        "Creates a flat footprint roof from a closed boundary of plan-coordinate points (in feet) on a named level. " +
        "Provide points in order; the boundary closes automatically. Minimum 3 points. " +
        "All spatial values are in feet.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["points"] = JsonSerializer.SerializeToElement(new
            {
                type = "array",
                minItems = 3,
                description = "Boundary points. Each is { \"x\": <feet>, \"y\": <feet> }.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        x = new { type = "number" },
                        y = new { type = "number" }
                    },
                    required = new[] { "x", "y" }
                }
            }),
            ["level_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Host level name." }),
            ["roof_type_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional roof type name. Defaults to first available." })
        },
        Required = ["points", "level_name"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var levelName = input["level_name"].GetString()!;

        var pts = input["points"].EnumerateArray()
            .Select(p => new XYZ(p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble(), 0))
            .ToList();
        if (pts.Count < 3)
            throw new InvalidOperationException($"Roof requires at least 3 points (got {pts.Count}).");

        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .FirstOrDefault(l => l.Name == levelName)
            ?? throw new InvalidOperationException($"Level '{levelName}' not found.");

        var roofTypes = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>()
            .ToList();

        RoofType roofType;
        if (input.TryGetValue("roof_type_name", out var rt) && rt.ValueKind == JsonValueKind.String)
        {
            var name = rt.GetString();
            roofType = roofTypes.FirstOrDefault(t => t.Name == name)
                ?? roofTypes.FirstOrDefault(t => string.Equals(t.Name?.Trim(), name?.Trim(),
                       StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    NameResolve.NotFound(name, "Roof type", roofTypes.Select(t => t.Name)));
        }
        else
        {
            // Prefer a type with a compound structure (a Basic Roof): the first RoofType in
            // the collector can be a sloped-glazing type, which a footprint roof can't use.
            roofType = roofTypes.FirstOrDefault(t => t.GetCompoundStructure() != null)
                ?? roofTypes.FirstOrDefault()
                ?? throw new InvalidOperationException("No roof types are loaded in this document.");
        }

        var curveArray = new CurveArray();
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            if (a.IsAlmostEqualTo(b))
                throw new InvalidOperationException($"Boundary points {i} and {(i + 1) % pts.Count} are identical.");
            curveArray.Append(Line.CreateBound(a, b));
        }

        // NewFootPrintRoof's implementation reads the incoming ModelCurveArray (it fills the
        // caller's array rather than allocating one), so the argument must be a live instance —
        // `out _` passes null and every call dies with "Value cannot be null".
        var footprintMapping = new ModelCurveArray();
        var roof = doc.Create.NewFootPrintRoof(curveArray, level, roofType, out footprintMapping);

        return JsonSerializer.Serialize(new
        {
            id = roof.Id.Value,
            type = "Roof",
            type_name = roofType.Name,
            level = level.Name,
            boundary_points = pts.Count
        });
    }
}
