#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ModPlus.Headless;

// Explicit, separately implemented operations for common dialog-backed tools.
// They are granular equivalents, not complete reproductions of every option in
// the original ModPlus windows. No original command is posted or executed.
public static class NativeOperations
{
    private static readonly Dictionary<string, string[]> Supported = new Dictionary<string, string[]>
    {
        ["mprPhases"] = new[] { "list_phases", "assign_phases" },
        ["mprNotes"] = new[] { "create_text_note" },
        ["mprScheduleLookup"] = new[] { "read_schedule_cells" },
        ["mprSheetRenumber"] = new[] { "renumber_sheets" },
        ["mprRebarSolidAndVisible"] = new[] { "set_rebar_unobscured" },
        ["mprCopyCropBox"] = new[] { "copy_crop_box" },
        ["mprCopyViewTemplateFilters"] = new[] { "copy_view_filters" },
        ["mprFlipped"] = new[] { "inspect_flipped" },
        ["mprViewRange"] = new[] { "set_view_range" },
        ["mprOverrideGraphicManager"] = new[] { "clear_element_overrides" },
        ["mprAlignViews"] = new[] { "align_viewport_centers" },
        ["mprFontReplacer"] = new[] { "set_text_type_font" }
        , ["mprToposolidFloorConverter"] = new[] { "convert_floors_toposolids" },
        ["mprParameters"] = new[] { "set_parameters" },
        ["mprNumbering"] = new[] { "number_elements" },
        ["mprFindAndReplace"] = new[] { "replace_parameter_text" },
        ["mprVisibility"] = new[] { "set_element_visibility" },
        ["mprColorizer"] = new[] { "set_element_color" },
        ["mprCleaner"] = new[] { "delete_elements" },
        ["mprJoin"] = new[] { "join_geometry", "unjoin_geometry", "switch_join_order" },
        ["mprViewCopy"] = new[] { "duplicate_views" },
        ["mprTypeManager"] = new[] { "change_element_type" },
        ["mprWorksets"] = new[] { "list_worksets", "assign_workset" },
        ["mprWarningViewer"] = new[] { "list_warnings" },
        ["mprCopyAlongCurve"] = new[] { "divide_curve", "copy_along_curve" },
        ["mprCopyParameterValues"] = new[] { "copy_parameter_values" },
        ["mprFloorByRoom"] = new[] { "create_floors_by_rooms" }
    };
    public static string[] Operations(string module) => Supported.TryGetValue(module, out string[] operations) ? operations : Array.Empty<string>();
    public static string Run(string module, string operation, JsonElement args, UIApplication app, bool preview = true)
    {
        if (!Operations(module).Contains(operation, StringComparer.Ordinal)) throw new HeadlessBindingException("native_operation", "This module has no native operation named " + operation + ". Use describe for original-core bindings.");
        NativeContracts.Validate(module, operation, args);
        if (app.Application.VersionNumber != "2027") throw new HeadlessBindingException("revit_version", "Revit 2027 is required.");
        Document doc = app.ActiveUIDocument?.Document ?? throw new HeadlessBindingException("no_document", "Open a document.");
        if (doc.IsModifiable) throw new HeadlessBindingException("transaction_context", "Do not wrap this adapter in a host transaction.");
        if (operation == "convert_floors_toposolids") return new ToposolidConversion().Run(app, args, preview);
        if (operation == "divide_curve") return JsonSerializer.Serialize(new { positions = CurveDistribution.Divide(doc, args, app.Application.ShortCurveTolerance).Select(t => new { originFt = new[] { t.Origin.X, t.Origin.Y, t.Origin.Z }, tangent = new[] { t.BasisX.X, t.BasisX.Y, t.BasisX.Z } }).ToArray(), modelModified = false });
        if (operation == "list_warnings") return JsonSerializer.Serialize(new { warnings = doc.GetWarnings().Select(w => new { description = w.GetDescriptionText(), severity = w.GetSeverity().ToString(), elementIds = w.GetFailingElements().Select(id => id.Value).ToArray() }).ToArray(), modelModified = false });
        if (operation == "list_worksets") return JsonSerializer.Serialize(new { workshared = doc.IsWorkshared, worksets = doc.IsWorkshared ? new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Select(w => new { id = w.Id.IntegerValue, name = w.Name, editable = w.IsEditable }).ToArray() : Array.Empty<object>(), modelModified = false });
        if (operation == "list_phases")
            return JsonSerializer.Serialize(new { phases = doc.Phases.Cast<Phase>().Select(p => new { id = p.Id.Value, name = p.Name }).ToArray(), modelModified = false });
        if (operation == "inspect_flipped")
            return JsonSerializer.Serialize(new { rows = Elements(doc, args).Select(e => e is FamilyInstance f
                ? (object)new { id = e.Id.Value, mirrored = f.Mirrored, handFlipped = f.HandFlipped, facingFlipped = f.FacingFlipped }
                : e is Wall w ? new { id = e.Id.Value, flipped = w.Flipped } : (object)new { id = e.Id.Value, supported = false }).ToArray(), modelModified = false });
        if (operation == "read_schedule_cells")
        {
            ViewSchedule schedule = Get<ViewSchedule>(doc, args.GetProperty("schedule_id").GetInt64());
            TableSectionData body = schedule.GetTableData().GetSectionData(SectionType.Body);
            int start = args.TryGetProperty("start_row", out JsonElement sr) ? sr.GetInt32() : body.FirstRowNumber;
            int count = args.TryGetProperty("row_count", out JsonElement rc) ? rc.GetInt32() : Math.Min(100, body.NumberOfRows);
            if (count < 1 || count > 1000 || start < body.FirstRowNumber || start > body.LastRowNumber) throw new ArgumentException("Invalid schedule row bounds.");
            return JsonSerializer.Serialize(new { scheduleId = schedule.Id.Value,
                rows = Enumerable.Range(start, Math.Min(count, body.LastRowNumber - start + 1)).Select(row => new
                {
                    row, cells = Enumerable.Range(body.FirstColumnNumber, body.NumberOfColumns).Select(col => schedule.GetCellText(SectionType.Body, row, col)).ToArray()
                }).ToArray(), modelModified = false });
        }
        using var group = new TransactionGroup(doc, "Claude: ModPlus / " + module);
        using var failures = new NoDialogFailures(app.Application, doc);
        if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("Cannot start transaction group.");
        try
        {
            object result;
            using (var transaction = new Transaction(doc, "Claude: ModPlus / " + operation))
            {
                if (transaction.Start() != TransactionStatus.Started) throw new InvalidOperationException("Cannot start transaction.");
                NoDialogFailures.Configure(transaction);
                result = Apply(operation, args, app, doc);
                ClaudeRevit.Tools.ToolContext.ThrowIfCancelled();
                if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("The transaction did not commit.");
            }
            ClaudeRevit.Tools.ToolContext.ThrowIfCancelled();
            TransactionStatus status = preview ? group.RollBack() : group.Assimilate();
            if (status != (preview ? TransactionStatus.RolledBack : TransactionStatus.Committed)) throw new InvalidOperationException("Unexpected group result.");
            return JsonSerializer.Serialize(new { module, operation, preview, committed = !preview, result, failures = failures.Messages,
                note = preview ? "Changes rolled back; newly created element IDs are temporary." : "Changes committed." });
        }
        catch { if (group.GetStatus() == TransactionStatus.Started) group.RollBack(); throw; }
    }
    private static object Apply(string operation, JsonElement args, UIApplication app, Document doc)
    {
        if (CommonOperations.Supports(operation)) return CommonOperations.Apply(operation, args, doc);
        if (operation == "copy_along_curve") return CurveDistribution.Copy(doc, args, app.Application.ShortCurveTolerance);
        if (operation == "create_floors_by_rooms") return RoomFloors.Create(doc, args);
        switch (operation)
        {
            case "assign_phases":
            {
                ElementId created = args.TryGetProperty("created_phase_id", out JsonElement c) ? Get<Phase>(doc, c.GetInt64()).Id : null;
                ElementId demolished = args.TryGetProperty("demolished_phase_id", out JsonElement d) ? (d.ValueKind == JsonValueKind.Null ? ElementId.InvalidElementId : Get<Phase>(doc, d.GetInt64()).Id) : null;
                if (created == null && demolished == null) throw new ArgumentException("Supply created_phase_id or demolished_phase_id.");
                foreach (Element element in Elements(doc, args)) { if (created != null) element.CreatedPhaseId = created; if (demolished != null) element.DemolishedPhaseId = demolished; }
                return new { updated = Ids(args) };
            }
            case "create_text_note":
            {
                View view = Get<View>(doc, args.GetProperty("view_id").GetInt64());
                double[] p = args.GetProperty("point_mm").EnumerateArray().Select(x => x.GetDouble()).ToArray();
                if (p.Length != 3 || p.Any(x => !double.IsFinite(x))) throw new ArgumentException("point_mm must have three finite coordinates.");
                TextNoteType type = Get<TextNoteType>(doc, args.GetProperty("text_type_id").GetInt64());
                string text = args.GetProperty("text").GetString();
                if (string.IsNullOrEmpty(text)) throw new ArgumentException("text must be non-empty.");
                TextNote note = TextNote.Create(doc, view.Id, new XYZ(p[0] / 304.8, p[1] / 304.8, p[2] / 304.8), text, new TextNoteOptions(type.Id));
                return new { elementId = note.Id.Value };
            }
            case "renumber_sheets":
            {
                var requested = args.GetProperty("sheets").EnumerateArray().Select(s => new { sheet = Get<ViewSheet>(doc, s.GetProperty("sheet_id").GetInt64()), number = s.GetProperty("number").GetString() }).ToArray();
                if (requested.Length < 1 || requested.Length > 10000 || requested.Any(s => string.IsNullOrWhiteSpace(s.number)) || requested.Select(s => s.number).Distinct(StringComparer.Ordinal).Count() != requested.Length || requested.Select(s => s.sheet.Id.Value).Distinct().Count() != requested.Length)
                    throw new ArgumentException("Supply unique sheet IDs and non-empty unique sheet numbers.");
                var updated = requested.Select(s => s.sheet.Id.Value).ToHashSet();
                var occupied = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !updated.Contains(s.Id.Value)).Select(s => s.SheetNumber).ToHashSet(StringComparer.Ordinal);
                if (requested.Any(s => occupied.Contains(s.number))) throw new ArgumentException("A requested number belongs to another sheet.");
                string prefix = "MCP-" + Guid.NewGuid().ToString("N") + "-";
                for (int i = 0; i < requested.Length; i++) requested[i].sheet.SheetNumber = prefix + i;
                foreach (var sheet in requested) sheet.sheet.SheetNumber = sheet.number;
                return new { sheets = requested.Select(s => new { id = s.sheet.Id.Value, number = s.number }).ToArray() };
            }
            case "set_rebar_unobscured":
            {
                View view = Get<View>(doc, args.GetProperty("view_id").GetInt64()); bool unobscured = args.GetProperty("unobscured").GetBoolean();
                foreach (Element element in Elements(doc, args))
                {
                    if (element is Rebar rebar) rebar.SetUnobscuredInView(view, unobscured);
                    else if (element is RebarInSystem rebarInSystem) rebarInSystem.SetUnobscuredInView(view, unobscured);
                    else throw new ArgumentException("Supply Rebar or RebarInSystem IDs; host expansion is not part of this granular operation.");
                }
                return new { updated = Ids(args), unobscured };
            }
            case "copy_crop_box":
            {
                View source = Get<View>(doc, args.GetProperty("source_view_id").GetInt64());
                foreach (Element element in Elements(doc, args))
                {
                    if (element is not View target || target.IsTemplate) throw new ArgumentException("Targets must be non-template views.");
                    target.CropBox = source.CropBox; target.CropBoxActive = source.CropBoxActive; target.CropBoxVisible = source.CropBoxVisible;
                }
                return new { updated = Ids(args), semantics = "Rectangular crop box properties; custom crop-shape regions are not copied by this operation." };
            }
            case "copy_view_filters":
            {
                View source = Get<View>(doc, args.GetProperty("source_view_id").GetInt64());
                ICollection<ElementId> filters = source.GetOrderedFilters();
                foreach (Element element in Elements(doc, args))
                {
                    if (element is not View target) throw new ArgumentException("Target IDs must be views.");
                    if (target.Id == source.Id) continue;
                    if (args.TryGetProperty("replace", out JsonElement r) && r.GetBoolean()) foreach (ElementId filter in target.GetFilters().ToList()) target.RemoveFilter(filter);
                    foreach (ElementId filter in filters)
                    {
                        if (!target.GetFilters().Contains(filter)) target.AddFilter(filter);
                        target.SetFilterOverrides(filter, source.GetFilterOverrides(filter));
                        target.SetFilterVisibility(filter, source.GetFilterVisibility(filter));
                        target.SetIsFilterEnabled(filter, source.GetIsFilterEnabled(filter));
                    }
                }
                return new { updated = Ids(args) };
            }
            case "set_view_range":
            {
                ViewPlan view = Get<ViewPlan>(doc, args.GetProperty("view_id").GetInt64());
                using PlanViewRange range = view.GetViewRange();
                foreach (JsonProperty plane in args.GetProperty("planes").EnumerateObject())
                {
                    PlanViewPlane key = Enum.Parse<PlanViewPlane>(plane.Name, false);
                    if (!Enum.IsDefined(key)) throw new ArgumentException("Unknown plan view plane.");
                    ElementId level = plane.Value.GetProperty("level_id").ValueKind == JsonValueKind.String
                        ? plane.Value.GetProperty("level_id").GetString() switch { "unlimited" => PlanViewRange.Unlimited, "current" => PlanViewRange.Current, _ => throw new ArgumentException("Unknown range level alias.") }
                        : Get<Level>(doc, plane.Value.GetProperty("level_id").GetInt64()).Id;
                    double offset = plane.Value.GetProperty("offset_mm").GetDouble();
                    if (!double.IsFinite(offset)) throw new ArgumentException("offset_mm must be finite.");
                    range.SetLevelId(key, level); range.SetOffset(key, offset / 304.8);
                }
                view.SetViewRange(range); return new { viewId = view.Id.Value };
            }
            case "clear_element_overrides":
            {
                View view = Get<View>(doc, args.GetProperty("view_id").GetInt64());
                foreach (Element element in Elements(doc, args)) view.SetElementOverrides(element.Id, new OverrideGraphicSettings());
                return new { updated = Ids(args) };
            }
            case "align_viewport_centers":
            {
                Viewport reference = Get<Viewport>(doc, args.GetProperty("reference_viewport_id").GetInt64());
                XYZ center = reference.GetBoxCenter(); string axis = args.GetProperty("axis").GetString();
                if (axis is not "x" and not "y" and not "xy") throw new ArgumentException("axis must be x, y or xy.");
                foreach (Element element in Elements(doc, args))
                {
                    if (element is not Viewport viewport) throw new ArgumentException("Supply viewport IDs.");
                    XYZ old = viewport.GetBoxCenter(); viewport.SetBoxCenter(new XYZ(axis is "x" or "xy" ? center.X : old.X, axis is "y" or "xy" ? center.Y : old.Y, old.Z));
                }
                return new { updated = Ids(args) };
            }
            case "set_text_type_font":
            {
                string font = args.GetProperty("font").GetString(); if (string.IsNullOrWhiteSpace(font)) throw new ArgumentException("font must be non-empty.");
                foreach (Element element in Elements(doc, args))
                {
                    if (element is not TextNoteType) throw new ArgumentException("Supply TextNoteType IDs.");
                    Parameter parameter = element.get_Parameter(BuiltInParameter.TEXT_FONT);
                    if (parameter == null || parameter.IsReadOnly || parameter.AsString() != font && !parameter.Set(font)) throw new ArgumentException("The font parameter is unavailable or read-only.");
                }
                return new { updated = Ids(args) };
            }
        }
        throw new ArgumentException("Unsupported native operation.");
    }
    private static long[] Ids(JsonElement args)
    {
        long[] ids = args.GetProperty("element_ids").EnumerateArray().Select(i => i.GetInt64()).Distinct().ToArray();
        if (ids.Length < 1 || ids.Length > 10000) throw new ArgumentException("element_ids must contain 1..10000 IDs.");
        return ids;
    }
    private static IEnumerable<Element> Elements(Document doc, JsonElement args) => Ids(args).Select(id => Get<Element>(doc, id));
    private static T Get<T>(Document doc, long id) where T : Element => doc.GetElement(new ElementId(id)) as T ?? throw new ArgumentException("Element " + id + " is missing or differs from " + typeof(T).Name);
}
