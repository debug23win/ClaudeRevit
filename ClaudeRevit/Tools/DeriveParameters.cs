using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Rule-based parameter fill: compute a value FROM THE MODEL (geometry, level, type, host…) and
// write it into a parameter for a whole set at once — the "parametrisation" chore that otherwise
// needs a one-off script per project (marks from level+type, elevations into a text parameter,
// volumes into a shared parameter, …). One transaction for the whole set.
public class DeriveParameters : IRevitTool
{
    public string Name => "derive_parameters";

    public string Description =>
        "Fill a parameter for many elements from a computed source: geometry (length_mm, area_m2, volume_m3, " +
        "height_mm, elevation_mm, top_elevation_mm), identity (level_name, type_name, family_name, category, " +
        "host_name, host_id), or a TEMPLATE combining them.\n" +
        "template example: \"{level_name}-{type_name}\" — any source name in braces, plus {index} for a running " +
        "number. Use `round` for numeric sources and `factor` to scale (e.g. tonnes).\n" +
        "Target a category (optionally on_level) or an explicit element_ids list. Writes text or numeric " +
        "parameters; numeric targets receive Revit-internal units unless the source is already unit-specific.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["element_ids"] = JsonSerializer.SerializeToElement(new
            {
                type = "array", description = "Explicit elements. Omit to use `category`.", items = new { type = "integer" }
            }),
            ["category"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Target category, e.g. 'Structural Columns'." }),
            ["on_level"] = JsonSerializer.SerializeToElement(new { type = "string", description = "With `category`: restrict to this level." }),
            ["parameter_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Parameter to write into." }),
            ["source"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                @enum = new[] { "length_mm", "area_m2", "volume_m3", "height_mm", "elevation_mm", "top_elevation_mm",
                                "level_name", "type_name", "family_name", "category", "host_name", "host_id", "template" },
                description = "What to compute. Use 'template' together with `template`."
            }),
            ["template"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "With source='template': text with {placeholders}, e.g. \"{level_name}-{type_name}-{index}\"."
            }),
            ["round"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Decimals for numeric sources. Default 1." }),
            ["factor"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Multiply numeric result by this. Default 1." }),
            ["skip_if_filled"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean", description = "Don't overwrite a parameter that already has a value. Default false."
            })
        },
        Required = ["parameter_name", "source"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var elements = Collect(doc, input);
        var paramName = input["parameter_name"].GetString()
            ?? throw new InvalidOperationException("parameter_name is required.");
        var source = input["source"].GetString() ?? "";
        var template = input.TryGetValue("template", out var t) ? t.GetString() ?? "" : "";
        if (source == "template" && string.IsNullOrWhiteSpace(template))
            throw new InvalidOperationException("source='template' requires a `template` string.");

        var decimals = input.TryGetValue("round", out var r) ? r.GetInt32() : 1;
        var factor = input.TryGetValue("factor", out var f) ? f.GetDouble() : 1.0;
        var skipFilled = input.TryGetValue("skip_if_filled", out var sf) && sf.ValueKind == JsonValueKind.True;

        var written = new List<object>();
        var failed = new List<object>();
        var index = 0;

        foreach (var el in elements)
        {
            index++;
            var p = el.LookupParameter(paramName);
            if (p == null) { failed.Add(new { id = el.Id.Value, reason = "parameter not found" }); continue; }
            if (p.IsReadOnly) { failed.Add(new { id = el.Id.Value, reason = "read-only" }); continue; }
            if (skipFilled && HasValue(p)) continue;

            try
            {
                object? value = source == "template"
                    ? (object)Render(template, el, doc, index, decimals, factor)
                    : Compute(source, el, doc, index);

                if (value == null) { failed.Add(new { id = el.Id.Value, reason = "source not available" }); continue; }

                string shown;
                if (value is double d)
                {
                    d = Math.Round(d * factor, decimals);
                    shown = d.ToString(CultureInfo.InvariantCulture);
                    if (p.StorageType == StorageType.Double) p.Set(d);
                    else if (p.StorageType == StorageType.Integer) p.Set((int)Math.Round(d));
                    else if (p.StorageType == StorageType.String) p.Set(shown);
                    else { failed.Add(new { id = el.Id.Value, reason = "incompatible parameter type" }); continue; }
                }
                else
                {
                    shown = value.ToString() ?? "";
                    if (p.StorageType == StorageType.String) p.Set(shown);
                    else if (p.StorageType == StorageType.Integer && int.TryParse(shown, out var iv)) p.Set(iv);
                    else if (p.StorageType == StorageType.Double &&
                             double.TryParse(shown, NumberStyles.Any, CultureInfo.InvariantCulture, out var dv)) p.Set(dv);
                    else { failed.Add(new { id = el.Id.Value, reason = "incompatible parameter type" }); continue; }
                }
                written.Add(new { id = el.Id.Value, value = shown });
            }
            catch (Exception ex) { failed.Add(new { id = el.Id.Value, reason = ex.Message }); }
        }

        return Services.Json.Serialize(new
        {
            parameter = paramName,
            source,
            written = written.Count,
            skipped = elements.Count - written.Count - failed.Count,
            failed,
            sample = written.Take(50)
        });
    }

    private static bool HasValue(Parameter p) => p.StorageType switch
    {
        StorageType.String => !string.IsNullOrWhiteSpace(p.AsString()),
        StorageType.Integer => p.AsInteger() != 0,
        StorageType.Double => Math.Abs(p.AsDouble()) > 1e-9,
        StorageType.ElementId => p.AsElementId() != ElementId.InvalidElementId,
        _ => false
    };

    private static object? Compute(string source, Element el, Document doc, int index)
    {
        switch (source)
        {
            case "length_mm":
                return el.Location is LocationCurve lc ? Units.FeetToMm(lc.Curve.Length) : (object?)null;
            case "height_mm":
            {
                var bb = el.get_BoundingBox(null);
                return bb != null ? Units.FeetToMm(bb.Max.Z - bb.Min.Z) : (object?)null;
            }
            case "elevation_mm":
            {
                if (el is Level lvl) return Units.FeetToMm(lvl.Elevation);
                var bb = el.get_BoundingBox(null);
                return bb != null ? Units.FeetToMm(bb.Min.Z) : (object?)null;
            }
            case "top_elevation_mm":
            {
                var bb = el.get_BoundingBox(null);
                return bb != null ? Units.FeetToMm(bb.Max.Z) : (object?)null;
            }
            case "area_m2":
            {
                var p = el.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED) ?? el.get_Parameter(BuiltInParameter.ROOM_AREA);
                return p != null && p.HasValue && p.StorageType == StorageType.Double
                    ? Units.SqFeetToSqM(p.AsDouble()) : (object?)null;
            }
            case "volume_m3":
            {
                var p = el.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED) ?? el.get_Parameter(BuiltInParameter.ROOM_VOLUME);
                return p != null && p.HasValue && p.StorageType == StorageType.Double
                    ? Units.CuFeetToCuM(p.AsDouble()) : (object?)null;
            }
            case "level_name":
                return el.LevelId != ElementId.InvalidElementId ? doc.GetElement(el.LevelId)?.Name : null;
            case "type_name":
                return doc.GetElement(el.GetTypeId())?.Name;
            case "family_name":
                return (doc.GetElement(el.GetTypeId()) as ElementType)?.FamilyName;
            case "category":
                return el.Category?.Name;
            case "host_name":
                return (el as FamilyInstance)?.Host?.Name;
            case "host_id":
            {
                var h = (el as FamilyInstance)?.Host;
                return h != null ? h.Id.Value.ToString() : null;
            }
            case "index":
                return index.ToString();
        }
        return null;
    }

    // Substitute every {source} placeholder in the template; unknown or unavailable sources become
    // empty so a partially-resolvable template still produces a usable string.
    private static string Render(string template, Element el, Document doc, int index, int decimals, double factor)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] != '{') { sb.Append(template[i]); continue; }
            var close = template.IndexOf('}', i + 1);
            if (close < 0) { sb.Append(template[i]); continue; }

            var key = template.Substring(i + 1, close - i - 1).Trim();
            i = close;

            if (key == "index") { sb.Append(index); continue; }
            var v = Compute(key, el, doc, index);
            if (v is double d) sb.Append(Math.Round(d * factor, decimals).ToString(CultureInfo.InvariantCulture));
            else if (v != null) sb.Append(v);
        }
        return sb.ToString();
    }

    private static List<Element> Collect(Document doc, IReadOnlyDictionary<string, JsonElement> input)
    {
        if (input.TryGetValue("element_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            return ids.EnumerateArray().Select(e => doc.GetElement(new ElementId(e.GetInt64())))
                      .Where(e => e != null).ToList()!;

        if (!input.TryGetValue("category", out var cat) || cat.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Provide either element_ids or category.");

        var bic = CategoryResolve.Parse(cat.GetString());
        var q = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToList();

        if (input.TryGetValue("on_level", out var lv) && lv.ValueKind == JsonValueKind.String)
        {
            var name = lv.GetString();
            var lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Level '{name}' not found.");
            q = q.Where(e => e.LevelId == lvl.Id).ToList();
        }
        return q;
    }
}
