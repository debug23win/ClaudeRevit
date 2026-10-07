using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class CreateSketchPlane : IRevitTool
{
    public string Name => "create_sketch_plane";

    public string Description =>
        "Creates a sketch plane element from a normal vector and an origin point. Returns the sketch-plane id, " +
        "useful as input to view-based sketching workflows.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["origin_x"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Origin X (feet)." }),
            ["origin_y"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Origin Y (feet)." }),
            ["origin_z"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Origin Z (feet)." }),
            ["normal_x"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Normal vector X." }),
            ["normal_y"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Normal vector Y." }),
            ["normal_z"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Normal vector Z." })
        },
        Required = ["origin_x", "origin_y", "origin_z", "normal_x", "normal_y", "normal_z"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = ToolContext.UiDocument(app)?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var origin = new XYZ(ToolInput.RequiredDouble(input, "origin_x"), ToolInput.RequiredDouble(input, "origin_y"), ToolInput.RequiredDouble(input, "origin_z"));
        var normal = new XYZ(ToolInput.RequiredDouble(input, "normal_x"), ToolInput.RequiredDouble(input, "normal_y"), ToolInput.RequiredDouble(input, "normal_z"));
        if (normal.IsZeroLength())
            throw new InvalidOperationException("Normal vector cannot be zero.");
        normal = normal.Normalize();

        var plane = Plane.CreateByNormalAndOrigin(normal, origin);
        var sp = SketchPlane.Create(doc, plane);

        return Services.Json.Serialize(new
        {
            id = sp.Id.Value,
            type = "SketchPlane",
            origin_ft = new { x = origin.X, y = origin.Y, z = origin.Z },
            normal = new { x = normal.X, y = normal.Y, z = normal.Z }
        });
    }
}
