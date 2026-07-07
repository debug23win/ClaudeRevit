using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Copies an existing element's geometry into a new DirectShape, with an optional rotation / scale
// / translation BAKED INTO THE VERTICES. This is the safe way to reuse a reference mesh (e.g. an
// imported model) in the correct orientation: rotating the built mesh afterwards with
// ElementTransformUtils re-processes every face on Revit's UI thread and FREEZES the app on a
// large mesh — the field hang. Baking the transform into the vertices during the copy is cheap
// (the same one pass that reads them) and never hangs.
public class CloneElementGeometry : IRevitTool
{
    public string Name => "clone_element_geometry";

    public string Description =>
        "Copies an element's geometry (mesh/solids) into a NEW DirectShape, optionally rotated / scaled / " +
        "moved — the transform is baked into the vertices, so it stays fast and never freezes Revit (unlike " +
        "rotating a built mesh afterwards). Use to reuse a reference/imported mesh in the right orientation. " +
        "Rotations are in degrees about the source's base-centre (X/Y correct a tilt, Z is plan yaw), applied " +
        "before the optional translation. Optional scale, category (default 'Generic Models'), material_name, " +
        "name, and delete_source to replace the original.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["source_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Element id to copy geometry from." }),
            ["rotate_x_deg"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Rotation about X through the base centre, degrees (tilt)." }),
            ["rotate_y_deg"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Rotation about Y through the base centre, degrees (tilt)." }),
            ["rotate_z_deg"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Rotation about Z (vertical) through the base centre, degrees (plan yaw)." }),
            ["scale"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Uniform scale about the base centre (default 1)." }),
            ["translate_x"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Move X after rotating, feet." }),
            ["translate_y"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Move Y after rotating, feet." }),
            ["translate_z"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Move Z after rotating, feet." }),
            ["category"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional DirectShape category (default 'Generic Models')." }),
            ["material_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional material for every face." }),
            ["name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional element name." }),
            ["delete_source"] = JsonSerializer.SerializeToElement(new { type = "boolean", description = "Delete the source element after cloning (default false)." })
        },
        Required = ["source_id"]
    };

    public bool RequiresTransaction => true;

    // Building this many triangles is itself heavy enough to risk a hang; refuse above it.
    private const int MaxTriangles = 250_000;
    private const int WarnTriangles = 60_000;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var srcId = new ElementId(input["source_id"].GetInt64());
        var src = doc.GetElement(srcId)
            ?? throw new InvalidOperationException($"Element {srcId.Value} not found.");

        // Collect the source triangles in world coordinates.
        var tris = new List<XYZ[]>();
        var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
        var geo = src.get_Geometry(opt)
            ?? throw new InvalidOperationException($"Element {srcId.Value} has no readable geometry.");
        Collect(geo, tris);

        if (tris.Count == 0)
            throw new InvalidOperationException(
                $"Element {srcId.Value} produced no triangles — it may not be a mesh/solid element.");
        if (tris.Count > MaxTriangles)
            throw new InvalidOperationException(
                $"Source has {tris.Count} triangles — too many to clone safely (limit {MaxTriangles}); " +
                "Revit would struggle. Simplify the source first.");

        // Base-centre pivot: centre in plan, bottom in Z — the natural point to turn a form about.
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var t in tris)
            foreach (var p in t)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                minZ = Math.Min(minZ, p.Z);
            }
        var pivot = new XYZ((minX + maxX) / 2, (minY + maxY) / 2, minZ);

        var rx = Deg(input, "rotate_x_deg");
        var ry = Deg(input, "rotate_y_deg");
        var rz = Deg(input, "rotate_z_deg");
        var scale = input.TryGetValue("scale", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : 1.0;
        if (scale <= 0) scale = 1.0;
        var tr = new XYZ(Num(input, "translate_x"), Num(input, "translate_y"), Num(input, "translate_z"));

        XYZ Transform(XYZ p)
        {
            var v = (p - pivot) * scale;
            v = RotX(v, rx); v = RotY(v, ry); v = RotZ(v, rz);
            return pivot + v + tr;
        }

        var materialId = ElementId.InvalidElementId;
        string? materialName = null;
        if (input.TryGetValue("material_name", out var m) && m.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(m.GetString()))
        {
            var mat = NameResolve.ByName<Material>(doc, m.GetString(), "Material");
            materialId = mat.Id;
            materialName = mat.Name;
        }

        var builder = new TessellatedShapeBuilder();
        builder.OpenConnectedFaceSet(false);
        foreach (var t in tris)
            builder.AddFace(new TessellatedFace(
                new List<XYZ> { Transform(t[0]), Transform(t[1]), Transform(t[2]) }, materialId));
        builder.CloseConnectedFaceSet();
        builder.Target = TessellatedShapeBuilderTarget.Mesh;
        builder.Fallback = TessellatedShapeBuilderFallback.Salvage;
        builder.Build();

        var catId = new ElementId(BuiltInCategory.OST_GenericModel);
        if (input.TryGetValue("category", out var c) && c.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(c.GetString()))
        {
            try
            {
                var requested = new ElementId(CategoryResolve.Parse(c.GetString()));
                if (DirectShape.IsValidCategoryId(requested, doc)) catId = requested;
            }
            catch { /* keep Generic Models */ }
        }

        var ds = DirectShape.CreateElement(doc, catId);
        ds.ApplicationId = "ClaudeRevit";
        ds.ApplicationDataId = "clone";
        ds.SetShape(builder.GetBuildResult().GetGeometricalObjects());
        if (input.TryGetValue("name", out var n) && n.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(n.GetString()))
        {
            try { ds.Name = n.GetString(); } catch { /* non-fatal */ }
        }

        var deletedSource = false;
        if (input.TryGetValue("delete_source", out var del) && del.ValueKind == JsonValueKind.True)
        {
            try { doc.Delete(srcId); deletedSource = true; } catch { /* leave source if delete fails */ }
        }

        doc.Regenerate();
        var bb = ds.get_BoundingBox(null);
        object size = bb == null ? "n/a" : new { w = bb.Max.X - bb.Min.X, d = bb.Max.Y - bb.Min.Y, h = bb.Max.Z - bb.Min.Z };

        return JsonSerializer.Serialize(new
        {
            id = ds.Id.Value,
            type = "DirectShape",
            triangles = tris.Count,
            material = materialName,
            deleted_source = deletedSource,
            size_ft = size,
            note = tris.Count >= WarnTriangles
                ? $"{tris.Count} triangles is heavy — do NOT later rotate/move this with a transform tool; it would freeze Revit. Re-clone instead."
                : null
        });
    }

    private static void Collect(GeometryElement ge, List<XYZ[]> tris)
    {
        foreach (var go in ge)
        {
            switch (go)
            {
                case Mesh mesh:
                    AddMesh(mesh, tris);
                    break;
                case Solid solid when solid.Faces.Size > 0:
                    foreach (Face f in solid.Faces)
                        AddMesh(f.Triangulate(), tris);
                    break;
                case GeometryInstance gi:
                    Collect(gi.GetInstanceGeometry(), tris); // instance transform already applied
                    break;
            }
        }
    }

    private static void AddMesh(Mesh? mesh, List<XYZ[]> tris)
    {
        if (mesh == null) return;
        for (int i = 0; i < mesh.NumTriangles; i++)
        {
            var t = mesh.get_Triangle(i);
            tris.Add(new[] { t.get_Vertex(0), t.get_Vertex(1), t.get_Vertex(2) });
        }
    }

    private static double Deg(IReadOnlyDictionary<string, JsonElement> d, string k) =>
        (d.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0) * Math.PI / 180.0;

    private static double Num(IReadOnlyDictionary<string, JsonElement> d, string k) =>
        d.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0;

    private static XYZ RotX(XYZ v, double a) =>
        a == 0 ? v : new XYZ(v.X, v.Y * Math.Cos(a) - v.Z * Math.Sin(a), v.Y * Math.Sin(a) + v.Z * Math.Cos(a));
    private static XYZ RotY(XYZ v, double a) =>
        a == 0 ? v : new XYZ(v.X * Math.Cos(a) + v.Z * Math.Sin(a), v.Y, -v.X * Math.Sin(a) + v.Z * Math.Cos(a));
    private static XYZ RotZ(XYZ v, double a) =>
        a == 0 ? v : new XYZ(v.X * Math.Cos(a) - v.Y * Math.Sin(a), v.X * Math.Sin(a) + v.Y * Math.Cos(a), v.Z);
}
