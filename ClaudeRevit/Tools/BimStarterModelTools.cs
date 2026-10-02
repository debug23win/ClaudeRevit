using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Independent native API workflows. No BIMStarter DLL/source is incorporated.
public sealed class BimStarterModelTools : IRevitTool
{
    public string Name => "bimstarter_model_tools";
    public string Description => "Native automation of BIMStarter-style workflows, without plugin dialogs: copy/propagate parameters by GUID, join order, unjoin, solid/void cuts, steel coping, beam end joins, rebar layout/display/set explosion, area-system removal, schedule refresh, view templates and view-filter removal. Atomic committed preview defaults true. Explicit IDs and mappings only; get_bimstarter_tools lists all plugin commands and related tools.";
    public bool RequiresTransaction => false;
    public bool RequiresConfirmation => true;
    public bool RequiresNoTurnGroup => true;
    public bool MutatesWithoutTransaction => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["operation"] = NativeToolUtil.Field("string", "copy_parameters, propagate_host_parameters, join_order, unjoin, cut, remove_cut, cope, remove_cope, beam_end_joins, rebar_layout, rebar_display, explode_rebar_set, remove_area_system, refresh_schedules, apply_view_template, remove_view_filters."),
        ["element_ids"] = NativeToolUtil.Array("integer", "Target elements, rebar, area systems, beams, schedules or views (1..5000)."),
        ["source_element_id"] = NativeToolUtil.Field("integer", "Source for copy_parameters."),
        ["mappings"] = NativeToolUtil.Any("copy/propagate: [{source:'nameOrGUID',target:'nameOrGUID'}]. Data types must match; prefer GUIDs. Instance values only."),
        ["pairs"] = NativeToolUtil.Any("join/cut/cope: [{target_id,cutter_id}]. join_order ensures cutter_id cuts target_id."),
        ["cut_kind"] = NativeToolUtil.Field("string", "solid (default) or instance_void."),
        ["allow_join"] = NativeToolUtil.Field("boolean", "beam_end_joins: true allows, false disallows."),
        ["ends"] = NativeToolUtil.Array("integer", "Beam ends 0/1; default both."),
        ["layout"] = NativeToolUtil.Any("rebar_layout: same object as create_rebar_geometry.layout."),
        ["view_id"] = NativeToolUtil.Field("integer", "Rebar display view; default active."),
        ["unobscured"] = NativeToolUtil.Field("boolean", "Optional rebar display change."),
        ["presentation"] = NativeToolUtil.Field("string", "RebarPresentationMode name: e.g. ShowAll, ShowFirstLast, ShowMiddle."),
        ["view_template_id"] = NativeToolUtil.Field("integer", "Template for apply_view_template."),
        ["filter_ids"] = NativeToolUtil.Array("integer", "Filters to remove from specified views."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true; false applies.")
    }, "operation");
    private static Parameter Resolve(Element element, string key) => NativeToolUtil.Parameter(element, Guid.TryParse(key, out _) ? "" : key, Guid.TryParse(key, out _) ? key : "")
        ?? throw new ToolInputException($"Parameter '{key}' missing on {element.Id.Value}.");
    private static bool Copy(Parameter source, Parameter target)
    {
        if (target.IsReadOnly || source.StorageType != target.StorageType || source.Definition.GetDataType() != target.Definition.GetDataType())
            throw new ToolInputException("Parameter target is read-only or has a different spec/storage type.");
        if (!source.HasValue) throw new ToolInputException("Source parameter has no value.");
        return source.StorageType switch
        {
            StorageType.String => target.Set(source.AsString() ?? ""),
            StorageType.Integer => target.Set(source.AsInteger()),
            StorageType.Double => target.Set(source.AsDouble()),
            StorageType.ElementId => target.Set(source.AsElementId()),
            _ => throw new ToolInputException("Unsupported parameter storage type.")
        };
    }
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var op = ToolInput.RequiredString(input, "operation"); var preview = NativeToolUtil.Preview(input);
        var ids = input.TryGetValue("element_ids", out var ei) ? NativeToolUtil.Ids(ei, 5000) : new List<ElementId>();
        var rows = new List<object>();
        var (_, warnings) = NativeToolUtil.Commit(doc, "Claude: " + op, preview, () =>
        {
            if (op is "join_order" or "unjoin" or "cut" or "remove_cut" or "cope" or "remove_cope")
            {
                var pairs = input["pairs"].EnumerateArray().ToArray();
                if (pairs.Length is < 1 or > 1000) throw new ToolInputException("Supply 1..1000 pairs.");
                foreach (var pair in pairs)
                {
                    ToolContext.ThrowIfCancelled();
                    var target = NativeToolUtil.Element(doc, pair.GetProperty("target_id").GetInt64());
                    var cutter = NativeToolUtil.Element(doc, pair.GetProperty("cutter_id").GetInt64());
                    switch (op)
                    {
                        case "join_order":
                            if (!JoinGeometryUtils.AreElementsJoined(doc, cutter, target)) JoinGeometryUtils.JoinGeometry(doc, cutter, target);
                            if (!JoinGeometryUtils.IsCuttingElementInJoin(doc, cutter, target)) JoinGeometryUtils.SwitchJoinOrder(doc, cutter, target);
                            break;
                        case "unjoin": if (JoinGeometryUtils.AreElementsJoined(doc, target, cutter)) JoinGeometryUtils.UnjoinGeometry(doc, target, cutter); break;
                        case "cut":
                            if (NativeToolUtil.Text(input, "cut_kind", "solid") == "instance_void") InstanceVoidCutUtils.AddInstanceVoidCut(doc, target, cutter);
                            else SolidSolidCutUtils.AddCutBetweenSolids(doc, target, cutter);
                            break;
                        case "remove_cut":
                            if (NativeToolUtil.Text(input, "cut_kind", "solid") == "instance_void") InstanceVoidCutUtils.RemoveInstanceVoidCut(doc, target, cutter);
                            else SolidSolidCutUtils.RemoveCutBetweenSolids(doc, target, cutter);
                            break;
                        case "cope":
                        case "remove_cope":
                            if (target is not FamilyInstance tf || cutter is not FamilyInstance cf) throw new ToolInputException("Coping requires two native framing FamilyInstances.");
                            if (op == "cope") tf.AddCoping(cf); else tf.RemoveCoping(cf); break;
                    }
                    rows.Add(new { target_id = target.Id.Value, cutter_id = cutter.Id.Value });
                }
            }
            else
            {
                if (ids.Count == 0) throw new ToolInputException("element_ids is required for this operation.");
                foreach (var id in ids)
                {
                    ToolContext.ThrowIfCancelled();
                    var element = NativeToolUtil.Element(doc, id.Value);
                    switch (op)
                    {
                        case "copy_parameters":
                        case "propagate_host_parameters":
                            var source = op == "copy_parameters" ? NativeToolUtil.Element(doc, ToolInput.RequiredLong(input, "source_element_id")) : element switch
                            {
                                Rebar bar => doc.GetElement(bar.GetHostId()),
                                RebarInSystem bar => doc.GetElement(bar.GetHostId()),
                                FamilyInstance instance => instance.SuperComponent ?? instance.Host,
                                _ => null
                            } ?? throw new ToolInputException("Element has no supported native host/parent.");
                            var mappings = input["mappings"].EnumerateArray().ToArray();
                            if (mappings.Length is < 1 or > 200) throw new ToolInputException("Supply 1..200 explicit parameter mappings.");
                            foreach (var mapping in mappings)
                            {
                                var from = Resolve(source, mapping.GetProperty("source").GetString() ?? "");
                                var to = Resolve(element, mapping.GetProperty("target").GetString() ?? "");
                                var changed = Copy(from, to);
                                rows.Add(new { id = id.Value, source_id = source.Id.Value, source_parameter = from.Definition.Name, target_parameter = to.Definition.Name, changed });
                            }
                            break;
                        case "beam_end_joins":
                            if (element is not FamilyInstance beam || beam.StructuralType != StructuralType.Beam) throw new ToolInputException("Element must be a native beam.");
                            var ends = input.TryGetValue("ends", out var en) ? en.EnumerateArray().Select(e => e.GetInt32()).Distinct().ToArray() : new[] { 0, 1 };
                            if (ends.Length == 0 || ends.Any(e => e is not (0 or 1))) throw new ToolInputException("ends requires 0/1.");
                            foreach (var end in ends) { if (ToolInput.Flag(input, "allow_join")) StructuralFramingUtils.AllowJoinAtEnd(beam, end); else StructuralFramingUtils.DisallowJoinAtEnd(beam, end); }
                            rows.Add(new { id = id.Value, ends }); break;
                        case "rebar_layout":
                            CreateRebarGeometry.ApplyLayout(element as Rebar ?? throw new ToolInputException("Select native Rebar."), input["layout"]);
                            rows.Add(new { id = id.Value }); break;
                        case "rebar_display":
                            var rebar = element as Rebar ?? throw new ToolInputException("Select native Rebar.");
                            var view = input.TryGetValue("view_id", out var vi) ? NativeToolUtil.Element(doc, vi.GetInt64()) as View : doc.ActiveView;
                            if (view == null || view.IsTemplate) throw new ToolInputException("Select a real view.");
                            if (input.ContainsKey("unobscured")) rebar.SetUnobscuredInView(view, ToolInput.Flag(input, "unobscured"));
                            var presentation = NativeToolUtil.Text(input, "presentation");
                            if (presentation.Length > 0) rebar.SetPresentationMode(view, Enum.TryParse<RebarPresentationMode>(presentation, true, out var mode) ? mode : throw new ToolInputException("Unknown presentation mode."));
                            rows.Add(new { id = id.Value, view_id = view.Id.Value }); break;
                        case "explode_rebar_set":
                            var set = element as Rebar ?? throw new ToolInputException("Select native Rebar.");
                            if (!set.IsRebarShapeDriven()) throw new ToolInputException("Set explosion currently supports uniform shape-driven sets. For free-form/varying geometry inspect transformed centerlines first.");
                            if (set.NumberOfBarPositions > 1002) throw new ToolInputException("Set exceeds 1002 positions.");
                            using (var accessor = set.GetShapeDrivenAccessor())
                            {
                                var baseCurves = set.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeAllMultiplanarCurves, 0);
                                var created = new List<long>();
                                for (var position = 0; position < set.NumberOfBarPositions; position++)
                                {
                                    ToolContext.ThrowIfCancelled(); if (!set.DoesBarExistAtPosition(position)) continue;
                                    var curves = set.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeAllMultiplanarCurves, position);
                                    if (curves.Count != baseCurves.Count || curves.Where((c, index) => c.GetEndPoint(0).DistanceTo(baseCurves[index].GetEndPoint(0)) > 1e-6 || c.GetEndPoint(1).DistanceTo(baseCurves[index].GetEndPoint(1)) > 1e-6).Any())
                                        throw new ToolInputException("Varying shape set cannot be exploded by uniform copying; no changes applied.");
                                    var transform = accessor.GetBarPositionTransform(position);
                                    if (!transform.BasisX.IsAlmostEqualTo(XYZ.BasisX) || !transform.BasisY.IsAlmostEqualTo(XYZ.BasisY) || !transform.BasisZ.IsAlmostEqualTo(XYZ.BasisZ)) throw new ToolInputException("Non-translation bar transforms require a geometry-preserving workflow.");
                                    var clone = ElementTransformUtils.CopyElement(doc, set.Id, XYZ.Zero).Select(doc.GetElement).OfType<Rebar>().Single();
                                    using var cloneAccessor = clone.GetShapeDrivenAccessor(); cloneAccessor.SetLayoutAsSingle();
                                    ElementTransformUtils.MoveElement(doc, clone.Id, transform.Origin); created.Add(clone.Id.Value);
                                }
                                var removed = doc.Delete(set.Id).Select(i => i.Value).ToArray();
                                doc.Regenerate();
                                if (created.Any(i => doc.GetElement(new ElementId(i)) is not Rebar)) throw new InvalidOperationException("Deleting the original set also removed a copied bar; explosion was rolled back.");
                                rows.Add(new { original_id = id.Value, removed_ids = removed, single_bar_count = created.Count, created_ids = preview ? null : created.ToArray() });
                            }
                            break;
                        case "remove_area_system":
                            var area = element as AreaReinforcement ?? throw new ToolInputException("Select AreaReinforcement.");
                            var nativeIds = AreaReinforcement.RemoveAreaReinforcementSystem(doc, area).Select(i => i.Value).ToArray();
                            rows.Add(new { original_id = id.Value, rebar_count = nativeIds.Length, rebar_ids = preview ? null : nativeIds }); break;
                        case "refresh_schedules":
                            (element as ViewSchedule ?? throw new ToolInputException("Select ViewSchedule.")).RefreshData(); rows.Add(new { id = id.Value }); break;
                        case "apply_view_template":
                            var targetView = element as View ?? throw new ToolInputException("Select views.");
                            var template = NativeToolUtil.Element(doc, ToolInput.RequiredLong(input, "view_template_id")) as View;
                            if (template == null || !template.IsTemplate || !targetView.IsValidViewTemplate(template.Id)) throw new ToolInputException("Template is incompatible with a target view.");
                            targetView.ViewTemplateId = template.Id; rows.Add(new { id = id.Value, template_id = template.Id.Value }); break;
                        case "remove_view_filters":
                            var filtered = element as View ?? throw new ToolInputException("Select views.");
                            foreach (var filter in NativeToolUtil.Ids(input["filter_ids"], 200)) if (filtered.GetFilters().Contains(filter)) filtered.RemoveFilter(filter);
                            rows.Add(new { id = id.Value }); break;
                        default: throw new ToolInputException("Unknown native operation: " + op);
                    }
                }
            }
            doc.Regenerate(); return true;
        });
        return Services.Json.Serialize(new { operation = op, preview, results = rows, warnings });
    }
}
