using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Bar bending schedule data. A rebar's real value on a drawing is its SHAPE: the ordered leg
// lengths and the bend angles between them, which Revit hides inside the centreline geometry. This
// walks the centreline, measures each straight leg (arcs are reported as bends, not legs) and
// computes the turn angle between consecutive legs — the numbers an engineer puts on a bending
// sketch. Optionally emits an SVG so the shape can actually be looked at.
public class GetRebarShapeSketch : IRevitTool
{
    public string Name => "get_rebar_shape_sketch";

    public string Description =>
        "Bar-bending data for rebar: ordered straight LEG lengths (mm), the BEND angle between consecutive " +
        "legs (degrees), total centreline length, bar diameter, shape name and quantity. This is what a " +
        "bending schedule / bar sketch needs and it is not readable from parameters alone. " +
        "Set include_svg=true to also get a simple 2D sketch of each distinct shape as inline SVG.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["element_ids"] = JsonSerializer.SerializeToElement(new
            {
                type = "array", description = "Rebar element ids. Omit to describe every rebar in the model (capped).",
                items = new { type = "integer" }
            }),
            ["host_id"] = JsonSerializer.SerializeToElement(new
            {
                type = "integer", description = "Describe only the rebar hosted by this element."
            }),
            ["group_by_shape"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean",
                description = "Collapse identical bars into one row per distinct shape+diameter+length with a count. Default true."
            }),
            ["include_svg"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean", description = "Also return an inline SVG sketch per distinct shape. Default false."
            }),
            ["limit"] = JsonSerializer.SerializeToElement(new
            {
                type = "integer", description = "Max bars examined (default 500).", minimum = 1
            })
        },
        Required = []
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var limit = input.TryGetValue("limit", out var l) ? Math.Max(1, l.GetInt32()) : 500;
        var group = !input.TryGetValue("group_by_shape", out var g) || g.ValueKind != JsonValueKind.False;
        var svg = input.TryGetValue("include_svg", out var s) && s.ValueKind == JsonValueKind.True;

        var bars = CollectBars(doc, input, limit);
        if (bars.Count == 0)
            return Services.Json.Serialize(new { bars = 0, note = "No rebar matched." });

        var described = new List<BarInfo>();
        foreach (var r in bars)
        {
            try { described.Add(Describe(doc, r)); } catch { /* skip unreadable bar */ }
        }

        if (!group)
            return Services.Json.Serialize(new
            {
                bars = described.Count,
                rebar = described.Select(b => Row(b, svg))
            });

        // Two bars are "the same" on a schedule when shape, diameter and leg pattern match.
        var groups = described
            .GroupBy(b => $"{b.ShapeName}|{b.DiameterMm:0.#}|{string.Join(",", b.Legs.Select(x => Math.Round(x)))}")
            .Select(gr =>
            {
                var first = gr.First();
                return new
                {
                    shape = first.ShapeName,
                    diameter_mm = Math.Round(first.DiameterMm, 1),
                    legs_mm = first.Legs.Select(x => Math.Round(x, 1)),
                    bend_angles_deg = first.Bends.Select(x => Math.Round(x, 1)),
                    total_length_mm = Math.Round(first.TotalLengthMm, 1),
                    bar_count = gr.Sum(x => x.Quantity),
                    positions = gr.Count(),
                    total_length_m = Math.Round(gr.Sum(x => x.TotalLengthMm * x.Quantity) / 1000.0, 2),
                    element_ids = gr.Select(x => x.Id).Take(50),
                    svg = svg ? SvgFor(first) : null
                };
            })
            .OrderByDescending(x => x.bar_count).ToList();

        return Services.Json.Serialize(new
        {
            bars_examined = described.Count,
            distinct_shapes = groups.Count,
            total_bars = groups.Sum(x => x.bar_count),
            total_length_m = Math.Round(groups.Sum(x => x.total_length_m), 2),
            shapes = groups
        });
    }

    private static object Row(BarInfo b, bool svg) => new
    {
        id = b.Id,
        shape = b.ShapeName,
        diameter_mm = Math.Round(b.DiameterMm, 1),
        quantity = b.Quantity,
        legs_mm = b.Legs.Select(x => Math.Round(x, 1)),
        bend_angles_deg = b.Bends.Select(x => Math.Round(x, 1)),
        total_length_mm = Math.Round(b.TotalLengthMm, 1),
        host_id = b.HostId,
        svg = svg ? SvgFor(b) : null
    };

    private sealed class BarInfo
    {
        public long Id;
        public string ShapeName = "";
        public double DiameterMm;
        public int Quantity = 1;
        public double TotalLengthMm;
        public List<double> Legs = new();
        public List<double> Bends = new();
        public long? HostId;
        public List<XYZ> Points = new();   // planar polyline for the sketch
    }

    private static BarInfo Describe(Document doc, Rebar r)
    {
        var info = new BarInfo { Id = r.Id.Value };

        var barType = doc.GetElement(r.GetTypeId()) as RebarBarType;
        info.DiameterMm = barType != null ? Units.FeetToMm(barType.BarNominalDiameter) : 0;

        try { info.ShapeName = doc.GetElement(r.GetShapeId())?.Name ?? "(free form)"; }
        catch { info.ShapeName = "(free form)"; }

        try { info.Quantity = Math.Max(1, r.NumberOfBarPositions); } catch { info.Quantity = 1; }
        try { info.HostId = r.GetHostId() != ElementId.InvalidElementId ? r.GetHostId().Value : (long?)null; } catch { }

        // Centreline of the FIRST bar in the set — every bar in a set shares the shape.
        var curves = r.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
        double total = 0;
        var pts = new List<XYZ>();

        foreach (var c in curves)
        {
            total += c.Length;
            if (c is Line ln)
            {
                info.Legs.Add(Units.FeetToMm(ln.Length));
                if (pts.Count == 0) pts.Add(ln.GetEndPoint(0));
                pts.Add(ln.GetEndPoint(1));
            }
            else if (c is Arc arc)
            {
                // A bend, not a leg: record its turn angle and keep the polyline continuous.
                var sweep = Math.Abs(arc.GetEndParameter(1) - arc.GetEndParameter(0)) * 180.0 / Math.PI;
                info.Bends.Add(sweep);
                if (pts.Count == 0) pts.Add(arc.GetEndPoint(0));
                pts.Add(arc.GetEndPoint(1));
            }
        }
        info.TotalLengthMm = Units.FeetToMm(total);
        info.Points = pts;

        // When the geometry has no explicit arcs (sharp-cornered polyline), derive the turn angle
        // between consecutive legs so the sketch still carries bend information.
        if (info.Bends.Count == 0 && pts.Count >= 3)
        {
            for (int i = 1; i < pts.Count - 1; i++)
            {
                var v1 = (pts[i] - pts[i - 1]);
                var v2 = (pts[i + 1] - pts[i]);
                if (v1.GetLength() < 1e-9 || v2.GetLength() < 1e-9) continue;
                var ang = v1.Normalize().AngleTo(v2.Normalize()) * 180.0 / Math.PI;
                if (ang > 0.5) info.Bends.Add(ang);
            }
        }
        return info;
    }

    // A flat sketch: project the polyline onto its own best plane so the shape reads correctly
    // regardless of how the bar sits in space, then scale to fit a fixed viewBox.
    private static string? SvgFor(BarInfo b)
    {
        if (b.Points.Count < 2) return null;

        var o = b.Points[0];
        var normal = PlaneNormal(b.Points);
        var xAxis = (b.Points[1] - o);
        if (xAxis.GetLength() < 1e-9) return null;
        xAxis = xAxis.Normalize();
        var yAxis = normal.CrossProduct(xAxis).Normalize();

        var flat = b.Points.Select(p =>
        {
            var v = p - o;
            return (x: v.DotProduct(xAxis), y: v.DotProduct(yAxis));
        }).ToList();

        double minX = flat.Min(p => p.x), maxX = flat.Max(p => p.x);
        double minY = flat.Min(p => p.y), maxY = flat.Max(p => p.y);
        double w = Math.Max(maxX - minX, 1e-6), h = Math.Max(maxY - minY, 1e-6);
        const double box = 200, pad = 12;
        var scale = Math.Min((box - 2 * pad) / w, (box - 2 * pad) / h);

        var pathPts = flat.Select(p =>
        {
            var sx = pad + (p.x - minX) * scale;
            var sy = box - (pad + (p.y - minY) * scale); // SVG y grows downward
            return $"{sx:0.#},{sy:0.#}";
        });

        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {box:0} {box:0}\" width=\"{box:0}\" height=\"{box:0}\">" +
               $"<polyline points=\"{string.Join(" ", pathPts)}\" fill=\"none\" stroke=\"#0b6\" stroke-width=\"3\" " +
               "stroke-linejoin=\"round\" stroke-linecap=\"round\"/></svg>";
    }

    private static XYZ PlaneNormal(List<XYZ> pts)
    {
        for (int i = 1; i < pts.Count - 1; i++)
        {
            var n = (pts[i] - pts[0]).CrossProduct(pts[i + 1] - pts[0]);
            if (n.GetLength() > 1e-9) return n.Normalize();
        }
        return XYZ.BasisZ;
    }

    private static List<Rebar> CollectBars(Document doc, IReadOnlyDictionary<string, JsonElement> input, int limit)
    {
        if (input.TryGetValue("element_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            return ids.EnumerateArray()
                .Select(e => doc.GetElement(new ElementId(e.GetInt64())) as Rebar)
                .Where(r => r != null).Take(limit).ToList()!;

        var all = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>();

        if (input.TryGetValue("host_id", out var h) && h.ValueKind == JsonValueKind.Number)
        {
            var hostId = new ElementId(h.GetInt64());
            all = all.Where(r => { try { return r.GetHostId() == hostId; } catch { return false; } });
        }
        return all.Take(limit).ToList();
    }
}
