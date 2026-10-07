using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// CAD underlay (linked or imported DWG) → grids, columns and beams, by layer. Ideas from
// shuotao/REVIT_MCP_study (DWG beam/column import; MIT) and HorizunGroup (CAD → BIM; Apache-2.0);
// native implementation over the shared shape logic in Services.PdfDrawing / DwgShapes.
public sealed class DwgToModel : IRevitTool
{
    public string Name => "dwg_to_model";
    public string Description =>
        "Turn a linked or imported DWG plan into native grids, structural columns and beams, by layer. mode=analyze " +
        "(default, read-only) lists the DWG's layers with line counts and what each detector finds; mode=create builds " +
        "them (preview defaults true). Layers: grid_layers / column_layers / beam_layers (default: matched by name — " +
        "ОСИ/AXIS/GRID, КОЛОН/COLUMN, БАЛК/РИГЕЛ/BEAM). Grids are long lines, named the Russian way (1, 2, 3… left to " +
        "right; А, Б, В… bottom to top) because DWG text is not readable through the API; existing grids are kept. " +
        "Columns are closed rectangles (b × h, rotation) or circles; beams are parallel line pairs (centre line + width). " +
        "Types are matched by size in column_family / beam_family; with create_types=true missing sizes are made by " +
        "duplicating a type and setting its size parameters.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["import_id"] = NativeToolUtil.Field("integer", "The DWG ImportInstance (default: the only one in the model)."),
        ["mode"] = NativeToolUtil.Field("string", "analyze (default) | create."),
        ["grid_layers"] = NativeToolUtil.Array("string", "Layers with grid axes."),
        ["column_layers"] = NativeToolUtil.Array("string", "Layers with column outlines."),
        ["beam_layers"] = NativeToolUtil.Array("string", "Layers with beam outlines."),
        ["grid_min_length_mm"] = NativeToolUtil.Field("number", "Shortest grid line (default 3000)."),
        ["beam_width_mm"] = NativeToolUtil.Any("[min, max] beam widths (default [100, 1200])."),
        ["level"] = NativeToolUtil.Field("string", "create: base level for columns and level of beams."),
        ["top_level"] = NativeToolUtil.Field("string", "create: column top level (default the next level up)."),
        ["column_family"] = NativeToolUtil.Field("string", "Structural column family name."),
        ["beam_family"] = NativeToolUtil.Field("string", "Structural framing family name."),
        ["size_parameters"] = NativeToolUtil.Any("Type parameter names {width, depth, diameter}, default tries b/h, Ширина/Высота, ADSK_Размер_Ширина/Высота, d/Диаметр."),
        ["beam_depth_mm"] = NativeToolUtil.Field("number", "create: beam depth when types are created (default 2 × width)."),
        ["create_types"] = NativeToolUtil.Field("boolean", "Create missing size types (default false: use the nearest existing type and report the difference)."),
        ["preview"] = NativeToolUtil.Field("boolean", "create: default true.")
    });

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
        var import = input.TryGetValue("import_id", out var iid) && iid.ValueKind == JsonValueKind.Number
            ? doc.GetElement(new ElementId(iid.GetInt64())) as ImportInstance ?? throw new ToolInputException($"Element {iid.GetInt64()} is not a CAD import or link.")
            : imports.Count == 1 ? imports[0] : throw new ToolInputException(imports.Count == 0 ? "The model has no DWG import or link." : $"{imports.Count} CAD imports: give import_id ({string.Join(", ", imports.Take(10).Select(i => $"{i.Id.Value} {doc.GetElement(i.GetTypeId())?.Name}"))}).");
        var byLayer = Read(doc, import);
        if (byLayer.Count == 0) throw new ToolInputException("No curves found in this CAD import (is it a 3D or image-only file?).");
        List<string> Layers(string key, string[] patterns)
        {
            if (input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Array)
            {
                var names = v.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                foreach (var n in names) if (!byLayer.ContainsKey(n)) throw NameResolve.Missing(n, "DWG layer", byLayer.Keys);
                return names;
            }
            return byLayer.Keys.Where(l => patterns.Any(p => l.Contains(p, StringComparison.OrdinalIgnoreCase))).ToList();
        }
        var gridLayers = Layers("grid_layers", ["ОСИ", "AXIS", "GRID", "AXES"]);
        var columnLayers = Layers("column_layers", ["КОЛОН", "COLUMN", "COLS", "S-COL"]);
        var beamLayers = Layers("beam_layers", ["БАЛК", "РИГЕЛ", "BEAM", "S-BEAM", "FRAM"]);
        double Num(string k, double d) => input.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;

        var gridSegs = PdfDrawing.MergeCollinear(gridLayers.SelectMany(l => byLayer[l].Segments).ToList(), 50, 2);
        var grids = DwgShapes.NameGrids(gridSegs.Where(s => s.Length >= Num("grid_min_length_mm", 3000)).ToList());
        var existingGrids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().Where(g => g.Curve is Line).ToList();
        bool GridExists(Seg s)
        {
            var mid = new XYZ((s.A.X + s.B.X) / 2 / Units.MmPerFoot, (s.A.Y + s.B.Y) / 2 / Units.MmPerFoot, 0);
            return existingGrids.Any(g => { var l = (Line)g.Curve; var flat = Line.CreateUnbound(new XYZ(l.Origin.X, l.Origin.Y, 0), new XYZ(l.Direction.X, l.Direction.Y, 0)); return flat.Distance(mid) < 10 / Units.MmPerFoot; });
        }
        var columns = columnLayers.SelectMany(l => DwgShapes.Rectangles(byLayer[l].Segments)).ToList();
        var circles = columnLayers.SelectMany(l => byLayer[l].Circles).ToList();
        var range = input.TryGetValue("beam_width_mm", out var bw) && bw.ValueKind == JsonValueKind.Array ? bw.EnumerateArray().Select(x => x.GetDouble()).ToArray() : [100, 1200];
        var beams = beamLayers.Count == 0 ? [] : PdfDrawing.DetectWalls(PdfDrawing.MergeCollinear(beamLayers.SelectMany(l => byLayer[l].Segments).ToList(), 2), range[0], range[1], 500);

        var summary = new Dictionary<string, object?>
        {
            ["import"] = new { id = import.Id.Value, name = doc.GetElement(import.GetTypeId())?.Name, linked = import.IsLinked },
            ["layers"] = byLayer.OrderByDescending(kv => kv.Value.Segments.Count).Take(60).Select(kv => new { layer = kv.Key, lines = kv.Value.Segments.Count, circles = kv.Value.Circles.Count }),
            ["grid_layers"] = gridLayers, ["column_layers"] = columnLayers, ["beam_layers"] = beamLayers,
            ["grids"] = grids.Select(g => new { name = g.Name, length_mm = Math.Round(g.Line.Length), exists = GridExists(g.Line) }),
            ["columns"] = columns.GroupBy(c => $"{c.Width:0}×{c.Depth:0}").Select(g => new { size = g.Key, count = g.Count() }).Concat(circles.GroupBy(c => $"Ø{c.D:0}").Select(g => new { size = g.Key, count = g.Count() })),
            ["beams"] = beams.GroupBy(b => Math.Round(b.Thickness / 10) * 10).Select(g => new { width_mm = g.Key, count = g.Count(), total_length_m = Math.Round(g.Sum(b => (b.B - b.A).Length) / 1000, 1) }),
        };
        if (NativeToolUtil.Text(input, "mode", "analyze") != "create")
        {
            summary["next"] = "Check layers and counts; then mode=create with level, column_family / beam_family (preview first).";
            return Services.Json.Serialize(summary);
        }

        var level = Circulation.Level(doc, input, "level");
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
        var topName = NativeToolUtil.Text(input, "top_level");
        var top = topName.Length > 0 ? levels.FirstOrDefault(l => l.Name == topName) ?? throw NameResolve.MissingLevel(doc, topName) : levels.FirstOrDefault(l => l.Elevation > level.Elevation + 1e-6);
        var sizes = input.TryGetValue("size_parameters", out var sp) && sp.ValueKind == JsonValueKind.Object ? sp : default;
        string[] Names(string key, string[] defaults) => sizes.ValueKind == JsonValueKind.Object && sizes.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.String ? [n.GetString()!] : defaults;
        var widthNames = Names("width", ["b", "Ширина", "ADSK_Размер_Ширина", "Width"]);
        var depthNames = Names("depth", ["h", "Высота", "ADSK_Размер_Высота", "Глубина", "Depth", "Height"]);
        var diameterNames = Names("diameter", ["d", "Диаметр", "ADSK_Размер_Диаметр", "Diameter"]);
        var createTypes = ToolInput.Flag(input, "create_types");
        var beamDepth = Num("beam_depth_mm", 0);
        FamilySymbol[] Family(string key, BuiltInCategory cat)
        {
            var name = NativeToolUtil.Text(input, key);
            var all = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(cat).Cast<FamilySymbol>().ToList();
            if (name.Length == 0) return [];
            var found = all.Where(s => s.Family.Name == name).ToArray();
            return found.Length > 0 ? found : throw NameResolve.Missing(name, cat == BuiltInCategory.OST_StructuralColumns ? "Column family" : "Framing family", all.Select(s => s.Family.Name).Distinct());
        }
        var columnSymbols = Family("column_family", BuiltInCategory.OST_StructuralColumns);
        var beamSymbols = Family("beam_family", BuiltInCategory.OST_StructuralFraming);
        if (columns.Count + circles.Count > 0 && columnSymbols.Length == 0) throw new ToolInputException("Give column_family for the columns found (or empty column_layers).");
        if (beams.Count > 0 && beamSymbols.Length == 0) throw new ToolInputException("Give beam_family for the beams found (or empty beam_layers).");
        var preview = NativeToolUtil.Preview(input);
        var z = level.Elevation;
        XYZ P(Pt p) => new(p.X / Units.MmPerFoot, p.Y / Units.MmPerFoot, z);

        var (made, warnings) = NativeToolUtil.Commit(doc, "Claude: DWG → модель", preview, () =>
        {
            var created = new { grids = new List<string>(), columns = new List<long>(), beams = new List<long>() };
            var sized = new SizedTypes(doc, createTypes);
            FamilySymbol Sized(FamilySymbol[] symbols, string[] wNames, double wMm, string[]? dNames, double dMm) => sized.Family(symbols, wNames, wMm, dNames, dMm);
            foreach (var (line, name) in grids)
            {
                if (GridExists(line)) continue;
                var g = Grid.Create(doc, Line.CreateBound(P(line.A), P(line.B)));
                try { g.Name = name; } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                created.grids.Add(g.Name);
            }
            foreach (var c in columns)
            {
                var symbol = Sized(columnSymbols, widthNames, c.Width, depthNames, c.Depth);
                var fi = doc.Create.NewFamilyInstance(P(c.Center), symbol, level, StructuralType.Column);
                if (top != null) fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(top.Id);
                if (Math.Abs(c.AngleRad) > 1e-6) ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(P(c.Center), P(c.Center) + XYZ.BasisZ), c.AngleRad);
                created.columns.Add(fi.Id.Value);
            }
            foreach (var c in circles)
            {
                var symbol = Sized(columnSymbols, diameterNames, c.D, null, 0);
                var fi = doc.Create.NewFamilyInstance(P(c.Center), symbol, level, StructuralType.Column);
                if (top != null) fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(top.Id);
                created.columns.Add(fi.Id.Value);
            }
            foreach (var b in beams)
            {
                var depth = beamDepth > 0 ? beamDepth : b.Thickness * 2;
                var symbol = Sized(beamSymbols, widthNames, Math.Round(b.Thickness), depthNames, depth);
                var fi = doc.Create.NewFamilyInstance(Line.CreateBound(P(b.A), P(b.B)), symbol, level, StructuralType.Beam);
                created.beams.Add(fi.Id.Value);
            }
            return new { created.grids, created.columns, created.beams, types_created = sized.Created, size_mismatches = sized.Mismatches };
        });
        summary["preview"] = preview; summary["created"] = made; summary["revit_warnings"] = warnings;
        return Services.Json.Serialize(summary);
    }

    private sealed record LayerGeometry(List<Seg> Segments, List<(Pt Center, double D)> Circles);

    // Curves of the import in model coordinates (mm, plan), by DWG layer.
    private static Dictionary<string, LayerGeometry> Read(Document doc, ImportInstance import)
    {
        var result = new Dictionary<string, LayerGeometry>(StringComparer.OrdinalIgnoreCase);
        LayerGeometry Layer(GeometryObject o)
        {
            var name = (doc.GetElement(o.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name ?? "(no layer)";
            return result.TryGetValue(name, out var g) ? g : result[name] = new(new(), new());
        }
        Pt Mm(XYZ p) => new(p.X * Units.MmPerFoot, p.Y * Units.MmPerFoot);
        void Walk(GeometryElement? g, int depth)
        {
            if (g == null || depth > 6) return;
            foreach (var o in g)
            {
                switch (o)
                {
                    case GeometryInstance gi: Walk(gi.GetInstanceGeometry(), depth + 1); break;
                    case Line l: Layer(l).Segments.Add(new(Mm(l.GetEndPoint(0)), Mm(l.GetEndPoint(1)), 0)); break;
                    case PolyLine pl:
                        var pts = pl.GetCoordinates();
                        var layer = Layer(pl);
                        for (int i = 0; i + 1 < pts.Count; i++) if (pts[i].DistanceTo(pts[i + 1]) > 1e-6) layer.Segments.Add(new(Mm(pts[i]), Mm(pts[i + 1]), 0));
                        break;
                    case Arc a when !a.IsBound || Math.Abs(a.Length - 2 * Math.PI * a.Radius) < 1e-3:
                        Layer(a).Circles.Add((Mm(a.Center), a.Radius * 2 * Units.MmPerFoot));
                        break;
                }
            }
        }
        Walk(import.get_Geometry(new Options { ComputeReferences = false, IncludeNonVisibleObjects = false }), 0);
        foreach (var g in result.Values)
        {
            // A circle drawn as two half arcs is not caught above; duplicate full circles collapse.
            var unique = g.Circles.GroupBy(c => (Math.Round(c.Center.X), Math.Round(c.Center.Y), Math.Round(c.D))).Select(x => x.First()).ToList();
            g.Circles.Clear(); g.Circles.AddRange(unique);
        }
        return result;
    }
}
