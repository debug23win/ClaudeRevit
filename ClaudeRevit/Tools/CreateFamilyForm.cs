using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class CreateFamilyForm : IRevitTool
{
    public string Name => "create_family_form";
    public string Description => "Create native solid/void Extrusion, Blend, Sweep, SweptBlend or Revolution in ordinary RFA (not conceptual mass). Profiles/paths accept vertices [[x,y,z],...] or ordered {start_mm,end_mm,mid_mm?} line/arc segments. All coordinates mm. Bind built-in form parameters to existing family parameters via parameter_bindings. Extrusion rectangle_mm plus width/height parameter names creates locked reference planes and labeled dimensions. Default preview=true.";
    public bool RequiresTransaction => false;
    public bool RequiresNoTurnGroup => true;
    public bool MutatesWithoutTransaction => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["kind"] = NativeToolUtil.Field("string", "extrusion, blend, sweep, swept_blend, revolution."),
        ["solid"] = NativeToolUtil.Field("boolean", "Default true; false makes a void."),
        ["profile_mm"] = NativeToolUtil.Any("Closed profile. World coordinates for extrusion/blend/revolution; local XY at z=0 for sweep/swept_blend."),
        ["top_profile_mm"] = NativeToolUtil.Any("Second closed profile for blend/swept_blend. Blend profiles share the sketch plane."),
        ["path_mm"] = NativeToolUtil.Any("Open path for sweep; one line/arc for swept_blend, in world coordinates."),
        ["plane_origin_mm"] = NativeToolUtil.Any("Sketch plane origin [x,y,z]; default [0,0,0]."),
        ["plane_normal"] = NativeToolUtil.Any("Sketch plane unit normal; default Z."),
        ["plane_x_axis"] = NativeToolUtil.Any("Optional unit X basis perpendicular to normal."),
        ["depth_mm"] = NativeToolUtil.Field("number", "Extrusion/blend positive depth."),
        ["axis_mm"] = NativeToolUtil.Any("Revolution axis [start,end], in sketch plane."),
        ["start_angle_deg"] = NativeToolUtil.Field("number", "Default 0."),
        ["end_angle_deg"] = NativeToolUtil.Field("number", "Default 360."),
        ["parameter_bindings"] = NativeToolUtil.Any("Map built-in names to existing family names/GUIDs: e.g. {EXTRUSION_END_PARAM:'Depth',IS_VISIBLE_PARAM:'Show'}."),
        ["rectangle_mm"] = NativeToolUtil.Any("Extrusion only: {width:600,height:400}; lower left at sketch plane origin."),
        ["width_parameter"] = NativeToolUtil.Field("string", "Rectangle width driver, created if missing. Requires a 2D family view parallel to sketch plane."),
        ["height_parameter"] = NativeToolUtil.Field("string", "Rectangle height driver, created if missing."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true; false commits.")
    }, "kind");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app); var fm = FamilyEditorUtil.Manager(doc);
        var preview = NativeToolUtil.Preview(input); var kind = ToolInput.RequiredString(input, "kind");
        var solid = !input.ContainsKey("solid") || ToolInput.Flag(input, "solid");
        using var plane = NativeCurveInput.Plane(input);
        ElementId? createdId = null;
        var (result, warnings) = NativeToolUtil.Commit(doc, "Claude: family form", preview, () =>
        {
            var sketchPlane = SketchPlane.Create(doc, plane);
            var factory = doc.FamilyCreate;
            var rectangle = input.TryGetValue("rectangle_mm", out var rect);
            var width = rectangle ? rect.GetProperty("width").GetDouble() / Units.MmPerFoot : 0;
            var height = rectangle ? rect.GetProperty("height").GetDouble() / Units.MmPerFoot : 0;
            if (rectangle && (kind != "extrusion" || width <= 0 || height <= 0)) throw new ToolInputException("rectangle_mm requires positive width/height and kind=extrusion.");
            XYZ RectPoint(double x, double y) => plane.Origin + plane.XVec * x + plane.YVec * y;
            var profile = rectangle ? new List<Curve>
            {
                Line.CreateBound(RectPoint(0,0),RectPoint(width,0)), Line.CreateBound(RectPoint(width,0),RectPoint(width,height)),
                Line.CreateBound(RectPoint(width,height),RectPoint(0,height)), Line.CreateBound(RectPoint(0,height),RectPoint(0,0))
            } : NativeCurveInput.Read(input["profile_mm"], true);
            var depth = ToolInput.OptionalDouble(input, "depth_mm") / Units.MmPerFoot;
            GenericForm form;
            if (kind is "extrusion" or "blend" or "revolution") NativeCurveInput.InPlane(profile, plane);
            switch (kind)
            {
                case "extrusion":
                    if (depth is null or <= 0) throw new ToolInputException("Positive depth_mm required.");
                    form = factory.NewExtrusion(solid, NativeCurveInput.Loops(profile), sketchPlane, depth.Value); break;
                case "blend":
                    if (depth is null or <= 0) throw new ToolInputException("Positive depth_mm required.");
                    var top = NativeCurveInput.Read(input["top_profile_mm"], true); NativeCurveInput.InPlane(top, plane);
                    var blend = factory.NewBlend(solid, NativeCurveInput.Array(top), NativeCurveInput.Array(profile), sketchPlane);
                    blend.get_Parameter(BuiltInParameter.BLEND_END_PARAM).Set(depth.Value); form = blend; break;
                case "sweep":
                case "swept_blend":
                {
                    using var localPlane = Autodesk.Revit.DB.Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
                    NativeCurveInput.InPlane(profile, localPlane);
                    var path = NativeCurveInput.Read(input["path_mm"], false); NativeCurveInput.InPlane(path, plane);
                    using (var bottomProfile = app.Application.Create.NewCurveLoopsProfile(NativeCurveInput.Loops(profile)))
                    {
                        if (kind == "sweep") form = factory.NewSweep(solid, NativeCurveInput.Array(path), sketchPlane, bottomProfile, 0, ProfilePlaneLocation.Start);
                        else
                        {
                            if (path.Count != 1) throw new ToolInputException("swept_blend requires exactly one path line or arc.");
                            var topCurves = NativeCurveInput.Read(input["top_profile_mm"], true);
                            NativeCurveInput.InPlane(topCurves, localPlane);
                            using var topProfile = app.Application.Create.NewCurveLoopsProfile(NativeCurveInput.Loops(topCurves));
                            form = factory.NewSweptBlend(solid, path[0], sketchPlane, bottomProfile, topProfile);
                        }
                    }
                    break;
                }
                case "revolution":
                    var axis = input["axis_mm"].EnumerateArray().Select(p => NativeToolUtil.Point(p)).ToArray();
                    if (axis.Length != 2) throw new ToolInputException("axis_mm requires two points.");
                    var axisLine = Line.CreateBound(axis[0], axis[1]); NativeCurveInput.InPlane([axisLine], plane);
                    form = factory.NewRevolution(solid, NativeCurveInput.Loops(profile), sketchPlane, axisLine,
                        (ToolInput.OptionalDouble(input, "start_angle_deg") ?? 0) * Math.PI / 180, (ToolInput.OptionalDouble(input, "end_angle_deg") ?? 360) * Math.PI / 180); break;
                default: throw new ToolInputException("Unknown family form kind.");
            }
            if (input.TryGetValue("parameter_bindings", out var bindings))
                foreach (var binding in bindings.EnumerateObject())
                {
                    if (!Enum.TryParse<BuiltInParameter>(binding.Name, out var bip)) throw new ToolInputException("Unknown built-in parameter: " + binding.Name);
                    var p = form.get_Parameter(bip) ?? throw new ToolInputException("Form lacks parameter: " + binding.Name);
                    if (!fm.CanElementParameterBeAssociated(p)) throw new ToolInputException("Cannot associate: " + binding.Name);
                    fm.AssociateElementParameterToFamilyParameter(p, FamilyDocumentScope.Parameter(fm, binding.Value.GetString() ?? ""));
                }
            var planeIds = new List<long>(); var dimensionIds = new List<long>();
            var widthName = NativeToolUtil.Text(input, "width_parameter"); var heightName = NativeToolUtil.Text(input, "height_parameter");
            if ((widthName.Length > 0 || heightName.Length > 0) && !rectangle) throw new ToolInputException("Width/height drivers require rectangle_mm.");
            if (rectangle && (widthName.Length > 0 || heightName.Length > 0))
            {
                var view = doc.ActiveView;
                if (view is View3D || Math.Abs(view.ViewDirection.Normalize().DotProduct(plane.Normal)) < 0.99999)
                    throw new ToolInputException("Activate a family plan/elevation parallel to the sketch plane before creating rectangular drivers.");
                if (fm.CurrentType == null) fm.NewType("Type 1");
                var extrusion = (Extrusion)form;
                doc.Regenerate();
                var lines = extrusion.Sketch.GetAllElements().Select(doc.GetElement).OfType<ModelCurve>().ToList();
                void Drive(string name, bool xAxis, double size)
                {
                    if (name.Length == 0) return;
                    var parameter = FamilyEditorUtil.Find(fm, name);
                    if (parameter == null) { parameter = fm.AddParameter(name, GroupTypeId.Geometry, SpecTypeId.Length, false); fm.Set(parameter, size); }
                    if (!FamilyEditorUtil.IsLength(parameter)) throw new ToolInputException("Rectangle driver must be a length parameter: " + name);
                    var normal = xAxis ? plane.XVec : plane.YVec; var tangent = xAxis ? plane.YVec : plane.XVec;
                    var p0 = plane.Origin; var p1 = p0 + normal * size;
                    var a = factory.NewReferencePlane(p0 - tangent, p0 + tangent, plane.Normal, view);
                    var b = factory.NewReferencePlane(p1 - tangent, p1 + tangent, plane.Normal, view);
                    a.Name = name + " fixed " + form.Id.Value; b.Name = name + " moving " + form.Id.Value; a.Pinned = true;
                    foreach (var rp in new[] { a, b })
                    {
                        var origin = rp == a ? p0 : p1;
                        var line = lines.SingleOrDefault(c => c.GeometryCurve is Line &&
                            Math.Abs((c.GeometryCurve.GetEndPoint(0) - origin).DotProduct(normal)) < 1e-6 &&
                            Math.Abs((c.GeometryCurve.GetEndPoint(1) - origin).DotProduct(normal)) < 1e-6)
                            ?? throw new InvalidOperationException("Could not locate the rectangular sketch edge to constrain.");
                        factory.NewAlignment(view, rp.GetReference(), line.GeometryCurve.Reference);
                    }
                    var refs = new ReferenceArray(); refs.Append(a.GetReference()); refs.Append(b.GetReference());
                    var offset = tangent * (-Math.Max(size * 0.15, 100 / Units.MmPerFoot));
                    var dimension = factory.NewDimension(view, Line.CreateBound(p0 + offset, p1 + offset), refs);
                    dimension.FamilyLabel = parameter;
                    planeIds.Add(a.Id.Value); planeIds.Add(b.Id.Value); dimensionIds.Add(dimension.Id.Value);
                }
                Drive(widthName, true, width); Drive(heightName, false, height);
            }
            doc.Regenerate();
            if (doc.GetElement(form.Id) is not GenericForm) throw new InvalidOperationException("Form disappeared during regeneration.");
            createdId = form.Id;
            return new { form_id = preview ? (long?)null : form.Id.Value, form_class = form.GetType().Name, solid = form.IsSolid,
                reference_plane_ids = preview ? null : planeIds.ToArray(), dimension_ids = preview ? null : dimensionIds.ToArray() };
        }, () => { if (createdId == null || doc.GetElement(createdId) is not GenericForm) throw new InvalidOperationException("Native family form was removed during commit; rolled back."); });
        return Services.Json.Serialize(new { preview, result, warnings, next_step = "flex_family with min/max dimensions and visibility/type variants" });
    }
}
