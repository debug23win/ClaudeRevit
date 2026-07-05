using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Batch position/orientation/bounding-box reader in millimetres. The family work kept
// scripting "for each id, print LocationPoint and bbox in mm" to verify where nested
// instances actually sit; this returns that for many elements at once.
public class GetElementLocations : IRevitTool
{
    public string Name => "get_element_locations";

    public string Description =>
        "Returns the location point, facing/hand orientation and bounding box (all in MILLIMETRES) for " +
        "one or more elements, in a single call. Handy for checking where nested family instances or " +
        "placed components actually sit. Pass a list of element ids.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["element_ids"] = JsonSerializer.SerializeToElement(new
            {
                type = "array",
                items = new { type = "integer" },
                description = "Element ids to report."
            })
        },
        Required = ["element_ids"]
    };

    public bool RequiresTransaction => false;

    private const double FeetToMm = 304.8;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        if (!input.TryGetValue("element_ids", out var idsEl) || idsEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("element_ids must be an array of integers.");

        var results = new List<object>();
        foreach (var el in idsEl.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var raw))
            {
                results.Add(new { error = "not an integer id", value = el.ToString() });
                continue;
            }
            var id = new ElementId(raw);
            var e = doc.GetElement(id);
            if (e == null)
            {
                results.Add(new { id = raw, error = "not found" });
                continue;
            }

            double[]? loc = null;
            if (e.Location is LocationPoint lp)
                loc = new[] { lp.Point.X * FeetToMm, lp.Point.Y * FeetToMm, lp.Point.Z * FeetToMm };

            double[]? facing = null, hand = null;
            if (e is FamilyInstance fi)
            {
                try { facing = new[] { fi.FacingOrientation.X, fi.FacingOrientation.Y, fi.FacingOrientation.Z }; } catch { }
                try { hand = new[] { fi.HandOrientation.X, fi.HandOrientation.Y, fi.HandOrientation.Z }; } catch { }
            }

            object? bbox = null;
            var bb = e.get_BoundingBox(null);
            if (bb != null)
                bbox = new
                {
                    min = new[] { bb.Min.X * FeetToMm, bb.Min.Y * FeetToMm, bb.Min.Z * FeetToMm },
                    max = new[] { bb.Max.X * FeetToMm, bb.Max.Y * FeetToMm, bb.Max.Z * FeetToMm }
                };

            results.Add(new
            {
                id = raw,
                type = e.GetType().Name,
                category = e.Category?.Name,
                location_mm = loc,
                facing = facing,
                hand = hand,
                bbox_mm = bbox
            });
        }

        return JsonSerializer.Serialize(new { count = results.Count, elements = results });
    }
}
