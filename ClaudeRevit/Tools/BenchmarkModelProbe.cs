using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace ClaudeRevit.Tools;

// Objective, bounded native evidence. Unsupported properties are reported, not replaced by
// the modeller's narration. Large models are explicitly marked as truncated.
internal static class BenchmarkModelProbe
{
    private const int Limit = 2000;
    public static List<long> AllElementIds(Document doc) => new FilteredElementCollector(doc)
        .WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(), new ElementIsElementTypeFilter(true)))
        .ToElementIds().Select(id => id.Value).ToList();
    public static void Append(Document doc, Dictionary<string, object?> result)
    {
        result["is_family_document"] = doc.IsFamilyDocument;
        var errors = new List<string>();
        void Collect<T>(string name, Func<T, object> snapshot) where T : Element
        {
            try
            {
                var collector = new FilteredElementCollector(doc).OfClass(typeof(T));
                var count = collector.GetElementCount();
                result[name + "_count"] = count;
                result[name + "_truncated"] = count > Limit;
                result[name] = collector.Cast<T>().Take(Limit).Select(e =>
                {
                    try { return snapshot(e); }
                    catch (Exception ex) { return (object)new { id = e.Id.Value, evidence_error = ex.Message }; }
                }).ToArray();
            }
            catch (Exception ex) { errors.Add(name + ": " + ex.Message); }
        }
        Collect<Level>("level_elements", l => new { id = l.Id.Value, name = l.Name, elevation_m = l.Elevation * 0.3048 });
        Collect<Grid>("grid_elements", g => new { id = g.Id.Value, name = g.Name, curve = Curve(g.Curve) });
        Collect<Wall>("wall_elements", w => new { id = w.Id.Value, type_id = w.GetTypeId().Value, level_id = w.LevelId.Value,
            curve = w.Location is LocationCurve lc ? Curve(lc.Curve) : null, comments = w.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() });
        Collect<Floor>("floor_elements", f => new { id = f.Id.Value, type_id = f.GetTypeId().Value, level_id = f.LevelId.Value,
            bounds = Bounds(f), area_m2 = f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() * 0.3048 * 0.3048 });
        Collect<FamilyInstance>("family_instances", f => new { id = f.Id.Value, family = f.Symbol.Family.Name, type = f.Symbol.Name,
            type_id = f.GetTypeId().Value, category_id = f.Category?.Id.Value, category = f.Category?.Name,
            host_id = f.Host?.Id.Value, level_id = f.LevelId.Value, bounds = Bounds(f),
            location = f.Location is LocationPoint lp ? NativeToolUtil.Mm(lp.Point) : null,
            curve = f.Location is LocationCurve lc ? Curve(lc.Curve) : null });
        Collect<Rebar>("rebar_elements", r =>
        {
            var type = doc.GetElement(r.GetTypeId()) as RebarBarType;
            // First and last bar positions expose actual set distribution, including shape/free-form geometry.
            var positions = new[] { 0, Math.Max(0, r.NumberOfBarPositions - 1) }.Distinct().Select(p => new
            {
                position = p, curves = r.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeAllMultiplanarCurves, p).Take(100).Select(Curve).ToArray()
            }).ToArray();
            return new { id = r.Id.Value, host_id = r.GetHostId().Value, type_id = r.GetTypeId().Value,
                diameter_mm = type?.BarNominalDiameter * Units.MmPerFoot, quantity = r.Quantity,
                bar_positions = r.NumberOfBarPositions, total_length_m = r.TotalLength * 0.3048,
                layout = r.LayoutRule.ToString(), bounds = Bounds(r), host_bounds = Bounds(doc.GetElement(r.GetHostId())),
                centerlines = positions,
                spacing_mm = r.get_Parameter(BuiltInParameter.REBAR_ELEM_BAR_SPACING)?.AsDouble() * Units.MmPerFoot };
        });
        Collect<Opening>("openings", o => new { id = o.Id.Value, host_id = o.Host?.Id.Value, bounds = Bounds(o) });
        Collect<ViewSheet>("sheets", s => new { id = s.Id.Value, number = s.SheetNumber, name = s.Name,
            placed_view_ids = s.GetAllPlacedViews().Select(i => i.Value).ToArray() });
        Collect<Viewport>("viewports", v => new { id = v.Id.Value, sheet_id = v.SheetId.Value, view_id = v.ViewId.Value });
        Collect<ViewPlan>("plans", v => new { id = v.Id.Value, name = v.Name, level_id = v.GenLevel?.Id.Value });
        Collect<Dimension>("dimensions", d => new { id = d.Id.Value, owner_view_id = d.OwnerViewId.Value,
            label = doc.IsFamilyDocument ? d.FamilyLabel?.Definition.Name : null });
        Collect<IndependentTag>("tags", t => new { id = t.Id.Value, owner_view_id = t.OwnerViewId.Value,
            local_tagged_ids = t.GetTaggedLocalElementIds().Select(i => i.Value).ToArray() });
        Collect<ViewSchedule>("schedules", s =>
        {
            var definition = s.Definition; var body = s.GetTableData().GetSectionData(SectionType.Body);
            var rows = new List<string[]>();
            for (var r = body.FirstRowNumber; r <= body.LastRowNumber && rows.Count < 200; r++)
            {
                var cells = new List<string>();
                for (var c = body.FirstColumnNumber; c <= body.LastColumnNumber && cells.Count < 30; c++)
                    cells.Add(s.GetCellText(SectionType.Body, r, c));
                rows.Add(cells.ToArray());
            }
            return new { id = s.Id.Value, name = s.Name, category_id = definition.CategoryId.Value,
                fields = definition.GetFieldOrder().Select(id => definition.GetField(id).GetName()).ToArray(),
                sort_group_count = definition.GetSortGroupFieldCount(), rows, body_rows = body.NumberOfRows,
                rows_truncated = body.NumberOfRows > rows.Count };
        });
        if (doc.IsFamilyDocument)
        {
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToArray();
            result["nested_seed_types"] = symbols.Where(s => s.Family.IsEditable && !s.Family.IsInPlace)
                .Take(200).Select(s => new { id = s.Id.Value, family = s.Family.Name, type = s.Name,
                    placement = s.Family.FamilyPlacementType.ToString() }).ToArray();
        }
        result["evidence_errors"] = errors;
    }

    private static object Curve(Autodesk.Revit.DB.Curve c) => new { kind = c.GetType().Name, length_mm = c.Length * Units.MmPerFoot,
        points_mm = new[] { c.Evaluate(0, true), c.Evaluate(0.25, true), c.Evaluate(0.5, true), c.Evaluate(0.75, true), c.Evaluate(1, true) }.Select(NativeToolUtil.Mm).ToArray() };
    private static object? Bounds(Element? e)
    {
        var bb = e?.get_BoundingBox(null); if (bb == null) return null;
        var corners = (from x in new[] { bb.Min.X, bb.Max.X } from y in new[] { bb.Min.Y, bb.Max.Y }
                       from z in new[] { bb.Min.Z, bb.Max.Z } select bb.Transform.OfPoint(new XYZ(x, y, z))).ToArray();
        return new { min_mm = NativeToolUtil.Mm(new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z))),
            max_mm = NativeToolUtil.Mm(new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z))) };
    }
}
