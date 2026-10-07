using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class CreateDimensionFromReferences : IRevitTool
{
    public string Name => "create_dimension_from_references";
    public string Description => "Create a dimension using exact stable references returned by get_dimension_references, document_key and explicit line/arc in millimetres. Linear dimensions in projects/families; angular, radial and arc_length in ordinary Family Editor. Optional family label. Default preview commits/regenerates then rolls back; returns measured values and Revit warnings.";
    public bool RequiresTransaction => false;
    public bool RequiresNoTurnGroup => true;
    public bool MutatesWithoutTransaction => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["document_key"] = NativeToolUtil.Field("string", "Document key from reference inspection."),
        ["references"] = NativeToolUtil.Array("string", "Stable references: linear 2+, angular 2, radial 1, arc_length 3 (arc,start,end)."),
        ["kind"] = NativeToolUtil.Field("string", "linear (default), angular, radial, arc_length."),
        ["view_id"] = NativeToolUtil.Field("integer", "Default active view."),
        ["line_mm"] = NativeToolUtil.Any("Linear placement: [[startX,startY,startZ],[endX,endY,endZ]]."),
        ["arc_mm"] = NativeToolUtil.Any("Arc placement: [start,end,pointOnArc], each [x,y,z]."),
        ["origin_mm"] = NativeToolUtil.Any("Radial text origin [x,y,z]."),
        ["dimension_type_id"] = NativeToolUtil.Field("integer", "Optional compatible dimension style."),
        ["label_parameter"] = NativeToolUtil.Field("string", "Optional Family Editor parameter label."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true; false commits.")
    }, "document_key", "references");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        if (ToolInput.RequiredString(input, "document_key") != Services.DocumentSessions.Key(doc)) throw new ToolInputException("References belong to another document; inspect again.");
        var references = ToolInput.RequiredArray(input, "references").EnumerateArray().Select(r => Reference.ParseFromStableRepresentation(doc, r.GetString() ?? "")).ToList();
        if (references.Count is < 1 or > 100) throw new ToolInputException("Supply 1..100 references.");
        var kind = NativeToolUtil.Text(input, "kind", "linear");
        var view = input.TryGetValue("view_id", out var vi) ? NativeToolUtil.Element(doc, vi.GetInt64()) as View : doc.ActiveView;
        if (view == null || view.IsTemplate || view is View3D) throw new ToolInputException("Choose a compatible 2D view.");
        DimensionType? type = input.TryGetValue("dimension_type_id", out var dt) ? NativeToolUtil.Element(doc, dt.GetInt64()) as DimensionType ?? throw new ToolInputException("dimension_type_id must be a DimensionType.") : null;
        var preview = NativeToolUtil.Preview(input);
        ElementId? createdId = null;
        var (result, warnings) = NativeToolUtil.Commit(doc, "Claude: exact dimension", preview, () =>
        {
            Dimension dimension;
            if (kind == "linear")
            {
                if (references.Count < 2) throw new ToolInputException("Linear dimension requires at least two references.");
                var pts = ToolInput.RequiredArray(input, "line_mm").EnumerateArray().Select(p => NativeToolUtil.Point(p)).ToArray();
                if (pts.Length != 2) throw new ToolInputException("line_mm requires two points.");
                var line = Line.CreateBound(pts[0], pts[1]);
                var array = new ReferenceArray(); references.ForEach(array.Append);
                dimension = doc.IsFamilyDocument ? doc.FamilyCreate.NewDimension(view, line, array) : doc.Create.NewDimension(view, line, array);
                if (type != null) dimension.ChangeTypeId(type.Id);
            }
            else
            {
                FamilyEditorUtil.Manager(doc);
                var factory = doc.FamilyCreate;
                Arc ArcInput()
                {
                    var pts = ToolInput.RequiredArray(input, "arc_mm").EnumerateArray().Select(p => NativeToolUtil.Point(p)).ToArray();
                    if (pts.Length != 3) throw new ToolInputException("arc_mm requires [start,end,pointOnArc].");
                    return Arc.Create(pts[0], pts[1], pts[2]);
                }
                dimension = kind switch
                {
                    "angular" when references.Count == 2 => type == null ? factory.NewAngularDimension(view, ArcInput(), references[0], references[1]) : factory.NewAngularDimension(view, ArcInput(), references[0], references[1], type),
                    "radial" when references.Count == 1 => type == null ? factory.NewRadialDimension(view, references[0], NativeToolUtil.Point(ToolInput.Required(input, "origin_mm"))) : factory.NewRadialDimension(view, references[0], NativeToolUtil.Point(ToolInput.Required(input, "origin_mm")), type),
                    "arc_length" when references.Count == 3 => type == null ? factory.NewArcLengthDimension(view, ArcInput(), references[0], references[1], references[2]) : factory.NewArcLengthDimension(view, ArcInput(), references[0], references[1], references[2], type),
                    _ => throw new ToolInputException("Unknown kind or incorrect reference count.")
                };
            }
            var label = NativeToolUtil.Text(input, "label_parameter");
            if (label.Length > 0) dimension.FamilyLabel = FamilyEditorUtil.Require(FamilyEditorUtil.Manager(doc), label);
            var id = dimension.Id;
            createdId = id;
            doc.Regenerate();
            dimension = doc.GetElement(id) as Dimension ?? throw new InvalidOperationException("Revit removed the dimension during regeneration.");
            if (!dimension.AreReferencesAvailable) throw new InvalidOperationException("Dimension references are unavailable.");
            double? ConvertValue(double? value) => value == null ? null : kind == "angular" ? value * 180 / Math.PI : value * Units.MmPerFoot;
            return new { dimension_id = preview ? (long?)null : id.Value, value = ConvertValue(dimension.Value), unit = kind == "angular" ? "degrees" : "mm",
                segment_values = dimension.Segments.Cast<DimensionSegment>().Select(s => ConvertValue(s.Value)).ToArray(), label };
        }, () => { if (createdId == null || doc.GetElement(createdId) is not Dimension committed || !committed.AreReferencesAvailable) throw new InvalidOperationException("Dimension was removed or lost its references during commit; operation rolled back."); });
        return Services.Json.Serialize(new { preview, kind, result, warnings });
    }
}
