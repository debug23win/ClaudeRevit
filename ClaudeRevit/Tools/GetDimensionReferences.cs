using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class GetDimensionReferences : IRevitTool
{
    public string Name => "get_dimension_references";
    public string Description => "Inspect exact stable references usable by create_dimension_from_references: geometry faces/edges, reference planes, grids, model curves and named references of nested/placed family instances. Geometry uses ComputeReferences. Stable strings belong to this document only; inspect normals and use a compatible view plane.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["element_ids"] = NativeToolUtil.Array("integer", "Elements to inspect (max 100)."),
        ["view_id"] = NativeToolUtil.Field("integer", "Optional geometry view."),
        ["limit"] = NativeToolUtil.Field("integer", "Max references, 1..2000; default 400.")
    }, "element_ids");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var limit = ToolInput.OptionalInt(input, "limit") ?? 400;
        if (limit is < 1 or > 2000) throw new ToolInputException("limit 1..2000.");
        var rows = new List<object>(); var seen = new HashSet<string>(); var warnings = new List<object>();
        var truncated = false;
        void Add(Element element, Reference? reference, string kind, XYZ? origin = null, XYZ? normal = null)
        {
            if (reference == null) return;
            var stable = reference.ConvertToStableRepresentation(doc);
            if (!seen.Add(stable)) return;
            if (rows.Count == limit) { truncated = true; return; }
            rows.Add(new { element_id = element.Id.Value, stable_reference = stable, kind,
                origin_mm = origin == null ? null : NativeToolUtil.Mm(origin), normal = normal == null ? null : NativeToolUtil.Vector(normal) });
        }
        void Walk(Element element, GeometryElement? geometry, int depth, Transform transform)
        {
            if (geometry == null || depth > 8) return;
            foreach (var obj in geometry)
            {
                ToolContext.ThrowIfCancelled();
                if (obj is Solid solid)
                {
                    foreach (Face face in solid.Faces)
                    {
                        if (face is PlanarFace planar) Add(element, face.Reference, "planar_face", transform.OfPoint(planar.Origin), transform.OfVector(planar.FaceNormal));
                        else Add(element, face.Reference, "curved_face");
                    }
                    foreach (Edge edge in solid.Edges) Add(element, edge.Reference, "edge", transform.OfPoint(edge.AsCurve().Evaluate(0.5, true)));
                }
                else if (obj is GeometryInstance instance) Walk(element, instance.GetSymbolGeometry(), depth + 1, transform.Multiply(instance.Transform));
                else if (obj is Curve curve) Add(element, curve.Reference, "curve", curve.IsBound ? transform.OfPoint(curve.Evaluate(0.5, true)) : null);
            }
        }
        using var options = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true };
        if (input.TryGetValue("view_id", out var v)) options.View = NativeToolUtil.Element(doc, v.GetInt64()) as View ?? throw new ToolInputException("view_id must be a View.");
        foreach (var id in NativeToolUtil.Ids(input["element_ids"], 100))
        {
            var e = NativeToolUtil.Element(doc, id.Value);
            try
            {
                if (e is ReferencePlane rp) Add(e, rp.GetReference(), "reference_plane:" + rp.Name, rp.BubbleEnd, rp.Normal);
                if (e is Grid grid) Add(e, new Reference(grid), "grid", grid.Curve.Evaluate(0.5, true));
                if (e is CurveElement ce) Add(e, ce.GeometryCurve.Reference, "model_curve", ce.GeometryCurve.Evaluate(0.5, true));
                if (e is FamilyInstance fi)
                    foreach (var type in Enum.GetValues<FamilyInstanceReferenceType>().Where(t => t != FamilyInstanceReferenceType.NotAReference))
                        foreach (var r in fi.GetReferences(type) ?? []) Add(e, r, "family_reference:" + type);
                Walk(e, e.get_Geometry(options), 0, Transform.Identity);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { warnings.Add(new { element_id = id.Value, error = ex.Message }); }
        }
        return Services.Json.Serialize(new { document_key = Services.DocumentSessions.Key(doc), references = rows, truncated, warnings,
            geometry_coordinates = "Document coordinates in mm. Symbol transforms are applied to coordinates only; references remain original API references. Choose the dimension line in the target view plane." });
    }
}
