using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// Lists reference planes with their axis and position (mm) — the recurring "which reference
// plane sits where" inspection when dimensioning or constraining a family. Came up in every
// family-authoring session.
public class ListReferencePlanes : IRevitTool
{
    public string Name => "list_reference_planes";

    public string Description =>
        "Lists reference planes in the active document: id, name, which axis they are perpendicular to " +
        "(⊥X/⊥Y/⊥Z or skewed) and their position in mm along that axis. Optionally filter to planes " +
        "perpendicular to one axis. Use it to find the planes to dimension or constrain against in the " +
        "Family Editor.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["axis"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                @enum = new[] { "x", "y", "z" },
                description = "Only planes perpendicular to this axis (⊥X etc.). Omit for all."
            })
        },
        Required = []
    };

    public bool RequiresTransaction => false;

    private const double FeetToMm = Units.MmPerFoot;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var axisFilter = input.TryGetValue("axis", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString()?.Trim().ToLowerInvariant() : null;

        var list = new List<object>();
        foreach (var rp in new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>())
        {
            Plane plane;
            try { plane = rp.GetPlane(); } catch { continue; }
            var n = plane.Normal;
            var perpX = Math.Abs(n.X) > 0.9;
            var perpY = Math.Abs(n.Y) > 0.9;
            var perpZ = Math.Abs(n.Z) > 0.9;
            var axis = perpX ? "⊥X" : perpY ? "⊥Y" : perpZ ? "⊥Z" : "skewed";

            if (axisFilter == "x" && !perpX) continue;
            if (axisFilter == "y" && !perpY) continue;
            if (axisFilter == "z" && !perpZ) continue;

            // Signed distance of the plane from the world origin along its normal — the value
            // that matters for a ⊥-axis plane (its X/Y/Z coordinate).
            var posMm = plane.Origin.DotProduct(n) * FeetToMm;

            list.Add(new
            {
                id = rp.Id.Value,
                name = rp.Name,
                axis,
                position_mm = posMm,
                normal = new[] { n.X, n.Y, n.Z }
            });
        }

        return Json.Serialize(new { count = list.Count, reference_planes = list });
    }
}
