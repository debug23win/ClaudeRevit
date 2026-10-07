using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// IFC geometry (DirectShapes from an opened IFC or an IFC link — Renga, Tekla, nanoCAD BIM,
// ArchiCAD exports) → native walls, floors, structural columns and beams. Idea from
// LuDattilo/RevitCortex IFC rebuild tools and shuotao IFC structural sync (MIT); native code.
public sealed class IfcToNative : IRevitTool
{
    public string Name => "ifc_to_native";
    public string Description =>
        "Rebuild IFC elements (DirectShape geometry from an opened IFC file or an IFC link) as native Revit walls, " +
        "floors, structural columns and beams. Each element's geometry is fitted with a plan box (walls, columns, beams) " +
        "or its top face outline (floors); fill = solid volume / box volume tells how box-like it is, and elements below " +
        "min_fill are skipped and listed. Walls and floors take the type with the nearest thickness (create_types makes " +
        "exact ones by resizing the core layer); columns and beams take sized types of column_family / beam_family. " +
        "Openings are not rebuilt. mode=analyze (default) reports what would be built and how well it fits; mode=create " +
        "builds it (preview defaults true) and compares each new element's volume with the original. The originals " +
        "stay in place unless delete_originals=true (host model only).";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["mode"] = NativeToolUtil.Field("string", "analyze (default) | create."),
        ["link"] = NativeToolUtil.Field("string", "Read from this linked model (an IFC link) instead of the host model."),
        ["categories"] = NativeToolUtil.Array("string", "walls, floors, columns, beams (default all four)."),
        ["min_fill"] = NativeToolUtil.Field("number", "Skip elements whose volume fills less than this share of their box (default 0.7)."),
        ["column_family"] = NativeToolUtil.Field("string", "create: structural column family for columns."),
        ["beam_family"] = NativeToolUtil.Field("string", "create: structural framing family for beams."),
        ["create_types"] = NativeToolUtil.Field("boolean", "create: make exact-size types (default false: nearest, reported)."),
        ["structural_walls"] = NativeToolUtil.Field("boolean", "create: mark rebuilt walls structural (default false)."),
        ["delete_originals"] = NativeToolUtil.Field("boolean", "create: delete the host-model DirectShapes that were rebuilt (default false)."),
        ["limit"] = NativeToolUtil.Field("integer", "Max elements processed (default 2000)."),
        ["preview"] = NativeToolUtil.Field("boolean", "create: default true.")
    });

    private sealed record Candidate(Element Source, string Kind, PlanBox Box, double Volume, double Fill, CurveLoop? Outline, string? IfcGuid);

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var linkName = NativeToolUtil.Text(input, "link");
        Document source = doc; Transform toHost = Transform.Identity;
        if (linkName.Length > 0)
        {
            var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Where(l => l.GetLinkDocument() != null).ToList();
            var link = links.FirstOrDefault(l => l.Name.Contains(linkName, StringComparison.OrdinalIgnoreCase)) ?? throw NameResolve.Missing(linkName, "Loaded link", links.Select(l => l.Name));
            source = link.GetLinkDocument(); toHost = link.GetTotalTransform();
        }
        var kinds = input.TryGetValue("categories", out var cs) && cs.ValueKind == JsonValueKind.Array ? cs.EnumerateArray().Select(c => c.GetString() ?? "").ToHashSet() : ["walls", "floors", "columns", "beams"];
        var map = new Dictionary<BuiltInCategory, string>
        {
            [BuiltInCategory.OST_Walls] = "walls", [BuiltInCategory.OST_Floors] = "floors", [BuiltInCategory.OST_StructuralColumns] = "columns",
            [BuiltInCategory.OST_Columns] = "columns", [BuiltInCategory.OST_StructuralFraming] = "beams"
        };
        var minFill = input.TryGetValue("min_fill", out var mf) && mf.ValueKind == JsonValueKind.Number ? mf.GetDouble() : 0.7;
        var limit = input.TryGetValue("limit", out var lm) && lm.ValueKind == JsonValueKind.Number ? Math.Clamp(lm.GetInt32(), 1, 20000) : 2000;
        var shapes = new FilteredElementCollector(source).OfClass(typeof(DirectShape)).Cast<DirectShape>()
            .Where(d => d.Category != null && map.TryGetValue(d.Category.BuiltInCategory, out var k) && kinds.Contains(k)).Take(limit).ToList();
        if (shapes.Count == 0) throw new ToolInputException($"No IFC DirectShapes of {string.Join(", ", kinds)} in {(linkName.Length > 0 ? "the link" : "this model")}. Open the IFC (File ▸ Open ▸ IFC) or link it, then retry.");

        var candidates = new List<Candidate>(); var skipped = new List<object>();
        foreach (var d in shapes)
        {
            ToolContext.ThrowIfCancelled();
            var kind = map[d.Category!.BuiltInCategory];
            var solids = RebarGeometry.Solids(d).Select(s => toHost.IsIdentity ? s : SolidUtils.CreateTransformed(s, toHost)).ToList();
            var volume = solids.Sum(s => s.Volume) * Math.Pow(Units.MmPerFoot, 3);
            var pts = solids.SelectMany(s => s.Edges.Cast<Edge>().SelectMany(e => e.Tessellate())).Select(p => (p.X * Units.MmPerFoot, p.Y * Units.MmPerFoot, p.Z * Units.MmPerFoot)).ToList();
            if (volume <= 0 || pts.Count < 4) { skipped.Add(new { id = d.Id.Value, kind, reason = "no solid geometry" }); continue; }
            PlanBox box;
            try { box = BoxFit.Fit(pts); } catch (ArgumentException ex) { skipped.Add(new { id = d.Id.Value, kind, reason = ex.Message }); continue; }
            CurveLoop? outline = null; var fill = box.Volume > 0 ? volume / box.Volume : 0;
            if (kind == "floors")
            {
                var top = solids.SelectMany(s => s.Faces.Cast<Face>()).OfType<PlanarFace>().Where(f => f.FaceNormal.Z > 0.99).OrderByDescending(f => f.Area).FirstOrDefault();
                if (top == null) { skipped.Add(new { id = d.Id.Value, kind, reason = "no horizontal top face" }); continue; }
                outline = top.GetEdgesAsCurveLoops().OrderByDescending(l => l.GetExactLength()).First();
                fill = volume / (top.Area * Math.Pow(Units.MmPerFoot, 2) * box.Height);
            }
            if (fill < minFill) { skipped.Add(new { id = d.Id.Value, kind, fill = Math.Round(fill, 2), reason = "not box-like enough (openings, slopes or a complex shape)" }); continue; }
            var guid = d.LookupParameter("IfcGUID")?.AsString() ?? d.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString();
            candidates.Add(new(d, kind, box, volume, fill, outline, guid));
        }
        var analysis = new Dictionary<string, object?>
        {
            ["source"] = linkName.Length > 0 ? source.Title : "host model",
            ["found"] = shapes.GroupBy(s => map[s.Category!.BuiltInCategory]).ToDictionary(g => g.Key, g => g.Count()),
            ["rebuildable"] = candidates.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => new
            {
                count = g.Count(), mean_fill = Math.Round(g.Average(c => c.Fill), 2),
                sizes = g.GroupBy(c => c.Kind is "walls" or "floors" ? $"{Math.Round(c.Kind == "floors" ? c.Box.Height : c.Box.Width)} мм" : $"{Math.Round(c.Box.Width)}×{Math.Round(c.Kind == "beams" ? c.Box.Height : c.Box.Length)}")
                    .OrderByDescending(x => x.Count()).Take(8).Select(x => new { size = x.Key, count = x.Count() })
            }),
            ["skipped"] = skipped.Take(100), ["skipped_count"] = skipped.Count
        };
        if (NativeToolUtil.Text(input, "mode", "analyze") != "create")
        {
            analysis["next"] = "mode=create (preview first) with column_family / beam_family when columns or beams are rebuilt.";
            return Services.Json.Serialize(analysis);
        }

        FamilySymbol[] Family(string key, BuiltInCategory cat)
        {
            var name = NativeToolUtil.Text(input, key);
            var all = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(cat).Cast<FamilySymbol>().ToList();
            if (name.Length == 0) return [];
            var found = all.Where(s => s.Family.Name == name).ToArray();
            return found.Length > 0 ? found : throw NameResolve.Missing(name, "Family", all.Select(s => s.Family.Name).Distinct());
        }
        var columnSymbols = Family("column_family", BuiltInCategory.OST_StructuralColumns);
        var beamSymbols = Family("beam_family", BuiltInCategory.OST_StructuralFraming);
        if (candidates.Any(c => c.Kind == "columns") && columnSymbols.Length == 0) throw new ToolInputException("Give column_family to rebuild columns (or leave columns out of categories).");
        if (candidates.Any(c => c.Kind == "beams") && beamSymbols.Length == 0) throw new ToolInputException("Give beam_family to rebuild beams (or leave beams out of categories).");
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
        if (levels.Count == 0) throw new ToolInputException("The model has no levels.");
        Level LevelAt(double zMm) => levels.LastOrDefault(l => l.Elevation * Units.MmPerFoot <= zMm + 1) ?? levels[0];
        var wallTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().Where(t => t.Kind == WallKind.Basic).ToList();
        var floorTypes = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().Where(t => !t.IsFoundationSlab).ToList();
        var structural = ToolInput.Flag(input, "structural_walls");
        var deleteOriginals = ToolInput.Flag(input, "delete_originals") && linkName.Length == 0;
        var preview = NativeToolUtil.Preview(input);
        XYZ Ft(double x, double y, double z) => new(x / Units.MmPerFoot, y / Units.MmPerFoot, z / Units.MmPerFoot);

        var (result, warnings) = NativeToolUtil.Commit(doc, "Claude: IFC → родные элементы", preview, () =>
        {
            var sized = new SizedTypes(doc, ToolInput.Flag(input, "create_types"));
            var made = new List<(Candidate C, ElementId Id)>(); var failed = new List<object>();
            foreach (var c in candidates)
            {
                try
                {
                    var b = c.Box; var ax = b.Axis;
                    Pt End(double s) => new(b.Center.X + ax.X * s * b.Length / 2, b.Center.Y + ax.Y * s * b.Length / 2);
                    Element e;
                    switch (c.Kind)
                    {
                        case "walls":
                        {
                            var level = LevelAt(b.ZMin);
                            var type = sized.Host(wallTypes, b.Width, t => t.Width);
                            var a0 = End(-1); var a1 = End(1);
                            e = Wall.Create(doc, Line.CreateBound(Ft(a0.X, a0.Y, level.Elevation * Units.MmPerFoot), Ft(a1.X, a1.Y, level.Elevation * Units.MmPerFoot)),
                                type.Id, level.Id, b.Height / Units.MmPerFoot, b.ZMin / Units.MmPerFoot - level.Elevation, false, structural);
                            break;
                        }
                        case "floors":
                        {
                            var level = LevelAt(b.ZMin);
                            var type = sized.Host(floorTypes, b.Height, t => t.GetCompoundStructure()?.GetWidth() ?? 0);
                            var flat = CurveLoop.CreateViaTransform(c.Outline!, Transform.CreateTranslation(new XYZ(0, 0, level.Elevation - c.Outline!.First().GetEndPoint(0).Z)));
                            var floor = Floor.Create(doc, [flat], type.Id, level.Id);
                            floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(b.ZMax / Units.MmPerFoot - level.Elevation);
                            e = floor;
                            break;
                        }
                        case "columns":
                        {
                            var level = LevelAt(b.ZMin);
                            var symbol = sized.Family(columnSymbols, SizedTypes.WidthNames, Math.Round(b.Width), SizedTypes.DepthNames, Math.Round(b.Length));
                            var fi = doc.Create.NewFamilyInstance(Ft(b.Center.X, b.Center.Y, level.Elevation * Units.MmPerFoot), symbol, level, StructuralType.Column);
                            fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM)?.Set(b.ZMin / Units.MmPerFoot - level.Elevation);
                            fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(level.Id);
                            fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.Set(b.ZMax / Units.MmPerFoot - level.Elevation);
                            var turn = b.AngleRad - Math.PI / 2;
                            if (Math.Abs(Math.Sin(turn)) > 1e-6) ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(Ft(b.Center.X, b.Center.Y, 0), Ft(b.Center.X, b.Center.Y, 1000)), turn);
                            e = fi;
                            break;
                        }
                        default:
                        {
                            var level = LevelAt(b.ZMax);
                            var symbol = sized.Family(beamSymbols, SizedTypes.WidthNames, Math.Round(b.Width), SizedTypes.DepthNames, Math.Round(b.Height));
                            var a0 = End(-1); var a1 = End(1);
                            e = doc.Create.NewFamilyInstance(Line.CreateBound(Ft(a0.X, a0.Y, b.ZMax), Ft(a1.X, a1.Y, b.ZMax)), symbol, level, StructuralType.Beam);
                            break;
                        }
                    }
                    if (c.IfcGuid != null && e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) is { IsReadOnly: false } cm && string.IsNullOrEmpty(cm.AsString()))
                        cm.Set("IfcGUID " + c.IfcGuid);
                    made.Add((c, e.Id));
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ArgumentException or Autodesk.Revit.Exceptions.InvalidOperationException or ToolInputException)
                { failed.Add(new { id = c.Source.Id.Value, kind = c.Kind, reason = ex.Message }); }
            }
            doc.Regenerate();
            var comparison = made.Select(m =>
            {
                var v = RebarGeometry.Solids(doc.GetElement(m.Id)).Sum(s => s.Volume) * Math.Pow(Units.MmPerFoot, 3);
                return new { source_id = m.C.Source.Id.Value, new_id = m.Id.Value, kind = m.C.Kind, volume_diff_pct = m.C.Volume > 0 ? Math.Round((v - m.C.Volume) / m.C.Volume * 100, 1) : 0 };
            }).ToList();
            if (deleteOriginals) doc.Delete(made.Select(m => m.C.Source.Id).ToList());
            return new
            {
                created = made.GroupBy(m => m.C.Kind).ToDictionary(g => g.Key, g => g.Count()),
                volume_check = new { within_5pct = comparison.Count(x => Math.Abs(x.volume_diff_pct) <= 5), outside_5pct = comparison.Where(x => Math.Abs(x.volume_diff_pct) > 5).Take(50) },
                types_created = sized.Created, size_mismatches = sized.Mismatches.Distinct(), failed, originals_deleted = deleteOriginals
            };
        });
        analysis["preview"] = preview; analysis["result"] = result; analysis["revit_warnings"] = warnings;
        return Services.Json.Serialize(analysis);
    }
}
