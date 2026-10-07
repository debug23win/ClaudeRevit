using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// Vector PDF plan → grids and walls. Idea from the vietnguyen0603 fork of mcp-servers-for-revit
// (pdf_extract with calibration); extraction/calibration/detection live in Services.PdfDrawing.
public sealed class PdfToModel : IRevitTool
{
    public string Name => "pdf_to_model";
    public string Description =>
        "Turn a vector PDF plan attached to the chat into grids and walls. mode=analyze (default, read-only) reports " +
        "the page, grid-label candidates in PDF points and — once calibrated — wall and grid candidates in model mm. " +
        "Calibration: {points:[{pdf:[x,y], model_mm:[x,y]}, {…}]} (two known points), {scale:100, pdf_origin:[x,y], " +
        "model_origin_mm:[x,y], rotation_deg} or {by_model_grids:true} (grids named in both drawing and model). " +
        "mode=create builds walls (type matched by thickness) on `level` and grids not already in the model; preview " +
        "defaults true. Use region_pdf to exclude the title block and legends. Scanned (raster) PDFs have no vectors.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["attachment_id"] = NativeToolUtil.Field("string", "The attached PDF."),
        ["page"] = NativeToolUtil.Field("integer", "Page number (default 1)."),
        ["mode"] = NativeToolUtil.Field("string", "analyze (default) | create."),
        ["calibration"] = NativeToolUtil.Any("{points:[{pdf:[x,y],model_mm:[x,y]},{...}]} | {scale, pdf_origin, model_origin_mm, rotation_deg} | {by_model_grids:true}."),
        ["region_pdf"] = NativeToolUtil.Any("[xmin, ymin, xmax, ymax] in PDF points: only geometry inside."),
        ["detect"] = NativeToolUtil.Array("string", "walls and/or grids (default both)."),
        ["wall_thickness_mm"] = NativeToolUtil.Any("[min, max] plausible wall thickness, default [80, 700]."),
        ["min_wall_length_mm"] = NativeToolUtil.Field("number", "Default 300."),
        ["grid_min_length_mm"] = NativeToolUtil.Field("number", "Default 3000."),
        ["level"] = NativeToolUtil.Field("string", "create: level for walls."),
        ["wall_height_mm"] = NativeToolUtil.Field("number", "create: unconnected wall height (default 3000)."),
        ["preview"] = NativeToolUtil.Field("boolean", "create: default true.")
    }, "attachment_id");

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var path = AttachmentStore.LocalPath(AttachmentStore.CurrentScope, ToolInput.RequiredString(input, "attachment_id"));
        if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) throw new ToolInputException("The attachment is not a PDF.");
        var pageNo = input.TryGetValue("page", out var pg) && pg.ValueKind == JsonValueKind.Number ? pg.GetInt32() : 1;
        var page = PdfDrawing.Read(path, pageNo);
        var segs = page.Segments; var labels = page.Labels;
        if (input.TryGetValue("region_pdf", out var rg) && rg.ValueKind == JsonValueKind.Array)
        {
            var r = rg.EnumerateArray().Select(v => v.GetDouble()).ToArray();
            if (r.Length != 4) throw new ToolInputException("region_pdf is [xmin, ymin, xmax, ymax].");
            bool In(Pt p) => p.X >= r[0] && p.X <= r[2] && p.Y >= r[1] && p.Y <= r[3];
            segs = segs.Where(s => In(s.A) && In(s.B)).ToList(); labels = labels.Where(l => In(l.Center)).ToList();
        }
        if (segs.Count == 0) throw new ToolInputException("No vector lines on this page (or region) — a scanned PDF needs tracing, not import.");
        var detect = input.TryGetValue("detect", out var dt) && dt.ValueKind == JsonValueKind.Array ? dt.EnumerateArray().Select(d => d.GetString()).ToHashSet() : ["walls", "grids"];

        // Grid-label candidates in PDF space: what a human (or the model) picks calibration points from.
        var pdfGridSegs = PdfDrawing.MergeCollinear(segs, 6);
        var pdfGrids = PdfDrawing.DetectGrids(pdfGridSegs, labels, Math.Min(page.WidthPt, page.HeightPt) * 0.2, 18);
        var modelGrids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().Where(g => g.Curve is Line)
            .ToDictionary(g => g.Name, g => (new Pt(g.Curve.GetEndPoint(0).X * Units.MmPerFoot, g.Curve.GetEndPoint(0).Y * Units.MmPerFoot),
                                            new Pt(g.Curve.GetEndPoint(1).X * Units.MmPerFoot, g.Curve.GetEndPoint(1).Y * Units.MmPerFoot)), StringComparer.OrdinalIgnoreCase);
        var (calibration, rms, calPoints) = Calibrate(input, pdfGrids, modelGrids);
        var summary = new Dictionary<string, object?>
        {
            ["page"] = page.Page, ["page_count"] = page.PageCount, ["page_size_pt"] = new[] { page.WidthPt, page.HeightPt },
            ["rotation"] = page.Rotation, ["segments"] = segs.Count, ["curves"] = page.Curves, ["text_labels"] = labels.Count, ["truncated"] = page.Truncated,
            ["line_widths_pt"] = segs.GroupBy(s => Math.Round(s.Width, 2)).OrderByDescending(g => g.Count()).Take(6).Select(g => new { width = g.Key, count = g.Count() }),
            ["grid_labels_pdf"] = pdfGrids.Take(60).Select(g => new { label = g.Label, a = new[] { g.A.X, g.A.Y }, b = new[] { g.B.X, g.B.Y }, in_model = modelGrids.ContainsKey(g.Label) }),
        };
        if (page.Rotation != 0) summary["warning"] = $"Page is rotated {page.Rotation}°; coordinates are in the unrotated page space — calibrate with points, not scale.";
        if (calibration == null)
        {
            summary["next"] = "Calibrate: two known points, a plot scale with an origin, or by_model_grids when the model has grids named like grid_labels_pdf.";
            return Json.Serialize(summary);
        }
        summary["calibration"] = new { scale_mm_per_pt = calibration.Scale, implied_scale = "1:" + Math.Round(calibration.ImpliedDenominator), rotation_deg = calibration.Rotation * 180 / Math.PI, rms_mm = rms, points = calPoints };

        var mm = segs.Select(calibration.Apply).ToList();
        var mmLabels = labels.Select(l => l with { Center = calibration.Apply(l.Center) }).ToList();
        double Num(string key, double d) => input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;
        var range = input.TryGetValue("wall_thickness_mm", out var wt) && wt.ValueKind == JsonValueKind.Array ? wt.EnumerateArray().Select(v => v.GetDouble()).ToArray() : [80, 700];
        if (range.Length != 2 || range[0] <= 0 || range[1] <= range[0]) throw new ToolInputException("wall_thickness_mm is [min, max] with 0 < min < max.");
        var walls = detect.Contains("walls") ? PdfDrawing.DetectWalls(PdfDrawing.MergeCollinear(mm, 2), range[0], range[1], Num("min_wall_length_mm", 300)) : [];
        var paperMm = calibration.Scale / Similarity.MmPerPoint;   // model mm per paper mm
        var grids = detect.Contains("grids")
            ? PdfDrawing.DetectGrids(PdfDrawing.MergeCollinear(mm, 6 * paperMm), mmLabels, Num("grid_min_length_mm", 3000), 12 * paperMm) : [];
        summary["walls_found"] = walls.Count;
        summary["wall_thicknesses_mm"] = walls.GroupBy(w => Math.Round(w.Thickness / 5) * 5).OrderByDescending(g => g.Count()).Take(8).Select(g => new { thickness = g.Key, count = g.Count() });
        summary["grids_found"] = grids.Select(g => g.Label);

        if (NativeToolUtil.Text(input, "mode", "analyze") != "create")
        {
            summary["wall_sample_mm"] = walls.Take(40).Select(w => new { a = new[] { w.A.X, w.A.Y }, b = new[] { w.B.X, w.B.Y }, thickness = w.Thickness });
            summary["next"] = "Check the thickness histogram and samples, then mode=create with level (preview first).";
            return Json.Serialize(summary);
        }

        var level = Circulation.Level(doc, input, "level");
        if (walls.Count > 3000) throw new ToolInputException($"{walls.Count} wall candidates — narrow region_pdf or wall_thickness_mm first.");
        var height = Num("wall_height_mm", 3000) / Units.MmPerFoot;
        var wallTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().Where(t => t.Kind == WallKind.Basic && t.Width > 0).ToList();
        if (detect.Contains("walls") && wallTypes.Count == 0) throw new ToolInputException("The project has no basic wall types.");
        var preview = NativeToolUtil.Preview(input);
        var z = level.Elevation; var shortest = doc.Application.ShortCurveTolerance;
        var ((made, gridsMade, typeUse, skipped), warnings) = NativeToolUtil.Commit(doc, "Claude: PDF → модель", preview, () =>
        {
            var wallIds = new List<long>(); var gridIds = new List<object>(); var use = new Dictionary<string, int>(); var skip = new List<string>();
            foreach (var w in walls)
            {
                var a = new XYZ(w.A.X / Units.MmPerFoot, w.A.Y / Units.MmPerFoot, z); var b = new XYZ(w.B.X / Units.MmPerFoot, w.B.Y / Units.MmPerFoot, z);
                if (a.DistanceTo(b) < shortest) continue;
                var type = wallTypes.OrderBy(t => Math.Abs(t.Width * Units.MmPerFoot - w.Thickness)).First();
                var label = $"{type.Name} ({Math.Round(type.Width * Units.MmPerFoot)} мм) ← {Math.Round(w.Thickness)} мм";
                use[label] = use.GetValueOrDefault(label) + 1;
                wallIds.Add(Wall.Create(doc, Line.CreateBound(a, b), type.Id, level.Id, height, 0, false, false).Id.Value);
            }
            foreach (var g in grids)
            {
                if (modelGrids.ContainsKey(g.Label)) { skip.Add($"grid {g.Label} already exists"); continue; }
                var grid = Grid.Create(doc, Line.CreateBound(new XYZ(g.A.X / Units.MmPerFoot, g.A.Y / Units.MmPerFoot, z), new XYZ(g.B.X / Units.MmPerFoot, g.B.Y / Units.MmPerFoot, z)));
                try { grid.Name = g.Label; } catch (Autodesk.Revit.Exceptions.ArgumentException) { skip.Add($"grid name {g.Label} rejected; kept as {grid.Name}"); }
                gridIds.Add(new { id = grid.Id.Value, name = grid.Name });
            }
            return (wallIds, gridIds, use, skip);
        });
        summary["preview"] = preview; summary["walls_created"] = made.Count; summary["wall_types"] = typeUse; summary["grids_created"] = gridsMade;
        summary["skipped"] = skipped; summary["revit_warnings"] = warnings;
        return Json.Serialize(summary);
    }

    private static (Similarity? T, double? Rms, int Points) Calibrate(IReadOnlyDictionary<string, JsonElement> input,
        List<GridCandidate> pdfGrids, Dictionary<string, (Pt, Pt)> modelGrids)
    {
        if (!input.TryGetValue("calibration", out var c) || c.ValueKind != JsonValueKind.Object) return (null, null, 0);
        Pt P(JsonElement e) { var a = e.EnumerateArray().Select(v => v.GetDouble()).ToArray(); return a.Length == 2 ? new(a[0], a[1]) : throw new ToolInputException("Calibration points are [x, y]."); }
        if (c.TryGetProperty("points", out var pts))
        {
            var list = pts.EnumerateArray().ToList();
            if (list.Count != 2) throw new ToolInputException("Point calibration takes exactly two {pdf, model_mm} pairs.");
            return (Similarity.FromPairs(P(list[0].GetProperty("pdf")), P(list[0].GetProperty("model_mm")), P(list[1].GetProperty("pdf")), P(list[1].GetProperty("model_mm"))), 0, 2);
        }
        if (c.TryGetProperty("scale", out var sc))
            return (Similarity.FromScale(sc.GetDouble(), c.TryGetProperty("pdf_origin", out var po) ? P(po) : new(0, 0),
                c.TryGetProperty("model_origin_mm", out var mo) ? P(mo) : new(0, 0), c.TryGetProperty("rotation_deg", out var rd) ? rd.GetDouble() : 0), null, 0);
        if (c.TryGetProperty("by_model_grids", out var bg) && bg.ValueKind == JsonValueKind.True)
        {
            try { var (t, rms, n) = PdfDrawing.FromGridMatches(pdfGrids, modelGrids); return (t, Math.Round(rms, 1), n); }
            catch (ArgumentException ex) { throw new ToolInputException(ex.Message + $" Model grids: {string.Join(", ", modelGrids.Keys.Take(40))}."); }
        }
        throw new ToolInputException("calibration needs points, scale or by_model_grids.");
    }
}
