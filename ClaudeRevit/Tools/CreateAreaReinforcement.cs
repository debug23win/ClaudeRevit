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
    // Area reinforcement regeneration is a known Revit crash risk on bad geometry;
    // surface it to the user before it runs.
    public bool RequiresConfirmation => true;

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

        var barType = ReinforcementHelpers.ResolveBarType(doc, input);

        var areaType = new FilteredElementCollector(doc)
            .OfClass(typeof(AreaReinforcementType))
            .Cast<AreaReinforcementType>()
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No area reinforcement types exist in this project.");

        // The major direction MUST lie in the plane of the host reinforcement face.
        // If it has any component along the face normal, Revit builds a singular
        // (non-invertible) per-bar transform during regeneration, silently commits a
        // corrupt element, then crashes when the view later draws it. So we determine
        // the face normal and project the candidate direction into the plane.
        var normal = HostFaceNormal(host);

        XYZ candidate;
        if (input.ContainsKey("major_dir_x") || input.ContainsKey("major_dir_y") || input.ContainsKey("major_dir_z"))
        {
            candidate = new XYZ(
                input.TryGetValue("major_dir_x", out var mx) ? mx.GetDouble() : 0,
                input.TryGetValue("major_dir_y", out var my) ? my.GetDouble() : 0,
                input.TryGetValue("major_dir_z", out var mz) ? mz.GetDouble() : 0);
            if (candidate.GetLength() < 1e-9)
                throw new InvalidOperationException("Major direction vector must be non-zero.");
        }
        else if (host is Wall wall && wall.Location is LocationCurve lc)
        {
            var curve = lc.Curve;
            candidate = curve.GetEndPoint(1) - curve.GetEndPoint(0);
        }
        else
        {
            candidate = XYZ.BasisX;
        }

        var majorDir = ProjectIntoPlane(candidate, normal);

        var area = AreaReinforcement.Create(
            doc, host, majorDir, areaType.Id, barType.Id, ElementId.InvalidElementId);

        // Force regeneration NOW, inside the transaction, so an invalid layout surfaces
        // as a catchable exception (the dispatcher rolls the transaction back) instead
        // of corrupting the element and crashing Revit on the next view redraw.
        try
        {
            doc.Regenerate();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Revit rejected this area reinforcement (invalid geometry during regeneration): " +
                ex.Message + ". Try a different host or an explicit major direction that lies in the " +
                "host's face plane.");
        }

        return JsonSerializer.Serialize(new
        {
            id = area.Id.Value,
            type = "AreaReinforcement",
            area_type = areaType.Name,
            bar_type = barType.Name,
            host_id = host.Id.Value,
            major_direction = new { x = majorDir.X, y = majorDir.Y, z = majorDir.Z },
            note = "Layer spacings/bar types use the type defaults — adjust with set_parameter on this element if needed."
        });
    }

    // Reinforcement face normal: a wall's exterior facing, a flat floor's up axis.
    private static XYZ HostFaceNormal(Element host)
    {
        if (host is Wall w)
        {
            try { return w.Orientation.Normalize(); } catch { return XYZ.BasisX; }
        }
        return XYZ.BasisZ; // floors: top face
    }

    // Remove the component along the normal so the result lies in the face plane;
    // fall back to a valid in-plane axis if the projection collapses to ~zero.
    private static XYZ ProjectIntoPlane(XYZ dir, XYZ normal)
    {
        var proj = dir - normal.Multiply(dir.DotProduct(normal));
        if (proj.GetLength() > 1e-6)
            return proj.Normalize();

        // dir was (near) parallel to the normal — pick any axis in the plane.
        var seed = Math.Abs(normal.DotProduct(XYZ.BasisX)) < 0.9 ? XYZ.BasisX : XYZ.BasisY;
        var fallback = seed - normal.Multiply(seed.DotProduct(normal));
        return fallback.Normalize();
    }
}
