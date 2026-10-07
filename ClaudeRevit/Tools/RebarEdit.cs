using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Rebar editing the API offers but our tools did not: lap splices (Revit 2025+), joining two bars
// back into one, breaking area/path reinforcement into individual rebar, length rounding. Ideas
// from LuDattilo/RevitCortex (RebarAdvancedTools, RebarSystemTools, RebarSettingsTools; MIT).
public sealed class SpliceRebar : IRevitTool
{
    public string Name => "splice_rebar";
    public string Description =>
        "Lap-splice a rebar (or a whole set) with Revit's splice tools: by maximum bar length (rules: max_bar_length_mm, " +
        "min_bar_length_mm, run_out start|end — e.g. 11 700 mm stock bars), or at a plane through point_mm with " +
        "normal [x,y,z]. splice_type picks a rebar splice type (created with lap_multiplier × d if missing); position " +
        "end1 | middle | end2 places the lap relative to the cut. Returns the resulting rebar ids. preview defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["rebar_id"] = NativeToolUtil.Field("integer", "Rebar to splice."),
        ["splice_type"] = NativeToolUtil.Field("string", "Rebar splice type name (default the first)."),
        ["lap_multiplier"] = NativeToolUtil.Field("number", "When creating splice_type: lap length as a multiple of the bar diameter."),
        ["position"] = NativeToolUtil.Field("string", "end1 | middle (default) | end2."),
        ["max_bar_length_mm"] = NativeToolUtil.Field("number", "Rules mode: longest bar allowed."),
        ["min_bar_length_mm"] = NativeToolUtil.Field("number", "Rules mode: shortest remaining bar (default 1000)."),
        ["run_out"] = NativeToolUtil.Field("string", "Rules mode: start | end — where the short remainder goes."),
        ["point_mm"] = NativeToolUtil.Any("Plane mode: a point [x,y,z] on the splice plane."),
        ["normal"] = NativeToolUtil.Any("Plane mode: plane normal [x,y,z] (usually the bar direction)."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "rebar_id");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var bar = NativeToolUtil.Element(doc, ToolInput.RequiredLong(input, "rebar_id")) as Rebar ?? throw new ToolInputException("rebar_id is not a Rebar.");
        var position = NativeToolUtil.Text(input, "position", "middle") switch
        {
            "end1" => RebarSplicePosition.End1, "middle" => RebarSplicePosition.Middle, "end2" => RebarSplicePosition.End2,
            _ => throw new ToolInputException("position must be end1, middle or end2.")
        };
        var spliceName = NativeToolUtil.Text(input, "splice_type");
        double? maxLen = input.TryGetValue("max_bar_length_mm", out var mx) && mx.ValueKind == JsonValueKind.Number ? mx.GetDouble() / Units.MmPerFoot : null;
        var byPlane = input.TryGetValue("point_mm", out var pt) && pt.ValueKind == JsonValueKind.Array;
        if (maxLen == null && !byPlane) throw new ToolInputException("Give max_bar_length_mm (rules) or point_mm + normal (plane).");
        var preview = NativeToolUtil.Preview(input);
        var (ids, warnings) = NativeToolUtil.Commit(doc, "Claude: стыки арматуры", preview, () =>
        {
            var types = RebarSpliceTypeUtils.GetAllRebarSpliceTypes(doc).Select(id => doc.GetElement(id)).Where(e => e != null).ToList();
            var type = spliceName.Length == 0 ? types.FirstOrDefault() : types.FirstOrDefault(t => t!.Name == spliceName);
            if (type == null)
            {
                if (spliceName.Length == 0) spliceName = "Нахлёст";
                type = RebarSpliceTypeUtils.CreateRebarSpliceType(doc, spliceName);
                if (input.TryGetValue("lap_multiplier", out var lm) && lm.ValueKind == JsonValueKind.Number) RebarSpliceTypeUtils.SetLapLengthMultiplier(doc, type.Id, lm.GetDouble());
            }
            var options = new RebarSpliceOptions(doc, type!.Id, position);
            IList<ElementId> result;
            if (maxLen is { } max)
            {
                var rules = RebarSpliceRules.Create(doc);
                var min = input.TryGetValue("min_bar_length_mm", out var mn) && mn.ValueKind == JsonValueKind.Number ? mn.GetDouble() / Units.MmPerFoot : 1000 / Units.MmPerFoot;
                rules.SetMaximumAndMinimumBarLength(max, min);
                if (NativeToolUtil.Text(input, "run_out") == "start") rules.RunOutPosition = RebarSpliceByRulesRunOutPosition.Start;
                else if (NativeToolUtil.Text(input, "run_out") == "end") rules.RunOutPosition = RebarSpliceByRulesRunOutPosition.End;
                var geometry = RebarSpliceUtils.GetSpliceGeometries(doc, bar.Id, options, rules);
                if (geometry.Error != RebarSpliceByRulesError.Success) throw new ToolInputException($"Revit cannot splice this bar by these rules: {geometry.Error}.");
                var g = geometry.GetSpliceGeometries();
                if (g.Count == 0) return new List<long> { bar.Id.Value };
                result = RebarSpliceUtils.SpliceRebar(doc, bar.Id, options, g);
            }
            else
            {
                var p = NativeToolUtil.Point(pt);
                var n = input.TryGetValue("normal", out var nv) ? NativeToolUtil.Point(nv, mm: false).Normalize() : throw new ToolInputException("Plane mode needs normal.");
                var helper = Math.Abs(n.Z) > 0.9 ? XYZ.BasisX : XYZ.BasisZ;
                var lineDir = n.CrossProduct(helper).Normalize();
                var line = Line.CreateBound(p - lineDir * 50, p + lineDir * 50);
                var planeNormal = n.CrossProduct(lineDir).Normalize();
                var check = RebarSpliceUtils.CanRebarBeSpliced(bar, options, line, planeNormal);
                if (check != RebarSpliceError.Success) throw new ToolInputException($"Revit cannot splice the bar at this plane: {check}.");
                result = RebarSpliceUtils.SpliceRebar(doc, bar.Id, options, line, planeNormal);
            }
            return result.Select(i => i.Value).ToList();
        });
        return Services.Json.Serialize(new { preview, source_rebar = bar.Id.Value, resulting_rebar_ids = ids, pieces = ids.Count, revit_warnings = warnings });
    }
}

public sealed class UnifyRebars : IRevitTool
{
    public string Name => "unify_rebars";
    public string Description => "Join two spliced rebars back into one bar (RebarSpliceUtils.UnifyRebarsIntoOne, Revit 2025+). preview defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["first_id"] = NativeToolUtil.Field("integer", "First rebar."),
        ["second_id"] = NativeToolUtil.Field("integer", "Second rebar (spliced to the first)."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "first_id", "second_id");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var a = ToolInput.RequiredLong(input, "first_id"); var b = ToolInput.RequiredLong(input, "second_id");
        if (NativeToolUtil.Element(doc, a) is not Rebar || NativeToolUtil.Element(doc, b) is not Rebar) throw new ToolInputException("Both ids must be rebars.");
        var preview = NativeToolUtil.Preview(input);
        var (id, warnings) = NativeToolUtil.Commit(doc, "Claude: объединение стержней", preview, () => RebarSpliceUtils.UnifyRebarsIntoOne(doc, new ElementId(a), new ElementId(b)).Value);
        return Services.Json.Serialize(new { preview, rebar_id = id, revit_warnings = warnings });
    }
}

public sealed class ConvertReinforcementSystem : IRevitTool
{
    public string Name => "convert_reinforcement_system";
    public string Description =>
        "Break area or path reinforcement into individual rebar elements (the system is removed, its bars stay) so " +
        "bars can be numbered, spliced, scheduled and edited one by one. preview defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["element_ids"] = NativeToolUtil.Array("integer", "Area and/or path reinforcement elements."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "element_ids");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var elements = NativeToolUtil.Ids(ToolInput.Required(input, "element_ids"), 500).Select(id => NativeToolUtil.Element(doc, id.Value)).ToList();
        if (elements.Any(e => e is not (AreaReinforcement or PathReinforcement))) throw new ToolInputException("Only area or path reinforcement can be converted.");
        var preview = NativeToolUtil.Preview(input);
        var (result, warnings) = NativeToolUtil.Commit(doc, "Claude: армирование в стержни", preview, () => elements.Select(e => new
        {
            system_id = e.Id.Value,
            kind = e is AreaReinforcement ? "area" : "path",
            rebar_ids = (e is AreaReinforcement ar ? AreaReinforcement.RemoveAreaReinforcementSystem(doc, ar) : PathReinforcement.RemovePathReinforcementSystem(doc, (PathReinforcement)e)).Select(i => i.Value).ToList()
        }).ToList());
        return Services.Json.Serialize(new { preview, converted = result, revit_warnings = warnings });
    }
}

public sealed class SetRebarRounding : IRevitTool
{
    public string Name => "set_rebar_rounding";
    public string Description =>
        "Set rebar length rounding on bar types or individual rebars: total and/or segment length rounded to a step " +
        "(e.g. 5 or 10 mm) nearest / up / down — what bar bending schedules print. Reads current settings when only " +
        "targets are given. preview defaults true.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["bar_types"] = NativeToolUtil.Array("string", "Rebar bar type names."),
        ["rebar_ids"] = NativeToolUtil.Array("integer", "Individual rebars."),
        ["total_length_mm"] = NativeToolUtil.Field("number", "Rounding step for the total bar length."),
        ["segment_length_mm"] = NativeToolUtil.Field("number", "Rounding step for segment lengths."),
        ["method"] = NativeToolUtil.Field("string", "nearest (default) | up | down."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    });
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var targets = new List<(string Label, Func<RebarRoundingManager> Manager)>();
        if (input.TryGetValue("bar_types", out var bt) && bt.ValueKind == JsonValueKind.Array)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();
            foreach (var n in bt.EnumerateArray().Select(x => x.GetString() ?? ""))
            {
                var t = all.FirstOrDefault(x => x.Name == n) ?? throw NameResolve.Missing(n, "Rebar bar type", all.Select(x => x.Name));
                var id = t.Id;
                targets.Add(("type " + n, () => ((RebarBarType)doc.GetElement(id)).GetReinforcementRoundingManager()));
            }
        }
        if (input.TryGetValue("rebar_ids", out var ri) && ri.ValueKind == JsonValueKind.Array)
            foreach (var id in NativeToolUtil.Ids(ri, 2000))
            {
                if (doc.GetElement(id) is not Rebar) throw new ToolInputException($"Element {id.Value} is not a Rebar.");
                targets.Add(("rebar " + id.Value, () => ((Rebar)doc.GetElement(id)).GetReinforcementRoundingManager()));
            }
        if (targets.Count == 0) throw new ToolInputException("Give bar_types and/or rebar_ids.");
        double? Step(string k) => input.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() / Units.MmPerFoot : null;
        var total = Step("total_length_mm"); var segment = Step("segment_length_mm");
        var method = NativeToolUtil.Text(input, "method", "nearest") switch
        {
            "nearest" => RoundingMethod.Nearest, "up" => RoundingMethod.Up, "down" => RoundingMethod.Down, _ => throw new ToolInputException("method: nearest, up or down.")
        };
        object Report(string label, RebarRoundingManager m) => new
        {
            target = label, source = m.ApplicableReinforcementRoundingSource.ToString(),
            total_mm = Math.Round(m.ApplicableTotalLengthRounding * Units.MmPerFoot, 2), total_method = m.ApplicableTotalLengthRoundingMethod.ToString(),
            segment_mm = Math.Round(m.ApplicableSegmentLengthRounding * Units.MmPerFoot, 2), segment_method = m.ApplicableSegmentLengthRoundingMethod.ToString()
        };
        if (total == null && segment == null) return Services.Json.Serialize(new { current = targets.Select(t => Report(t.Label, t.Manager())) });
        var preview = NativeToolUtil.Preview(input);
        var (result, warnings) = NativeToolUtil.Commit(doc, "Claude: округление арматуры", preview, () => targets.Select(t =>
        {
            var m = t.Manager();
            if (total is { } tl) { m.TotalLengthRounding = tl; m.TotalLengthRoundingMethod = method; }
            if (segment is { } sl) { m.SegmentLengthRounding = sl; m.SegmentLengthRoundingMethod = method; }
            return Report(t.Label, m);
        }).ToList());
        return Services.Json.Serialize(new { preview, targets = result, revit_warnings = warnings });
    }
}
