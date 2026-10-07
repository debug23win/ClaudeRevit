using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// Ведомость деталей (ГОСТ 21.501-2018) as a LIVE rebar schedule on a sheet with native Revit
// bending details (RebarBendingDetail, Revit 2024+) set into its "Эскиз" column, one per position.
// The schedule and the sketches follow the model; the sketches are real Revit annotation with
// proper dimension types, not generated pictures. Positions and marks come from a parameter
// profile: native Revit (Rebar Number / Partition), ADSK or BIMStarter, detected from the model
// or chosen, with per-field overrides for an office template.
public sealed class CreateBarBendingSchedule : IRevitTool
{
    public string Name => "create_bar_bending_schedule";
    public string Description =>
        "Ведомость деталей per ГОСТ 21.501-2018 on a sheet: a live Revit rebar schedule (graphs «Поз.» and «Эскиз», one " +
        "row per position, optional Ø/length columns) plus native bending details (Revit's own dimensioned bending " +
        "sketches) placed in the «Эскиз» cells. Scope: construction (mark value), host_ids or rebar_ids. profile: auto " +
        "(detect ADSK / BIMStarter / native Revit parameters in the model), native, adsk, bimstarter or custom with " +
        "field_mapping {position, construction, diameter, length: parameter name or builtin:NAME}. Straight bars are " +
        "left out unless include_straight. ALWAYS confirm the profile and sheet with the user first (offices use " +
        "different templates); run with preview=true (default) and show the detected mapping. Re-run with replace=true " +
        "after positions change: the sketches are placed per row and do not move by themselves when rows are added.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["name"] = NativeToolUtil.Field("string", "Schedule name, e.g. 'Ведомость деталей Пм-1'."),
        ["sheet_number"] = NativeToolUtil.Field("string", "Sheet to place it on."),
        ["construction"] = NativeToolUtil.Field("string", "Construction mark value to filter by (e.g. Пм-1)."),
        ["host_ids"] = NativeToolUtil.Array("integer", "Or: rebar in these hosts (positions are collected from them; the schedule filters by construction when given)."),
        ["rebar_ids"] = NativeToolUtil.Array("integer", "Or: these rebars."),
        ["profile"] = NativeToolUtil.Field("string", "auto (default) | native | adsk | bimstarter | custom."),
        ["field_mapping"] = NativeToolUtil.Any("Overrides {position, construction, diameter, length}: parameter name or builtin:BUILT_IN_PARAMETER."),
        ["extra_columns"] = NativeToolUtil.Array("string", "Optional extra graphs: diameter, length."),
        ["include_straight"] = NativeToolUtil.Field("boolean", "Also sketch straight bars (default false)."),
        ["row_height_mm"] = NativeToolUtil.Field("number", "Data row height for the sketches (default 25)."),
        ["sketch_width_mm"] = NativeToolUtil.Field("number", "«Эскиз» graph width (default 170: А4 frame width with «Поз.» 15)."),
        ["origin_mm"] = NativeToolUtil.Any("Top-left corner on the sheet [x,y] mm (default inside the frame, top left)."),
        ["bending_detail_type"] = NativeToolUtil.Field("string", "Existing bending detail type name (default: a schematic type sized to the cell is created)."),
        ["replace"] = NativeToolUtil.Field("boolean", "Replace an existing schedule/sketch view of this name."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    }, "name", "sheet_number");

    private static readonly Guid SketchParameter = new("6f1c2a9e-3b7d-4c55-9a40-0c2b1e8d7a51");

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var name = ToolInput.RequiredString(input, "name");
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
        var sheetNo = ToolInput.RequiredString(input, "sheet_number");
        var sheet = sheets.FirstOrDefault(s => s.SheetNumber == sheetNo) ?? throw NameResolve.Missing(sheetNo, "Sheet number", sheets.Select(s => s.SheetNumber));

        // Rebar in scope.
        var all = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().ToList();
        if (all.Count == 0) throw new ToolInputException("The model has no rebar.");
        var profileName = NativeToolUtil.Text(input, "profile", "auto");
        bool Has(string p) => p.StartsWith("builtin:") || all.Take(200).Any(r => r.LookupParameter(p) != null || doc.GetElement(r.GetTypeId())?.LookupParameter(p) != null);
        var detected = profileName == "auto" ? BendingScheduleProfiles.Detect(Has) : profileName;
        Dictionary<string, string> mapping;
        try
        {
            var candidates = detected == "custom" ? BendingScheduleProfiles.Get("native") : BendingScheduleProfiles.Get(detected);
            mapping = BendingScheduleProfiles.Fields.ToDictionary(f => f, f => candidates[f].FirstOrDefault(Has) ?? candidates[f].Last());
        }
        catch (ArgumentException ex) { throw new ToolInputException(ex.Message); }
        if (input.TryGetValue("field_mapping", out var fm) && fm.ValueKind == JsonValueKind.Object)
            foreach (var p in fm.EnumerateObject())
            {
                if (!BendingScheduleProfiles.Fields.Contains(p.Name)) throw new ToolInputException($"field_mapping keys: {string.Join(", ", BendingScheduleProfiles.Fields)}.");
                var v = p.Value.GetString() ?? ""; if (!Has(v)) throw new ToolInputException($"No rebar in the model has the parameter '{v}' ({p.Name}).");
                mapping[p.Name] = v;
            }
        string Read(Rebar r, string field)
        {
            var spec = mapping[field];
            Parameter? prm = spec.StartsWith("builtin:") && Enum.TryParse<BuiltInParameter>(spec[8..], out var bip)
                ? r.get_Parameter(bip) ?? doc.GetElement(r.GetTypeId())?.get_Parameter(bip)
                : r.LookupParameter(spec) ?? doc.GetElement(r.GetTypeId())?.LookupParameter(spec);
            return prm == null ? "" : prm.StorageType == StorageType.String ? prm.AsString() ?? "" : prm.AsValueString() ?? "";
        }
        var construction = NativeToolUtil.Text(input, "construction");
        // The live schedule can only filter by a parameter value, so a host or id scope needs the
        // construction mark too — otherwise the sheet would list every position in the project.
        if (construction.Length == 0 && (input.ContainsKey("host_ids") || input.ContainsKey("rebar_ids")))
            throw new ToolInputException($"Give construction (the mark in {mapping["construction"]}) as well: the live schedule filters by it. Mark the bars first if they have none.");
        var scope = all.AsEnumerable();
        if (input.TryGetValue("rebar_ids", out var ri) && ri.ValueKind == JsonValueKind.Array) { var s = NativeToolUtil.Ids(ri, 20000).ToHashSet(); scope = scope.Where(r => s.Contains(r.Id)); }
        if (input.TryGetValue("host_ids", out var hi) && hi.ValueKind == JsonValueKind.Array) { var s = NativeToolUtil.Ids(hi, 2000).ToHashSet(); scope = scope.Where(r => s.Contains(r.GetHostId())); }
        if (construction.Length > 0) scope = scope.Where(r => Read(r, "construction") == construction);
        var bars = scope.ToList();
        if (bars.Count == 0) throw new ToolInputException($"No rebar in scope (construction '{construction}' read from {mapping["construction"]}).");
        var includeStraight = ToolInput.Flag(input, "include_straight");
        bool Straight(Rebar r) { var c = r.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0); return c.Count == 1 && c[0] is Line; }
        var positions = bars.GroupBy(r => Read(r, "position")).Select(g => (Pos: g.Key, Bar: g.First(), Count: g.Count(), Shapes: g.Select(b => b.GetShapeId()).Distinct().Count()))
            .Where(p => includeStraight || !Straight(p.Bar)).OrderBy(p => RoomNumbering.NaturalKey(p.Pos), StringComparer.Ordinal).ToList();
        var unnumbered = positions.Where(p => string.IsNullOrWhiteSpace(p.Pos)).Sum(p => p.Count);
        if (positions.Count == 0) throw new ToolInputException("No bent bars in scope (straight bars are left out; pass include_straight=true to include them).");
        var straightShapes = includeStraight ? new List<ElementId>() : bars.Where(Straight).Select(b => b.GetShapeId()).Distinct().ToList();

        double Mm(string k, double d) => (input.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d) / Units.MmPerFoot;
        var rowH = Mm("row_height_mm", 25); var sketchW = Mm("sketch_width_mm", 170); var posW = 15 / Units.MmPerFoot;
        var extras = input.TryGetValue("extra_columns", out var ec) && ec.ValueKind == JsonValueKind.Array ? ec.EnumerateArray().Select(e => e.GetString() ?? "").ToList() : [];
        if (extras.Any(e => e is not ("diameter" or "length"))) throw new ToolInputException("extra_columns: diameter, length.");
        var replace = ToolInput.Flag(input, "replace");
        var detailTypeName = NativeToolUtil.Text(input, "bending_detail_type");
        var preview = NativeToolUtil.Preview(input);

        var (result, warnings) = NativeToolUtil.Commit(doc, "Claude: ведомость деталей", preview, () =>
        {
            foreach (var old in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.Name == name || v.Name == name + " — эскизы").ToList())
            {
                if (!replace) throw new ToolInputException($"'{old.Name}' already exists; pass replace=true or another name.");
                doc.Delete(old.Id);
            }
            EnsureSketchParameter(doc);
            var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Rebar));
            schedule.Name = name;
            var def = schedule.Definition;
            var fields = def.GetSchedulableFields();
            SchedulableField Field(string spec)
            {
                if (spec.StartsWith("builtin:") && Enum.TryParse<BuiltInParameter>(spec[8..], out var bip))
                    return fields.FirstOrDefault(f => f.ParameterId == new ElementId(bip)) ?? throw new InvalidOperationException($"{spec} is not schedulable for rebar.");
                return fields.FirstOrDefault(f => f.GetName(doc) == spec) ?? throw NameResolve.Missing(spec, "Rebar schedule field", fields.Select(f => f.GetName(doc)));
            }
            ScheduleField? constructionField = null;
            if (construction.Length > 0)
            {
                constructionField = def.AddField(Field(mapping["construction"]));
                constructionField.IsHidden = true;
                def.AddFilter(new ScheduleFilter(constructionField.FieldId, ScheduleFilterType.Equal, construction));
            }
            var pos = def.AddField(Field(mapping["position"])); pos.ColumnHeading = "Поз.";
            var sketch = def.AddField(fields.First(f => f.ParameterId == SharedParameterElement.Lookup(doc, SketchParameter).Id)); sketch.ColumnHeading = "Эскиз";
            foreach (var e in extras)
            {
                var f = def.AddField(Field(mapping[e])); f.ColumnHeading = e == "diameter" ? "Ø, мм" : "Длина, мм";
            }
            if (straightShapes.Count > 0)
            {
                var shapeField = def.AddField(fields.First(f => f.ParameterId == new ElementId(BuiltInParameter.REBAR_SHAPE)));
                shapeField.IsHidden = true;
                foreach (var s in straightShapes.Take(6)) def.AddFilter(new ScheduleFilter(shapeField.FieldId, ScheduleFilterType.NotEqual, s));
            }
            def.AddSortGroupField(new ScheduleSortGroupField(pos.FieldId, ScheduleSortOrder.Ascending));
            def.IsItemized = false;
            def.ShowGrandTotal = false;
            var body = schedule.GetTableData().GetSectionData(SectionType.Body);
            SetWidthByHeading(schedule, "Поз.", posW); SetWidthByHeading(schedule, "Эскиз", sketchW);
            doc.Regenerate();

            // Data rows tall enough for a sketch; Revit may refuse body row heights on some
            // versions — then the measured heights are used and reported.
            var rowsSet = 0;
            for (int r = body.FirstRowNumber; r < body.FirstRowNumber + body.NumberOfRows; r++)
            {
                if (!positions.Any(p => p.Pos == Cell(schedule, r, "Поз."))) continue;
                try { body.SetRowHeight(r, rowH); rowsSet++; } catch (Autodesk.Revit.Exceptions.ArgumentException) { } catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
            }
            doc.Regenerate();

            // Place on the sheet: top-left inside the ГОСТ frame unless given.
            var tb = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstOrDefault();
            var tbBox = tb?.get_BoundingBox(sheet);
            XYZ origin = input.TryGetValue("origin_mm", out var om) && om.ValueKind == JsonValueKind.Array
                ? new XYZ(om[0].GetDouble() / Units.MmPerFoot, om[1].GetDouble() / Units.MmPerFoot, 0)
                : tbBox != null ? new XYZ(tbBox.Min.X + 25 / Units.MmPerFoot, tbBox.Max.Y - 10 / Units.MmPerFoot, 0) : new XYZ(0.1, 0.9, 0);
            var instance = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, origin);
            doc.Regenerate();
            var ib = instance.get_BoundingBox(sheet);
            ElementTransformUtils.MoveElement(doc, instance.Id, new XYZ(origin.X - ib.Min.X, origin.Y - ib.Max.Y, 0));
            doc.Regenerate();
            ib = instance.get_BoundingBox(sheet);

            // Row centres on the sheet: title/header section, then body rows from the top.
            var header = schedule.GetTableData().GetSectionData(SectionType.Header);
            var headerHeights = header == null || header.NumberOfRows == 0 ? new List<double>() : Enumerable.Range(header.FirstRowNumber, header.NumberOfRows).Select(header.GetRowHeight).ToList();
            var bodyRows = Enumerable.Range(body.FirstRowNumber, body.NumberOfRows).ToList();
            var bodyHeights = bodyRows.Select(body.GetRowHeight).ToList();
            var firstData = bodyRows.FindIndex(r => positions.Any(p => p.Pos == Cell(schedule, r, "Поз.")));
            var centres = BendingScheduleProfiles.RowCentres(ib.Max.Y, headerHeights, bodyHeights, Math.Max(0, firstData));
            var rowPos = bodyRows.Skip(Math.Max(0, firstData)).Select(r => Cell(schedule, r, "Поз.")).ToList();
            var sketchX = ib.Min.X + posW + sketchW / 2;
            var minRow = bodyHeights.Skip(Math.Max(0, firstData)).DefaultIfEmpty(rowH).Min();

            // Bending detail type sized to the cell.
            RebarBendingDetailType detailType;
            if (detailTypeName.Length > 0)
            {
                var types = new FilteredElementCollector(doc).OfClass(typeof(RebarBendingDetailType)).Cast<RebarBendingDetailType>().ToList();
                detailType = types.FirstOrDefault(t => t.Name == detailTypeName) ?? throw NameResolve.Missing(detailTypeName, "Bending detail type", types.Select(t => t.Name));
            }
            else
            {
                detailType = RebarBendingDetailType.CreateSchematic(doc);
                try { detailType.Name = $"CR ведомость деталей {Math.Round(sketchW * Units.MmPerFoot)}×{Math.Round(minRow * Units.MmPerFoot)}"; } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                detailType.SchematicWidth = sketchW - 20 / Units.MmPerFoot;
                detailType.SchematicHeight = Math.Max(minRow - 8 / Units.MmPerFoot, 6 / Units.MmPerFoot);
                detailType.SegmentLengthDimensionsEnabled = true;
            }

            // Where the sketches live: on the sheet itself if Revit allows, else on a 1:1 drafting
            // view whose viewport maps view coordinates onto the sheet exactly.
            string host = "sheet"; View target = sheet; Transform toSheet = Transform.Identity;
            Element? Place(Rebar bar, XYZ at)
            {
                foreach (var key in new[] { -1, 0 })
                    try { return RebarBendingDetail.Create(doc, target.Id, bar.Id, key, detailType, at, 0); }
                    catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
                return null;
            }
            var placed = new List<object>(); var failed = new List<string>();
            var firstPos = positions[0];
            var firstRow = rowPos.IndexOf(firstPos.Pos);
            Element? probe = firstRow >= 0 ? Place(firstPos.Bar, new XYZ(sketchX, centres[firstRow], 0)) : null;
            if (probe == null)
            {
                host = "drafting view";
                var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.Drafting);
                var drafting = ViewDrafting.Create(doc, vft.Id); drafting.Name = name + " — эскизы"; drafting.Scale = 1;
                // Anchors fix the view extents to the schedule's footprint, so the viewport transform is stable.
                var w = ib.Max.X - ib.Min.X; var h = ib.Max.Y - ib.Min.Y;
                doc.Create.NewDetailCurve(drafting, Line.CreateBound(new XYZ(0, 0, 0), new XYZ(0.002, 0, 0)));
                doc.Create.NewDetailCurve(drafting, Line.CreateBound(new XYZ(w, -h, 0), new XYZ(w - 0.002, -h, 0)));
                var vp = Viewport.Create(doc, sheet.Id, drafting.Id, new XYZ((ib.Min.X + ib.Max.X) / 2, (ib.Min.Y + ib.Max.Y) / 2, 0));
                doc.Regenerate();
                toSheet = vp.GetProjectionToSheetTransform();
                var shift = new XYZ(ib.Min.X, ib.Max.Y, 0) - toSheet.OfPoint(XYZ.Zero);
                ElementTransformUtils.MoveElement(doc, vp.Id, shift);
                doc.Regenerate();
                toSheet = vp.GetProjectionToSheetTransform();
                target = drafting;
                if (firstRow >= 0) probe = Place(firstPos.Bar, toSheet.Inverse.OfPoint(new XYZ(sketchX, centres[firstRow], 0)));
                if (probe == null) throw new InvalidOperationException("Revit refused to place a bending detail on the sheet and on a drafting view; place one manually to check the detail type, then re-run with bending_detail_type.");
            }
            if (probe != null) placed.Add(new { position = firstPos.Pos, rebar_id = firstPos.Bar.Id.Value, detail_id = probe.Id.Value });
            foreach (var p in positions.Skip(1))
            {
                var row = rowPos.IndexOf(p.Pos);
                if (row < 0) { failed.Add(p.Pos); continue; }
                var at = toSheet.Inverse.OfPoint(new XYZ(sketchX, centres[row], 0));
                var d = Place(p.Bar, at);
                if (d == null) failed.Add(p.Pos); else placed.Add(new { position = p.Pos, rebar_id = p.Bar.Id.Value, detail_id = d.Id.Value });
            }
            return new
            {
                schedule_id = schedule.Id.Value, schedule_instance_id = instance.Id.Value, sketches_on = host, sketches = placed, positions_not_sketched = failed,
                row_heights_set = rowsSet, row_height_mm = Math.Round(minRow * Units.MmPerFoot, 1), detail_type = detailType.Name,
                note = rowsSet == 0 ? "Revit kept its own row height; sketches were scaled to the actual rows. Enlarge the schedule's body text or row height and re-run if they are too small." : null
            };
        });
        return Json.Serialize(new
        {
            preview, profile = detected, mapping, construction = construction.Length > 0 ? construction : null, rebar_in_scope = bars.Count,
            positions = positions.Select(p => new { position = p.Pos, bars = p.Count, shapes = p.Shapes }),
            positions_with_several_shapes = positions.Where(p => p.Shapes > 1).Select(p => p.Pos),
            unnumbered_bars = unnumbered, result, revit_warnings = warnings
        });
    }

    private static string Cell(ViewSchedule s, int row, string heading)
    {
        var body = s.GetTableData().GetSectionData(SectionType.Body);
        for (int c = body.FirstColumnNumber; c < body.FirstColumnNumber + body.NumberOfColumns; c++)
            if (s.GetCellText(SectionType.Body, body.FirstRowNumber, c) == heading) return s.GetCellText(SectionType.Body, row, c);
        return "";
    }

    private static void SetWidthByHeading(ViewSchedule s, string heading, double width)
    {
        var def = s.Definition;
        for (int i = 0; i < def.GetFieldCount(); i++)
        {
            var f = def.GetField(i);
            if (!f.IsHidden && f.ColumnHeading == heading) { f.GridColumnWidth = width; return; }
        }
    }

    // The «Эскиз» graph is an always-empty text parameter on rebar, bound once per project with a
    // fixed GUID — a real schedule column that the sketches sit in.
    private static void EnsureSketchParameter(Document doc)
    {
        if (SharedParameterElement.Lookup(doc, SketchParameter) != null) return;
        var app = doc.Application; var previous = app.SharedParametersFilename;
        var path = Path.Combine(Path.GetTempPath(), "ClaudeRevit-bbs-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "# ClaudeRevit\n*META\tVERSION\tMINVERSION\nMETA\t2\t1\n*GROUP\tID\tNAME\nGROUP\t1\tClaudeRevit\n*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\n", Encoding.UTF8);
            app.SharedParametersFilename = path;
            var file = app.OpenSharedParameterFile() ?? throw new InvalidOperationException("Cannot open temporary shared parameter definitions.");
            var group = file.Groups.get_Item("ClaudeRevit");
            var definition = group.Definitions.Create(new ExternalDefinitionCreationOptions("CR_Эскиз", SpecTypeId.String.Text)
            { GUID = SketchParameter, Description = "Empty graph for bending sketches in the bar bending schedule (ведомость деталей)" });
            var set = app.Create.NewCategorySet(); set.Insert(Autodesk.Revit.DB.Category.GetCategory(doc, BuiltInCategory.OST_Rebar));
            if (!doc.ParameterBindings.Insert(definition, app.Create.NewInstanceBinding(set), GroupTypeId.Data))
                throw new InvalidOperationException("Cannot bind the CR_Эскиз rebar parameter.");
            doc.Regenerate();
        }
        finally { app.SharedParametersFilename = previous; try { File.Delete(path); } catch { } }
    }
}
