using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Builds a DirectShape from a triangle/quad mesh — the reliable primitive for freeform,
// sculptural geometry that walls/floors/roofs cannot express: domes, shells, canopies, the
// Sydney-Opera-House "sails" (sections of a common sphere), sweeping roofs, terrain solids.
// The model computes the vertices in code (a sphere patch is a couple of nested loops) and
// hands the mesh here; TessellatedShapeBuilder turns it into real Revit geometry with a Mesh
// fallback when the faces don't close into a watertight solid.
public class CreateDirectShape : IRevitTool
{
    public string Name => "create_direct_shape";

    public string Description =>
        "Creates a DirectShape element from a mesh (vertices + triangular/quad faces), for curved or " +
        "sculptural geometry no dedicated tool covers — domes, shells, vaults, canopies, freeform roofs, " +
        "the Sydney Opera House sails (sections of a sphere). vertices: array of [x,y,z] in feet. faces: " +
        "array of index lists (3 or 4 zero-based indices each). Set solid=true only for a closed, watertight " +
        "mesh; leave false for an open shell. Optional category (default 'Generic Models'), material_name, name. " +
        "Tip: generate the vertices parametrically (e.g. a sphere patch) rather than by hand.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["vertices"] = JsonSerializer.SerializeToElement(new
            {
                type = "array", minItems = 3,
                description = "Vertices as [x, y, z] arrays in feet.",
                items = new { type = "array", items = new { type = "number" } }
            }),
            ["faces"] = JsonSerializer.SerializeToElement(new
            {
                type = "array", minItems = 1,
                description = "Faces as arrays of 3 or 4 zero-based vertex indices, e.g. [[0,1,2],[0,2,3]].",
                items = new { type = "array", items = new { type = "integer" } }
            }),
            ["solid"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean",
                description = "True only if the mesh is a closed watertight solid; false (default) for an open shell."
            }),
            ["category"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Optional DirectShape category (default 'Generic Models'). e.g. 'Roofs', 'Mass', 'Walls'."
            }),
            ["material_name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Optional material name to apply to every face."
            }),
            ["name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional element name." })
        },
        Required = ["vertices", "faces"]
    };

    public bool RequiresTransaction => true;

    // A single DirectShape past this many faces builds/renders on the UI thread heavily enough to
    // freeze Revit. Refuse above it; warn as we approach it.
    private const int MaxFaces = 20000;
    private const int WarnFaces = 8000;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var verts = input["vertices"].EnumerateArray().Select(ParseVertex).ToList();
        if (verts.Count < 3)
            throw new InvalidOperationException($"Need at least 3 vertices (got {verts.Count}).");

        var faces = input["faces"].EnumerateArray()
            .Select(f => f.EnumerateArray().Select(i => i.GetInt32()).ToList())
            .ToList();
        if (faces.Count == 0)
            throw new InvalidOperationException("No faces provided.");

        // Guardrail: a DirectShape with tens of thousands of faces (e.g. cloning a high-poly
        // reference mesh) builds and renders on Revit's UI thread and can hang the whole app —
        // a 92k-face copy froze it in the field. Refuse and tell the model to build coarser /
        // split into pieces, rather than locking Revit up.
        if (faces.Count > MaxFaces)
            throw new InvalidOperationException(
                $"Mesh has {faces.Count} faces — too many for one DirectShape (limit {MaxFaces}); a mesh " +
                "this dense hangs Revit's UI thread. Build a coarser mesh (fewer grid steps), split it " +
                "into several smaller shapes, or model the form parametrically instead of cloning a " +
                "high-poly mesh.");

        // Validate indices up front so a bad mesh fails with a clear message, not deep in Revit.
        foreach (var face in faces)
        {
            if (face.Count is < 3 or > 4)
                throw new InvalidOperationException(
                    $"Each face needs 3 or 4 indices (found one with {face.Count}).");
            foreach (var i in face)
                if (i < 0 || i >= verts.Count)
                    throw new InvalidOperationException(
                        $"Face index {i} is out of range (0..{verts.Count - 1}).");
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

        var solid = input.TryGetValue("solid", out var s) && s.ValueKind == JsonValueKind.True;

        var builder = new TessellatedShapeBuilder();
        builder.OpenConnectedFaceSet(solid);
        foreach (var face in faces)
        {
            var loop = face.Select(i => verts[i]).ToList();
            builder.AddFace(new TessellatedFace(loop, materialId));
        }
        builder.CloseConnectedFaceSet();
        builder.Target = TessellatedShapeBuilderTarget.AnyGeometry;
        builder.Fallback = TessellatedShapeBuilderFallback.Mesh;

        try { builder.Build(); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not build geometry from the mesh: " + ex.Message +
                ". Check that faces wind consistently and vertices aren't degenerate; for an open " +
                "shell keep solid=false.");
        }

        var result = builder.GetBuildResult();
        var geometry = result.GetGeometricalObjects();
        if (geometry.Count == 0)
            throw new InvalidOperationException("The mesh produced no geometry (all faces degenerate?).");

        // Resolve the category; DirectShape only allows certain ones, so fall back to Generic
        // Models with a note rather than throwing.
        var catId = new ElementId(BuiltInCategory.OST_GenericModel);
        string? categoryNote = null;
        if (input.TryGetValue("category", out var c) && c.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(c.GetString()))
        {
            try
            {
                var requested = new ElementId(CategoryResolve.Parse(c.GetString()));
                if (DirectShape.IsValidCategoryId(requested, doc)) catId = requested;
                else categoryNote = $"Category '{c.GetString()}' isn't valid for a DirectShape; used Generic Models.";
            }
            catch (Exception ex) { categoryNote = ex.Message + " Used Generic Models."; }
        }

        var ds = DirectShape.CreateElement(doc, catId);
        ds.ApplicationId = "ClaudeRevit";
        ds.ApplicationDataId = "direct_shape";
        ds.SetShape(geometry);

        if (input.TryGetValue("name", out var n) && n.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(n.GetString()))
        {
            try { ds.Name = n.GetString(); } catch { /* name may be rejected — non-fatal */ }
        }

        return JsonSerializer.Serialize(new
        {
            id = ds.Id.Value,
            type = "DirectShape",
            category = doc.GetElement(catId) is { } ce ? ce.Name : catId.ToString(),
            vertex_count = verts.Count,
            face_count = faces.Count,
            solid,
            material = materialName,
            note = faces.Count >= WarnFaces
                ? (categoryNote + $" NOTE: {faces.Count} faces is heavy — Revit may lag; prefer coarser meshes.").Trim()
                : categoryNote
        });
    }

    private static XYZ ParseVertex(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Array)
        {
            var a = v.EnumerateArray().ToList();
            if (a.Count < 3) throw new InvalidOperationException("A vertex needs 3 numbers [x, y, z].");
            return new XYZ(a[0].GetDouble(), a[1].GetDouble(), a[2].GetDouble());
        }
        // Tolerate {x, y, z} objects too.
        return new XYZ(v.GetProperty("x").GetDouble(), v.GetProperty("y").GetDouble(), v.GetProperty("z").GetDouble());
    }
}
