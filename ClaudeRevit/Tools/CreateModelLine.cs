using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class CreateModelLine : IRevitTool
{
    public string Name => "create_model_line";

    public string Description =>
        "Creates a model line in 3D space between two points (in feet). Unlike detail lines, model lines " +
        "appear in all views and are part of the 3D model. The line is created on a horizontal sketch plane " +
        "at the start point's elevation.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["start_x"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Start X (feet)." }),
            ["start_y"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Start Y (feet)." }),
            ["start_z"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Start Z (feet)." }),
            ["end_x"] = JsonSerializer.SerializeToElement(new { type = "number", description = "End X (feet)." }),
            ["end_y"] = JsonSerializer.SerializeToElement(new { type = "number", description = "End Y (feet)." }),
            ["end_z"] = JsonSerializer.SerializeToElement(new { type = "number", description = "End Z (feet)." })
        },
        Required = ["start_x", "start_y", "start_z", "end_x", "end_y", "end_z"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var start = new XYZ(
            input["start_x"].GetDouble(),
            input["start_y"].GetDouble(),
            input["start_z"].GetDouble());
        var end = new XYZ(
            input["end_x"].GetDouble(),
            input["end_y"].GetDouble(),
            input["end_z"].GetDouble());

        if (start.IsAlmostEqualTo(end))
            throw new InvalidOperationException("Model line has zero length.");

        // Build a sketch plane that contains the line. Use the direction perpendicular to line as plane normal.
        var dir = (end - start).Normalize();
        XYZ normal;
        if (Math.Abs(dir.Z) > 0.999)
            normal = XYZ.BasisX;
        else
            normal = dir.CrossProduct(XYZ.BasisZ).Normalize();

        var plane = Plane.CreateByNormalAndOrigin(normal, start);
        var sketchPlane = SketchPlane.Create(doc, plane);

        var line = Line.CreateBound(start, end);
        var modelLine = doc.Create.NewModelCurve(line, sketchPlane);

        return JsonSerializer.Serialize(new
        {
            id = modelLine.Id.Value,
            type = "ModelLine",
            length_ft = line.Length
        });
    }
}
