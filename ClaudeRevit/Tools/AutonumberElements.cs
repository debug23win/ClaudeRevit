using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Spatial auto-numbering: walk elements in reading order and write a running number into a
// parameter (Mark by default). Doing this by hand is one of the most common documentation chores —
// and doing it correctly needs a ROW-AWARE sweep, not a naive sort: elements are banded into rows
// by a Y tolerance, then ordered within each row, so a slightly misaligned grid still numbers
// left-to-right, top-to-bottom the way a human reads a drawing.
public class AutonumberElements : IRevitTool
{
    public string Name => "autonumber_elements";

    public string Description =>
        "Auto-number elements in spatial reading order and write the result into a parameter (default 'Mark'). " +
        "Order: rows banded by a tolerance, then left-to-right within each row (configurable). " +
        "Supports a prefix/suffix, a start value and zero-padding — e.g. 'ST-001', 'ST-002'. " +
        "Pass element_ids, or a category to number every element of that category (optionally on one level).";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["element_ids"] = JsonSerializer.SerializeToElement(new
            {
                type = "array",
                description = "Elements to number. Omit to use `category`.",
                items = new { type = "integer" }
            }),
            ["category"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Number every element of this category instead of an explicit list, e.g. 'Doors'."
            }),
            ["on_level"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "With `category`: restrict to this level name."
            }),
            ["parameter_name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Parameter to write into. Default 'Mark'. Must be a writable text parameter."
            }),
            ["order"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                @enum = new[] { "rows_lr_tb", "rows_lr_bt", "columns_tb_lr", "by_x", "by_y" },
                description = "Sweep order. Default 'rows_lr_tb' (rows top→bottom, left→right within a row)."
            }),
            ["row_tolerance_mm"] = JsonSerializer.SerializeToElement(new
            {
                type = "number",
                description = "How far apart (mm) two elements may be and still count as the same row/column. Default 1000."
            }),
            ["prefix"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Text before the number." }),
            ["suffix"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Text after the number." }),
            ["start"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "First number. Default 1." }),
            ["pad"] = JsonSerializer.SerializeToElement(new
            {
                type = "integer",
                description = "Zero-pad the number to this many digits (e.g. 3 → 001). Default 0 (no padding)."
            })
        },
        Required = []
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var elements = Collect(doc, input);
        if (elements.Count == 0)
            return JsonSerializer.Serialize(new { numbered = 0, note = "No elements matched." });

        var paramName = input.TryGetValue("parameter_name", out var pn) ? pn.GetString() : null;
        if (string.IsNullOrWhiteSpace(paramName)) paramName = "Mark";

        var order = input.TryGetValue("order", out var o) ? o.GetString() ?? "rows_lr_tb" : "rows_lr_tb";
        var tolMm = input.TryGetValue("row_tolerance_mm", out var rt) ? rt.GetDouble() : 1000.0;
        var tolFt = Units.MmToFeet(Math.Max(1.0, tolMm));
        var prefix = input.TryGetValue("prefix", out var pf) ? pf.GetString() ?? "" : "";
        var suffix = input.TryGetValue("suffix", out var sf) ? sf.GetString() ?? "" : "";
        var start = input.TryGetValue("start", out var st) ? st.GetInt32() : 1;
        var pad = input.TryGetValue("pad", out var pd) ? pd.GetInt32() : 0;

        // Elements with no usable location can't be spatially ordered — report them rather than
        // scattering arbitrary numbers through the model.
        var placed = new List<(Element el, XYZ p)>();
        var skipped = new List<long>();
        foreach (var el in elements)
        {
            var p = PointOf(el);
            if (p == null) skipped.Add(el.Id.Value); else placed.Add((el, p));
        }

        var ordered = Sweep(placed, order, tolFt);

        var applied = new List<object>();
        var failed = new List<object>();
        var n = start;
        foreach (var (el, _) in ordered)
        {
            var value = prefix + (pad > 0 ? n.ToString().PadLeft(pad, '0') : n.ToString()) + suffix;
            var p = el.LookupParameter(paramName);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.String)
            {
                failed.Add(new { id = el.Id.Value, reason = p == null ? "parameter not found" : p.IsReadOnly ? "read-only" : "not a text parameter" });
                continue;
            }
            try { p.Set(value); applied.Add(new { id = el.Id.Value, value }); n++; }
            catch (Exception ex) { failed.Add(new { id = el.Id.Value, reason = ex.Message }); }
        }

        return JsonSerializer.Serialize(new
        {
            parameter = paramName,
            order,
            numbered = applied.Count,
            first = applied.Count > 0 ? ((dynamic)applied[0]).value : null,
            last = applied.Count > 0 ? ((dynamic)applied[applied.Count - 1]).value : null,
            no_location = skipped,
            failed,
            elements = applied.Take(200)
        });
    }

    private static List<Element> Collect(Document doc, IReadOnlyDictionary<string, JsonElement> input)
    {
        if (input.TryGetValue("element_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            return ids.EnumerateArray()
                .Select(e => doc.GetElement(new ElementId(e.GetInt64())))
                .Where(e => e != null).ToList()!;

        if (!input.TryGetValue("category", out var cat) || cat.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Provide either element_ids or category.");

        var bic = CategoryResolve.Parse(cat.GetString());
        var q = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToList();

        if (input.TryGetValue("on_level", out var lv) && lv.ValueKind == JsonValueKind.String)
        {
            var lvlName = lv.GetString();
            var lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(x => string.Equals(x.Name, lvlName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Level '{lvlName}' not found.");
            q = q.Where(e => e.LevelId == lvl.Id).ToList();
        }
        return q;
    }

    // A representative point: a placed instance's location point, the midpoint of a curve-driven
    // element, else the bounding-box centre.
    private static XYZ? PointOf(Element el)
    {
        try
        {
            switch (el.Location)
            {
                case LocationPoint lp: return lp.Point;
                case LocationCurve lc: return lc.Curve.Evaluate(0.5, true);
            }
            var bb = el.get_BoundingBox(null);
            return bb != null ? (bb.Min + bb.Max) * 0.5 : null;
        }
        catch { return null; }
    }

    // Band into rows/columns by tolerance, then order within each band. This is what makes the
    // numbering read like a drawing instead of jumping around on tiny coordinate differences.
    private static List<(Element el, XYZ p)> Sweep(List<(Element el, XYZ p)> items, string order, double tol)
    {
        switch (order)
        {
            case "by_x": return items.OrderBy(i => i.p.X).ToList();
            case "by_y": return items.OrderBy(i => i.p.Y).ToList();

            case "columns_tb_lr":
            {
                var bands = Band(items, i => i.p.X, tol);           // columns by X
                return bands.OrderBy(b => b.Key)                     // left → right
                            .SelectMany(b => b.Items.OrderByDescending(i => i.p.Y)) // top → bottom
                            .ToList();
            }
            case "rows_lr_bt":
            {
                var bands = Band(items, i => i.p.Y, tol);
                return bands.OrderBy(b => b.Key)                     // bottom → top
                            .SelectMany(b => b.Items.OrderBy(i => i.p.X))
                            .ToList();
            }
            default: // rows_lr_tb
            {
                var bands = Band(items, i => i.p.Y, tol);
                return bands.OrderByDescending(b => b.Key)           // top → bottom
                            .SelectMany(b => b.Items.OrderBy(i => i.p.X))
                            .ToList();
            }
        }
    }

    private sealed class BandGroup
    {
        public double Key;
        public List<(Element el, XYZ p)> Items = new();
    }

    // Greedy banding on a sorted axis: start a new band whenever the gap to the previous item
    // exceeds the tolerance. The band key is the mean, so ordering is stable for ragged rows.
    private static List<BandGroup> Band(
        List<(Element el, XYZ p)> items, Func<(Element el, XYZ p), double> axis, double tol)
    {
        var sorted = items.OrderBy(axis).ToList();
        var bands = new List<BandGroup>();
        foreach (var it in sorted)
        {
            var v = axis(it);
            var band = bands.Count > 0 && Math.Abs(v - axis(bands[^1].Items[^1])) <= tol ? bands[^1] : null;
            if (band == null) { band = new BandGroup(); bands.Add(band); }
            band.Items.Add(it);
        }
        foreach (var b in bands) b.Key = b.Items.Average(axis);
        return bands;
    }
}
