using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class CreateRebarGeometry : IRevitTool
{
    public string Name => "create_rebar_geometry";
    public string Description => "Create native multi-segment shape-driven Rebar or a native free-form set, with lines/arcs, stirrup style, hooks/terminations and distribution layouts. Coordinates mm; normal is the shape plane normal (distribution axis). Curves exclude hooks; Revit creates bends according to bar type. Free-form uses explicit curves for each bar and is NOT associated to host faces by a custom update server. Preview defaults true. Does not certify cover/clash compliance.";
    public bool RequiresTransaction => false;
    public bool RequiresNoTurnGroup => true;
    public bool MutatesWithoutTransaction => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["host_id"] = NativeToolUtil.Field("integer", "Valid structural rebar host."),
        ["bar_type_name"] = NativeToolUtil.Field("string", "Loaded RebarBarType name; default first."),
        ["mode"] = NativeToolUtil.Field("string", "shape_driven (default) or free_form."),
        ["style"] = NativeToolUtil.Field("string", "Standard (default) or StirrupTie."),
        ["curves_mm"] = NativeToolUtil.Any("Shape-driven ordered vertices or {start_mm,end_mm,mid_mm?} segments, open or closed. For a closed stirrup repeat the first vertex."),
        ["bars_mm"] = NativeToolUtil.Any("Free form: array of curve inputs, one per bar, max 2000."),
        ["normal"] = NativeToolUtil.Any("Shape plane normal [x,y,z], required for shape_driven."),
        ["start_hook_type_id"] = NativeToolUtil.Field("integer", "Optional native RebarHookType."),
        ["end_hook_type_id"] = NativeToolUtil.Field("integer", "Optional native RebarHookType."),
        ["start_hook_orientation"] = NativeToolUtil.Field("string", "Left (default) or Right."),
        ["end_hook_orientation"] = NativeToolUtil.Field("string", "Left (default) or Right."),
        ["start_end_treatment_id"] = NativeToolUtil.Field("integer", "Revit 2026+ native end treatment."),
        ["end_end_treatment_id"] = NativeToolUtil.Field("integer", "Revit 2026+ native end treatment."),
        ["layout"] = NativeToolUtil.Any("Shape-driven: {rule:single|number_with_spacing|fixed_number|maximum_spacing|minimum_clear_spacing,count,spacing_mm,array_length_mm,bars_on_normal_side,include_first,include_last}. Free-form curves define their explicit set; no extra layout."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "host_id");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var host = ReinforcementHelpers.GetValidRebarHost(doc, input);
        var barType = ReinforcementHelpers.ResolveBarType(doc, input);
        if (!Enum.TryParse<RebarStyle>(NativeToolUtil.Text(input, "style", "Standard"), true, out var style)) throw new ToolInputException("Unknown rebar style.");
        var mode = NativeToolUtil.Text(input, "mode", "shape_driven");
        var preview = NativeToolUtil.Preview(input);
        ElementId? createdId = null;
        var (result, warnings) = NativeToolUtil.Commit(doc, "Claude: native rebar geometry", preview, () =>
        {
            Rebar rebar;
            if (mode == "free_form")
            {
                if (input.ContainsKey("layout") || input.ContainsKey("start_hook_type_id") || input.ContainsKey("end_hook_type_id") || input.ContainsKey("start_end_treatment_id") || input.ContainsKey("end_end_treatment_id"))
                    throw new ToolInputException("Free-form uses explicit complete bar curves; omit shape layout/hooks/terminations.");
                var entries = ToolInput.RequiredArray(input, "bars_mm").EnumerateArray().ToArray();
                if (entries.Length is < 1 or > 2000) throw new ToolInputException("bars_mm must contain 1..2000 bars.");
                IList<CurveLoop> loops = entries.Select(entry =>
                {
                    ToolContext.ThrowIfCancelled();
                    var loop = new CurveLoop(); foreach (var curve in NativeCurveInput.Read(entry, false)) loop.Append(curve); return loop;
                }).ToList();
#if REVIT2025
                if (style != RebarStyle.Standard) throw new ToolInputException("Free-form StirrupTie requires Revit 2026+.");
                rebar = Rebar.CreateFreeForm(doc, barType, host, loops, out RebarFreeFormValidationResult validation);
                if (rebar == null) throw new InvalidOperationException("Free-form validation: " + validation);
#else
                using (var creation = Rebar.CreateFreeForm(doc, barType, host, loops, style))
                    rebar = creation.Rebar ?? throw new InvalidOperationException("Free-form validation: " + creation.Error);
#endif
            }
            else if (mode == "shape_driven")
            {
                var curves = NativeCurveInput.Read(ToolInput.Required(input, "curves_mm"), false);
                var normal = NativeToolUtil.Point(ToolInput.Required(input, "normal"), false).Normalize();
                using var plane = Plane.CreateByNormalAndOrigin(normal, curves[0].GetEndPoint(0));
                NativeCurveInput.InPlane(curves, plane);
                RebarHookType? Hook(string key) => input.TryGetValue(key, out var id) ? NativeToolUtil.Element(doc, id.GetInt64()) as RebarHookType ?? throw new ToolInputException(key + " must be RebarHookType.") : null;
                var start = Hook("start_hook_type_id"); var end = Hook("end_hook_type_id");
                var startText = NativeToolUtil.Text(input, "start_hook_orientation", "Left");
                var endText = NativeToolUtil.Text(input, "end_hook_orientation", "Left");
                if (startText is not ("Left" or "Right") || endText is not ("Left" or "Right")) throw new ToolInputException("Hook orientation must be Left or Right.");
#if REVIT2025
                var startOrientation = startText == "Left" ? RebarHookOrientation.Left : RebarHookOrientation.Right;
                var endOrientation = endText == "Left" ? RebarHookOrientation.Left : RebarHookOrientation.Right;
                if (input.ContainsKey("start_end_treatment_id") || input.ContainsKey("end_end_treatment_id")) throw new ToolInputException("End treatment inputs require Revit 2026+.");
                rebar = Rebar.CreateFromCurves(doc, style, barType, start, end, host, normal, curves, startOrientation, endOrientation, true, true);
#else
                using var terminations = new BarTerminationsData(doc)
                {
                    HookTypeIdAtStart = start?.Id ?? ElementId.InvalidElementId,
                    HookTypeIdAtEnd = end?.Id ?? ElementId.InvalidElementId,
                    TerminationOrientationAtStart = startText == "Left" ? RebarTerminationOrientation.Left : RebarTerminationOrientation.Right,
                    TerminationOrientationAtEnd = endText == "Left" ? RebarTerminationOrientation.Left : RebarTerminationOrientation.Right
                };
                if (input.TryGetValue("start_end_treatment_id", out var st)) terminations.EndTreatmentTypeIdAtStart = NativeToolUtil.Element(doc, st.GetInt64()).Id;
                if (input.TryGetValue("end_end_treatment_id", out var et)) terminations.EndTreatmentTypeIdAtEnd = NativeToolUtil.Element(doc, et.GetInt64()).Id;
                rebar = Rebar.CreateFromCurves(doc, style, barType, host, normal, curves, terminations, true, true);
#endif
                if (rebar == null) throw new InvalidOperationException("Revit could not create a native rebar shape.");
                if (input.TryGetValue("layout", out var layout)) ApplyLayout(rebar, layout);
            }
            else throw new ToolInputException("mode must be shape_driven or free_form.");
            doc.Regenerate();
            var actual = (Rebar)doc.GetElement(rebar.Id);
            createdId = actual.Id;
            return new { rebar_id = preview ? (long?)null : actual.Id.Value, host_id = actual.GetHostId().Value, mode,
                quantity = actual.Quantity, bar_positions = actual.NumberOfBarPositions, shape_id = actual.GetShapeId().Value,
                total_length_mm = actual.get_Parameter(BuiltInParameter.REBAR_ELEM_TOTAL_LENGTH)?.AsDouble() * Units.MmPerFoot };
        }, () => { if (createdId == null || doc.GetElement(createdId) is not Rebar) throw new InvalidOperationException("Rebar did not survive native transaction validation; rolled back."); });
        return Services.Json.Serialize(new { preview, result, warnings });
    }
    internal static void ApplyLayout(Rebar rebar, JsonElement layout)
    {
        if (!rebar.IsRebarShapeDriven()) throw new ToolInputException("Layout requires shape-driven rebar.");
        var fields = layout.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        var rule = NativeToolUtil.Text(fields, "rule", "single");
        var count = ToolInput.OptionalInt(fields, "count") ?? 1;
        var spacing = (ToolInput.OptionalDouble(fields, "spacing_mm") ?? 0) / Units.MmPerFoot;
        var length = (ToolInput.OptionalDouble(fields, "array_length_mm") ?? 0) / Units.MmPerFoot;
        var side = !fields.ContainsKey("bars_on_normal_side") || ToolInput.Flag(fields, "bars_on_normal_side");
        var first = !fields.ContainsKey("include_first") || ToolInput.Flag(fields, "include_first");
        var last = !fields.ContainsKey("include_last") || ToolInput.Flag(fields, "include_last");
        if (count is < 1 or > 1002 || !double.IsFinite(spacing) || !double.IsFinite(length)) throw new ToolInputException("Invalid count/spacing/array length.");
        using var accessor = rebar.GetShapeDrivenAccessor();
        switch (rule)
        {
            case "single": accessor.SetLayoutAsSingle(); break;
            case "number_with_spacing" when spacing > 0: accessor.SetLayoutAsNumberWithSpacing(count, spacing, side, first, last); break;
            case "fixed_number" when length > 0: accessor.SetLayoutAsFixedNumber(count, length, side, first, last); break;
            case "maximum_spacing" when spacing > 0 && length > 0: accessor.SetLayoutAsMaximumSpacing(spacing, length, side, first, last); break;
            case "minimum_clear_spacing" when spacing > 0 && length > 0: accessor.SetLayoutAsMinimumClearSpacing(spacing, length, side, first, last); break;
            default: throw new ToolInputException("Unknown layout or missing positive spacing_mm/array_length_mm.");
        }
    }
}
