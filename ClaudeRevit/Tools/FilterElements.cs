using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Smart element filter — the answer to "find all walls taller than 3 m on Level 2, and total their
// length". A structured, UNIT-AWARE query so the model never has to know Revit's internal feet or the
// exact BuiltInParameter enum. Beats a vague natural-language filter: predicates combine with AND/OR,
// pseudo-parameters (length/area/volume/height/elevation) are computed from geometry regardless of how
// they're stored, scoping by level / active view is built in, and an optional aggregate rolls the
// matched set up (count/sum/avg/min/max) in the SAME call.
public class FilterElements : IRevitTool
{
    public string Name => "filter_elements";

    public string Description =>
        "Filter elements of a category by parameter conditions and return the matches (id, name, type, " +
        "level, plus each matched value). Optionally aggregate the matched set in one call.\n" +
        "predicates: array of {parameter, op, value}, combined by `match` ('all'=AND default, 'any'=OR).\n" +
        "ops: eq, ne, gt, lt, gte, lte (numeric), contains, starts_with (text), exists, not_exists.\n" +
        "Pseudo-parameters (unit-aware, computed from geometry — USE THESE for numeric compares): " +
        "'length' (mm), 'height' (mm, bbox Z-extent), 'area' (m2), 'volume' (m3), 'elevation' (mm). " +
        "Also 'name', 'type'/'type_name', 'family', 'level', 'category', or ANY parameter by display " +
        "name (fuzzy, case-insensitive) for text ops. Numeric gt/lt on a normal shared/instance " +
        "parameter works only if it stores an integer; for others use text ops.\n" +
        "Scope with `on_level` (level name) and/or `in_active_view`. `aggregate`: {op, parameter} where " +
        "op is count|sum|avg|min|max (parameter omitted for count).";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["category"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Revit category, e.g. 'Walls', 'Structural Columns', 'Doors', 'Rooms'."
            }),
            ["predicates"] = JsonSerializer.SerializeToElement(new
            {
                type = "array",
                description = "Conditions, e.g. [{\"parameter\":\"length\",\"op\":\"gt\",\"value\":3000}].",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        parameter = new { type = "string" },
                        op = new { type = "string", @enum = new[] { "eq", "ne", "gt", "lt", "gte", "lte", "contains", "starts_with", "exists", "not_exists" } },
                        value = new { description = "String or number (omit for exists/not_exists)." }
                    },
                    required = new[] { "parameter", "op" }
                }
            }),
            ["match"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                @enum = new[] { "all", "any" },
                description = "Combine predicates with AND ('all', default) or OR ('any')."
            }),
            ["on_level"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Only elements hosted on this level (name)."
            }),
            ["in_active_view"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean",
                description = "Only elements shown in the active view."
            }),
            ["aggregate"] = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                description = "Roll up the matched set, e.g. {\"op\":\"sum\",\"parameter\":\"length\"}.",
                properties = new
                {
                    op = new { type = "string", @enum = new[] { "count", "sum", "avg", "min", "max" } },
                    parameter = new { type = "string" }
                },
                required = new[] { "op" }
            }),
            ["limit"] = JsonSerializer.SerializeToElement(new
            {
                type = "integer",
                description = "Max elements listed (default 50, max 500). Aggregates use the full set.",
                minimum = 1,
                maximum = 500
            })
        },
        Required = ["category"]
    };

    public bool RequiresTransaction => false;

    private const double FtToMm = 304.8;
    private const double Ft2ToM2 = 0.09290304;
    private const double Ft3ToM3 = 0.028316846592;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var category = input["category"].GetString()
            ?? throw new InvalidOperationException("category is required.");
        var bic = CategoryResolve.Parse(category);

        var matchAny = input.TryGetValue("match", out var mm) &&
                       string.Equals(mm.GetString(), "any", StringComparison.OrdinalIgnoreCase);
        var limit = input.TryGetValue("limit", out var l) ? l.GetInt32() : 50;
        if (limit < 1 || limit > 500) limit = 50;

        var predicates = ParsePredicates(input);

        // Scope: active view (much cheaper) or the whole document, then the category.
        FilteredElementCollector collector;
        if (input.TryGetValue("in_active_view", out var iav) && iav.ValueKind == JsonValueKind.True &&
            doc.ActiveView != null)
            collector = new FilteredElementCollector(doc, doc.ActiveView.Id);
        else
            collector = new FilteredElementCollector(doc);

        var candidates = collector.OfCategory(bic).WhereElementIsNotElementType();

        ElementId? levelFilter = null;
        if (input.TryGetValue("on_level", out var lv) && lv.ValueKind == JsonValueKind.String)
        {
            var name = lv.GetString();
            levelFilter = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;
            if (levelFilter == null)
                return JsonSerializer.Serialize(new { error = $"Level '{name}' not found." });
        }

        var matched = new List<Element>();
        foreach (var el in candidates)
        {
            try
            {
                if (levelFilter != null && el.LevelId != levelFilter) continue;
                if (Matches(el, doc, predicates, matchAny)) matched.Add(el);
            }
            catch { /* skip elements that throw on a parameter read */ }
        }

        object? aggregate = null;
        if (input.TryGetValue("aggregate", out var agg) && agg.ValueKind == JsonValueKind.Object)
            aggregate = Aggregate(matched, doc, agg);

        var listed = matched.Take(limit).Select(e => new
        {
            id = e.Id.Value,
            name = e.Name,
            type_name = doc.GetElement(e.GetTypeId())?.Name,
            level = e.LevelId != ElementId.InvalidElementId ? doc.GetElement(e.LevelId)?.Name : null
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            category,
            match = matchAny ? "any" : "all",
            total_matched = matched.Count,
            listed = listed.Count,
            truncated = matched.Count > listed.Count,
            aggregate,
            elements = listed
        });
    }

    private sealed record Pred(string Param, string Op, JsonElement? Value);

    private static List<Pred> ParsePredicates(IReadOnlyDictionary<string, JsonElement> input)
    {
        var list = new List<Pred>();
        if (!input.TryGetValue("predicates", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var p in arr.EnumerateArray())
        {
            var param = p.TryGetProperty("parameter", out var pr) ? pr.GetString() : null;
            var op = p.TryGetProperty("op", out var o) ? o.GetString() : null;
            if (string.IsNullOrWhiteSpace(param) || string.IsNullOrWhiteSpace(op)) continue;
            JsonElement? val = p.TryGetProperty("value", out var v) ? v : (JsonElement?)null;
            list.Add(new Pred(param!.Trim(), op!.Trim().ToLowerInvariant(), val));
        }
        return list;
    }

    private static bool Matches(Element el, Document doc, List<Pred> preds, bool any)
    {
        if (preds.Count == 0) return true;
        foreach (var p in preds)
        {
            var ok = Test(el, doc, p);
            if (any && ok) return true;
            if (!any && !ok) return false;
        }
        return !any;
    }

    private static bool Test(Element el, Document doc, Pred p)
    {
        var (num, text) = Resolve(el, doc, p.Param);
        var present = num.HasValue || !string.IsNullOrEmpty(text);
        switch (p.Op)
        {
            case "exists": return present;
            case "not_exists": return !present;
        }
        if (p.Value is not { } val) return false;

        // Numeric comparison when both the element value and the predicate value are numbers.
        if (num.HasValue && TryNumber(val, out var target))
        {
            return p.Op switch
            {
                "eq" => Math.Abs(num.Value - target) < 1e-6,
                "ne" => Math.Abs(num.Value - target) >= 1e-6,
                "gt" => num.Value > target,
                "lt" => num.Value < target,
                "gte" => num.Value >= target,
                "lte" => num.Value <= target,
                _ => false
            };
        }

        // Text comparison otherwise.
        var left = text ?? (num?.ToString(CultureInfo.InvariantCulture) ?? "");
        var right = val.ValueKind == JsonValueKind.String
            ? val.GetString() ?? ""
            : val.ToString();
        return p.Op switch
        {
            "eq" => string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
            "ne" => !string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
            "contains" => left.IndexOf(right, StringComparison.OrdinalIgnoreCase) >= 0,
            "starts_with" => left.StartsWith(right, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    // Returns (numeric-in-friendly-unit, text) for a pseudo- or named parameter. Numeric is only set
    // for well-defined units (mm/m2/m3) and integer parameters, so numeric compares are never
    // ambiguous about feet vs metres.
    private static (double? num, string? text) Resolve(Element el, Document doc, string param)
    {
        switch (param.ToLowerInvariant())
        {
            case "length":
                return (el.Location is LocationCurve lc ? lc.Curve.Length * FtToMm : (double?)null, null);
            case "height":
                var bb = el.get_BoundingBox(null);
                return (bb != null ? (bb.Max.Z - bb.Min.Z) * FtToMm : (double?)null, null);
            case "area":
                return (Ft2(el, BuiltInParameter.HOST_AREA_COMPUTED, BuiltInParameter.ROOM_AREA), null);
            case "volume":
                return (Ft3(el, BuiltInParameter.HOST_VOLUME_COMPUTED, BuiltInParameter.ROOM_VOLUME), null);
            case "elevation":
                if (el is Level lvl) return (lvl.Elevation * FtToMm, null);
                var bbx = el.get_BoundingBox(null);
                return (bbx != null ? bbx.Min.Z * FtToMm : (double?)null, null);
            case "name":
                return (null, el.Name);
            case "category":
                return (null, el.Category?.Name);
            case "level":
                return (null, el.LevelId != ElementId.InvalidElementId ? doc.GetElement(el.LevelId)?.Name : null);
            case "type":
            case "type_name":
                return (null, doc.GetElement(el.GetTypeId())?.Name);
            case "family":
                return (null, (doc.GetElement(el.GetTypeId()) as ElementType)?.FamilyName);
        }

        // Any parameter by (fuzzy) display name — exact first, then case-insensitive contains.
        var pm = el.LookupParameter(param) ?? FuzzyParam(el, param);
        if (pm == null) return (null, null);
        return pm.StorageType switch
        {
            StorageType.Integer => (pm.AsInteger(), pm.AsInteger().ToString(CultureInfo.InvariantCulture)),
            StorageType.String => (null, pm.AsString()),
            StorageType.Double => (null, pm.AsValueString()),
            StorageType.ElementId => (null, pm.AsValueString() ?? pm.AsElementId().Value.ToString()),
            _ => (null, pm.AsValueString())
        };
    }

    private static Parameter? FuzzyParam(Element el, string name) =>
        el.Parameters.Cast<Parameter>().FirstOrDefault(p =>
            (p.Definition?.Name ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);

    private static double? Ft2(Element el, params BuiltInParameter[] bips)
    {
        foreach (var bip in bips)
        {
            var p = el.get_Parameter(bip);
            if (p != null && p.HasValue && p.StorageType == StorageType.Double) return p.AsDouble() * Ft2ToM2;
        }
        return null;
    }

    private static double? Ft3(Element el, params BuiltInParameter[] bips)
    {
        foreach (var bip in bips)
        {
            var p = el.get_Parameter(bip);
            if (p != null && p.HasValue && p.StorageType == StorageType.Double) return p.AsDouble() * Ft3ToM3;
        }
        return null;
    }

    private static bool TryNumber(JsonElement v, out double d)
    {
        if (v.ValueKind == JsonValueKind.Number) { d = v.GetDouble(); return true; }
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return true;
        d = 0; return false;
    }

    private static object Aggregate(List<Element> matched, Document doc, JsonElement agg)
    {
        var op = (agg.TryGetProperty("op", out var o) ? o.GetString() : "count")?.ToLowerInvariant() ?? "count";
        if (op == "count") return new { op, value = matched.Count };

        var param = agg.TryGetProperty("parameter", out var pr) ? pr.GetString() : null;
        if (string.IsNullOrWhiteSpace(param))
            return new { op, error = "parameter is required for sum/avg/min/max." };

        var vals = matched
            .Select(e => { try { return Resolve(e, doc, param!).num; } catch { return null; } })
            .Where(x => x.HasValue).Select(x => x!.Value).ToList();
        if (vals.Count == 0) return new { op, parameter = param, value = (double?)null, note = "no numeric values" };

        double result = op switch
        {
            "sum" => vals.Sum(),
            "avg" => vals.Average(),
            "min" => vals.Min(),
            "max" => vals.Max(),
            _ => double.NaN
        };
        return new { op, parameter = param, value = Math.Round(result, 3), sampled = vals.Count };
    }
}
