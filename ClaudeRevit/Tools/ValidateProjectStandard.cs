using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class ValidateProjectStandard : IRevitTool
{
    public string Name => "validate_project_standard";
    public string Description => "Read-only partial Samolet EIR v5.0 audit: imported CAD, worksharing, dimension value overrides, KR Parts/Paint/Stairs/Ramp/steel connections, RD analytical elements, slab rebar zone fields and path partition/face comparison. Optional view scan finds direct hides/graphic overrides. Reports rule/page and caps; never certifies full EIR compliance or changes the model. Family nesting requires analyze_family_structure.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["profile"] = NativeToolUtil.Field("string", "Required Samolet."),
        ["discipline"] = NativeToolUtil.Field("string", "KR (default), AR or another section."),
        ["stage"] = NativeToolUtil.Field("string", "PD or RD; default RD."),
        ["element_limit"] = NativeToolUtil.Field("integer", "Default 20000; 1..100000. Applies independently to audit scans."),
        ["view_ids"] = NativeToolUtil.Array("integer", "Optional 1..20 views for direct hide/override scan; max 2000 visible elements per view."),
        ["parameter_guids"] = NativeToolUtil.Any("Optional object mapping exact EIR names to actual project FOP GUIDs. Otherwise unique exact-name resolution is a hint, not a verified shared identity.")
    }, "profile");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        if (!ToolInput.RequiredString(input, "profile").Equals("Samolet", StringComparison.OrdinalIgnoreCase)) throw new ToolInputException("Currently supported audit profile: Samolet. Other profiles: get_project_standards and get_standard_workflows.");
        var doc = NativeToolUtil.Doc(app);
        var kr = NativeToolUtil.Text(input, "discipline", "KR").Equals("KR", StringComparison.OrdinalIgnoreCase);
        var stage = NativeToolUtil.Text(input, "stage", "RD").ToUpperInvariant();
        if (stage is not ("RD" or "PD")) throw new ToolInputException("stage must be PD or RD.");
        var limit = Math.Clamp(ToolInput.OptionalInt(input, "element_limit") ?? 20000, 1, 100000);
        var findings = new List<object>(); var incomplete = new List<string>();
        void Add(string rule, int page, long? id, string detail, string severity = "violation", long? view = null)
        { if (findings.Count < 1000) findings.Add(new { rule, page, element_id = id, view_id = view, severity, detail }); else if (!incomplete.Contains("Finding cap 1000 reached.")) incomplete.Add("Finding cap 1000 reached."); }
        IEnumerable<Element> Scan(FilteredElementCollector c, string scope)
        { var es = c.Take(limit + 1).ToArray(); if (es.Length > limit) incomplete.Add(scope + " truncated at " + limit); return es.Take(limit); }
        if (!doc.IsFamilyDocument && !doc.IsWorkshared) Add("4.1.2", 18, null, "Project is not workshared; required worksets need review.");
        foreach (var e in Scan(new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)), "CAD"))
        {
            ToolContext.ThrowIfCancelled(); var cad = (ImportInstance)e;
            Add(cad.IsLinked ? "4.1.5" : "4.1.6", 18, e.Id.Value, cad.IsLinked ? "Linked CAD: confirm it is only a permitted schematic, node or detail." : "Imported CAD is prohibited.", cad.IsLinked ? "manual_review" : "violation");
        }
        foreach (var e in Scan(new FilteredElementCollector(doc).OfClass(typeof(Dimension)), "Dimensions"))
        {
            ToolContext.ThrowIfCancelled(); var d = (Dimension)e;
            if (d.NumberOfSegments == 0 ? !string.IsNullOrEmpty(d.ValueOverride) : d.Segments.Cast<DimensionSegment>().Any(s => !string.IsNullOrEmpty(s.ValueOverride)))
                Add("9.1.10", 47, e.Id.Value, "Dimension has a value override. Check its sheet use; do not replace measured geometry with text.");
        }
        Parameter? Field(Element e, string name)
        {
            if (input.TryGetValue("parameter_guids", out var map) && map.TryGetProperty(name, out var g)) return NativeToolUtil.Parameter(e, "", g.GetString() ?? "");
            var ps = e.GetParameters(name); return ps.Count == 1 ? ps[0] : null;
        }
        bool Empty(Parameter? p) => p == null || !p.HasValue || p.StorageType == StorageType.String && string.IsNullOrWhiteSpace(p.AsString());
        if (kr && !doc.IsFamilyDocument)
        {
            foreach (var e in Scan(new FilteredElementCollector(doc).WhereElementIsNotElementType(), "KR elements"))
            {
                ToolContext.ThrowIfCancelled();
                var category = e.Category?.Id.Value;
                if (e is Part) Add("6.3.8", 37, e.Id.Value, "Parts are prohibited in KR.");
                if (category == (long)BuiltInCategory.OST_Stairs || category == (long)BuiltInCategory.OST_Ramps) Add("6.3.12", 38, e.Id.Value, "Native stairs/ramp tool is prohibited in KR; use customer-approved modelling workflow.");
                if (e is StructuralConnectionHandler) Add("6.3.12", 38, e.Id.Value, "Steel connection tool use needs review against the KR prohibition on Steel tools.");
                if (e.GetMaterialIds(true).Count > 0) Add("6.3.9", 37, e.Id.Value, "Paint material assignments found in KR.");
                if (stage == "RD" && e is AnalyticalMember or AnalyticalPanel) Add("6.3.11", 37, e.Id.Value, "Analytical member/panel exists in RD.");
                var hostId = e switch { Rebar r => r.GetHostId(), RebarInSystem r => r.GetHostId(), _ => ElementId.InvalidElementId };
                if (doc.GetElement(hostId) is Floor)
                {
                    var partition = Field(e, "Раздел"); var zone = Field(e, "ОргЗонаРасположения");
                    if (Empty(partition) || Empty(zone)) Add("6.3.12", 38, e.Id.Value, "Slab reinforcement lacks an unambiguous filled Раздел or ОргЗонаРасположения. Resolve actual shared GUIDs from the project FOP.");
                }
                if (e is PathReinforcement)
                {
                    var part = Field(e, "Раздел"); var face = Field(e, "Грань");
                    Add("6.3.12", 38, e.Id.Value, $"Path partition='{part?.AsString() ?? part?.AsValueString()}', face='{face?.AsString() ?? face?.AsValueString()}'. Verify semantic correspondence to actual placement; values may use different encodings.", "manual_review");
                }
            }
        }
        if (input.TryGetValue("view_ids", out var views)) foreach (var id in NativeToolUtil.Ids(views, 20))
        {
            var view = NativeToolUtil.Element(doc, id.Value) as View ?? throw new ToolInputException("view_ids must identify Views.");
            if (!FilteredElementCollector.IsViewValidForElementIteration(doc, view.Id)) { incomplete.Add("View " + id.Value + " does not support element iteration."); continue; }
            // Collector on the view excludes permanently hidden objects, so scan a
            // bounded document set for hiding and a view set for graphic overrides.
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType().Take(2000))
            { ToolContext.ThrowIfCancelled(); if (e.IsHidden(view)) Add("9.1.12", 47, e.Id.Value, "Direct permanent element hiding found.", view: id.Value); }
            var visible = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().Take(2001).ToArray();
            incomplete.Add("Direct hide scan is limited to first 2000 document elements in view " + id.Value + "; graphic scan to first 2000 visible elements.");
            foreach (var e in visible.Take(2000))
            {
                ToolContext.ThrowIfCancelled(); using var o = view.GetElementOverrides(e.Id);
                if (o.ProjectionLineColor.IsValid || o.CutLineColor.IsValid || o.SurfaceForegroundPatternColor.IsValid || o.SurfaceBackgroundPatternColor.IsValid || o.CutForegroundPatternColor.IsValid || o.CutBackgroundPatternColor.IsValid ||
                    o.ProjectionLineWeight != -1 || o.CutLineWeight != -1 || o.Transparency > 0 || o.Halftone || o.SurfaceForegroundPatternId != ElementId.InvalidElementId || o.CutForegroundPatternId != ElementId.InvalidElementId)
                    Add("9.1.13", 47, e.Id.Value, "Direct element graphic overrides found; use filters/templates for EIR deliverables.", view: id.Value);
            }
        }
        return Services.Json.Serialize(new { profile = "Samolet-EIR-v5.0", stage, discipline = kr ? "KR" : NativeToolUtil.Text(input, "discipline"), findings, incomplete,
            compliance_certified = false, not_checked = new[] { "All nested families: analyze_family_structure separately", "Customer FOP/UPM/PIM/naming appendices and required attributes", "Rebar placement, laps, multiplier values and design adequacy", "Room bounding, gaps, join order and all physical separation", "Every document view and sheet", "IFC mapping/export and project delivery requirements" } });
    }
}
