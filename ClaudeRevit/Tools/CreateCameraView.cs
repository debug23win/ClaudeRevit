using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class CreateCameraView : IRevitTool
{
    public string Name => "create_camera_view";

    public string Description =>
        "Creates a perspective 3D view from a camera position (eye) looking toward a target point, " +
        "both in feet world coordinates. Use this for renderings, interior shots, exterior shots, etc.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["eye_x"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Camera position X (feet)." }),
            ["eye_y"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Camera position Y (feet)." }),
            ["eye_z"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Camera position Z (feet)." }),
            ["target_x"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Target X (feet)." }),
            ["target_y"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Target Y (feet)." }),
            ["target_z"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Target Z (feet)." }),
            ["name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional view name." })
        },
        Required = ["eye_x", "eye_y", "eye_z", "target_x", "target_y", "target_z"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var vftId = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional)?.Id
            ?? throw new InvalidOperationException("No 3D ViewFamilyType in this document.");

        var view = View3D.CreatePerspective(doc, vftId);

        var eye = new XYZ(input["eye_x"].GetDouble(), input["eye_y"].GetDouble(), input["eye_z"].GetDouble());
        var target = new XYZ(input["target_x"].GetDouble(), input["target_y"].GetDouble(), input["target_z"].GetDouble());
        if (eye.IsAlmostEqualTo(target))
            throw new InvalidOperationException("Eye and target are the same point.");

        var forward = (target - eye).Normalize();
        XYZ up;
        if (Math.Abs(forward.Z) > 0.999)
            up = XYZ.BasisY;
        else
        {
            var right = forward.CrossProduct(XYZ.BasisZ).Normalize();
            up = right.CrossProduct(forward).Normalize();
        }

        view.SetOrientation(new ViewOrientation3D(eye, up, forward));

        if (input.TryGetValue("name", out var n) && n.ValueKind == JsonValueKind.String)
        {
            try { view.Name = n.GetString(); } catch { }
        }

        return JsonSerializer.Serialize(new
        {
            id = view.Id.Value,
            type = "View3D (perspective)",
            name = view.Name,
            eye_ft = new { x = eye.X, y = eye.Y, z = eye.Z },
            target_ft = new { x = target.X, y = target.Y, z = target.Z }
        });
    }
}
