using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class CreatePathReinforcement : IRevitTool
{
    public string Name => "create_path_reinforcement";

    public string Description =>
        "Creates path reinforcement in a structural floor or wall: bars perpendicular to a " +
        "polyline path, for LOCAL extra reinforcement (openings, edges, supports) — unlike " +
        "create_area_reinforcement which covers the whole face. The path is a sequence of " +
        "points (feet, model coordinates) on the host face plane. Bars go to the face chosen " +
        "by 'face' (top/bottom for floors, exterior/interior for walls). Spacing, bar length " +
        "and other settings can be adjusted afterwards with set_parameter on the created " +
        "element (parameters like 'Bar Spacing' work by their localized names too).";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["host_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Element id of the structural floor or wall." }),
            ["points"] = JsonSerializer.SerializeToElement(new
            {
                type = "array",
                minItems = 2,
                description = "Path points in order (open polyline, not closed). Each is { x, y, z } in feet; for a floor use the top-face elevation, for a wall points along the wall.",
                items = new
                {
                    type = "object",
                    properties = new { x = new { type = "number" }, y = new { type = "number" }, z = new { type = "number" } },
                    required = new[] { "x", "y", "z" }
                }
            }),
            ["bar_type_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional rebar bar type name (defaults to the first available)." }),
            ["spacing_ft"] = JsonSerializer.SerializeToElement(new { type = "number", minimum = 0.01, description = "Optional bar spacing in feet (defaults to the path reinforcement type's value)." }),
            ["face"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                @enum = new[] { "top", "bottom", "exterior", "interior" },
                description = "Which face the bars reinforce: top/bottom for floors, exterior/interior for walls. Default: the type's default."
            }),
            ["flip"] = JsonSerializer.SerializeToElement(new { type = "boolean", description = "Flip which side of the path the bars extend to (default false)." })
        },
        Required = ["host_id", "points"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var host = ReinforcementHelpers.GetValidRebarHost(doc, input);
        if (host is not Wall && host is not Floor)
            throw new InvalidOperationException(
                $"Element {host.Id.Value} ({host.Category?.Name}) is not a wall or floor. " +
                "Path reinforcement supports structural walls and floors only.");

        var pts = input["points"].EnumerateArray()
            .Select(p => new XYZ(p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble(), p.GetProperty("z").GetDouble()))
            .ToList();
        if (pts.Count < 2)
            throw new InvalidOperationException("At least 2 path points are required.");

        var curves = new List<Curve>();
        for (int i = 0; i < pts.Count - 1; i++)
        {
            if (pts[i].DistanceTo(pts[i + 1]) < 0.01)
                throw new InvalidOperationException($"Path points {i} and {i + 1} are (nearly) identical.");
            curves.Add(Line.CreateBound(pts[i], pts[i + 1]));
        }

        var barType = ReinforcementHelpers.ResolveBarType(doc, input);

        // CreateDefaultPathReinforcementType returns the new type's id, not the type itself.
        var pathTypeId = new FilteredElementCollector(doc)
                .OfClass(typeof(PathReinforcementType)).Cast<PathReinforcementType>()
                .FirstOrDefault()?.Id
            ?? PathReinforcementType.CreateDefaultPathReinforcementType(doc);
        var pathTypeName = doc.GetElement(pathTypeId).Name;

        bool flip = input.TryGetValue("flip", out var f) && f.ValueKind == JsonValueKind.True;

        var path = PathReinforcement.Create(
            doc, host, curves, flip, pathTypeId, barType.Id,
            ElementId.InvalidElementId, ElementId.InvalidElementId);

        string? faceResult = null;
        if (input.TryGetValue("face", out var faceEl) && faceEl.ValueKind == JsonValueKind.String)
        {
            var face = faceEl.GetString()!.ToLowerInvariant();
            path.PrimaryBarOrientation = face is "top" or "exterior"
                ? ReinforcementBarOrientation.TopOrExterior
                : ReinforcementBarOrientation.BottomOrInterior;
            faceResult = path.PrimaryBarOrientation.ToString();
        }

        double? spacingResult = null;
        if (input.TryGetValue("spacing_ft", out var sp) && sp.ValueKind == JsonValueKind.Number)
        {
            var spacingParam = path.get_Parameter(BuiltInParameter.PATH_REIN_SPACING);
            if (spacingParam is { IsReadOnly: false })
            {
                spacingParam.Set(sp.GetDouble());
                spacingResult = spacingParam.AsDouble();
            }
        }

        // Same guard as area reinforcement: invalid layouts must throw inside the
        // transaction (→ rollback) instead of corrupting the element.
        try
        {
            doc.Regenerate();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Revit rejected this path reinforcement (invalid geometry during regeneration): " +
                ex.Message + ". Make sure the path lies on the host's face plane and stays inside the host.");
        }

        var numberOfBars = path.get_Parameter(BuiltInParameter.PATH_REIN_NUMBER_OF_BARS)?.AsInteger();

        return JsonSerializer.Serialize(new
        {
            id = path.Id.Value,
            type = "PathReinforcement",
            path_type = pathTypeName,
            bar_type = barType.Name,
            host_id = host.Id.Value,
            segments = curves.Count,
            face = faceResult,
            spacing_ft = spacingResult,
            number_of_bars = numberOfBars,
            note = "Adjust spacing/bar type/face later with set_parameter on this element."
        });
    }
}
