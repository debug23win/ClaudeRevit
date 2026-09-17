using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Mass take-off from real geometry. Steel plates, rebar and concrete all get weighed the same way —
// volume × density — but Revit only gives volume, and the density lives on the material (when it's
// set at all). This resolves density per element from its material, falls back to a supplied or
// standard density, totals by type, and can write the result into a parameter.
public class CalculateWeight : IRevitTool
{
    public string Name => "calculate_weight";

    public string Description =>
        "Weigh elements from their real volume × material density: steel plates/framing, rebar, concrete. " +
        "Density comes from each element's material when available, else `density_kg_m3` (defaults: steel 7850, " +
        "concrete 2500). Rebar is measured from bar length × nominal diameter rather than volume, which Revit " +
        "reports unreliably. Returns per-element and per-type totals in kg and tonnes, and can write each " +
        "element's mass into `write_to_parameter`.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["element_ids"] = JsonSerializer.SerializeToElement(new
            {
                type = "array", description = "Explicit elements. Omit to use `category`.", items = new { type = "integer" }
            }),
            ["category"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Weigh a whole category, e.g. 'Structural Framing', 'Rebar'."
            }),
            ["on_level"] = JsonSerializer.SerializeToElement(new { type = "string", description = "With `category`: restrict to this level." }),
            ["density_kg_m3"] = JsonSerializer.SerializeToElement(new
            {
                type = "number",
                description = "Density to use when the material doesn't define one. Default 7850 (steel), or 2500 if the category is concrete-ish."
            }),
            ["write_to_parameter"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Optional parameter to write each element's mass (kg) into."
            })
        },
        Required = []
    };

    // Only opens a transaction when asked to write results back.
    public bool RequiresTransaction => true;

    private const double SteelDensity = 7850.0;    // kg/m3
    private const double ConcreteDensity = 2500.0;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var elements = Collect(doc, input);
        if (elements.Count == 0)
            return Services.Json.Serialize(new { total_kg = 0.0, note = "No elements matched." });

        var explicitDensity = input.TryGetValue("density_kg_m3", out var dd) ? dd.GetDouble() : (double?)null;
        var writeParam = input.TryGetValue("write_to_parameter", out var wp) ? wp.GetString() : null;

        var rows = new List<(long id, string type, double kg)>();
        var noVolume = new List<long>();

        foreach (var el in elements)
        {
            ToolContext.ThrowIfCancelled();

            double? kg = null;
            try { kg = MassKg(doc, el, explicitDensity); } catch { }
            if (kg == null || kg.Value <= 0) { noVolume.Add(el.Id.Value); continue; }

            var typeName = doc.GetElement(el.GetTypeId())?.Name ?? el.Name ?? "(unknown)";
            rows.Add((el.Id.Value, typeName, kg.Value));

            if (!string.IsNullOrWhiteSpace(writeParam))
            {
                var p = el.LookupParameter(writeParam);
                if (p != null && !p.IsReadOnly)
                {
                    try
                    {
                        if (p.StorageType == StorageType.Double) p.Set(kg.Value);
                        else if (p.StorageType == StorageType.String) p.Set(Math.Round(kg.Value, 2).ToString("0.##"));
                        else if (p.StorageType == StorageType.Integer) p.Set((int)Math.Round(kg.Value));
                    }
                    catch { }
                }
            }
        }

        var byType = rows.GroupBy(r => r.type).Select(g => new
        {
            type = g.Key,
            count = g.Count(),
            kg = Math.Round(g.Sum(x => x.kg), 2),
            tonnes = Math.Round(g.Sum(x => x.kg) / 1000.0, 3)
        }).OrderByDescending(x => x.kg).ToList();

        var total = rows.Sum(r => r.kg);
        return Services.Json.Serialize(new
        {
            elements_weighed = rows.Count,
            total_kg = Math.Round(total, 2),
            total_tonnes = Math.Round(total / 1000.0, 3),
            by_type = byType,
            no_volume = noVolume.Take(50),
            wrote_parameter = writeParam,
            sample = rows.Take(50).Select(r => new { id = r.id, type = r.type, kg = Math.Round(r.kg, 2) })
        });
    }

    private static double? MassKg(Document doc, Element el, double? explicitDensity)
    {
        // Rebar: Revit's volume for a bar is unreliable, so use the honest engineering route —
        // total centreline length × the nominal bar cross-section.
        if (el is Rebar rebar)
        {
            var barType = doc.GetElement(rebar.GetTypeId()) as RebarBarType;
            var diaFt = barType?.BarNominalDiameter ?? 0;
            if (diaFt <= 0) return null;

            var lenParam = rebar.get_Parameter(BuiltInParameter.REBAR_ELEM_TOTAL_LENGTH);
            var totalLenFt = lenParam != null && lenParam.HasValue ? lenParam.AsDouble() : 0;
            if (totalLenFt <= 0) return null;

            var dM = Units.FeetToMm(diaFt) / 1000.0;
            var lM = Units.FeetToMm(totalLenFt) / 1000.0;
            var volM3 = Math.PI * dM * dM / 4.0 * lM;
            return volM3 * (explicitDensity ?? DensityOf(doc, el) ?? SteelDensity);
        }

        var volFt3 = VolumeFt3(el);
        if (volFt3 == null || volFt3 <= 0) return null;
        var density = explicitDensity ?? DensityOf(doc, el) ?? DefaultDensity(el);
        return Units.CuFeetToCuM(volFt3.Value) * density;
    }

    private static double? VolumeFt3(Element el)
    {
        var p = el.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
        if (p != null && p.HasValue && p.StorageType == StorageType.Double) return p.AsDouble();

        // Fall back to summing the solids of the actual geometry (covers loadable families, plates).
        try
        {
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            var geo = el.get_Geometry(opt);
            if (geo == null) return null;
            double vol = 0;
            foreach (var g in geo) vol += SolidVolume(g);
            return vol > 0 ? vol : null;
        }
        catch { return null; }
    }

    private static double SolidVolume(GeometryObject g)
    {
        switch (g)
        {
            case Solid s when s.Volume > 0: return s.Volume;
            case GeometryInstance gi:
            {
                double v = 0;
                foreach (var sub in gi.GetInstanceGeometry()) v += SolidVolume(sub);
                return v;
            }
            default: return 0;
        }
    }

    // Density from the element's first material that actually declares a structural density.
    private static double? DensityOf(Document doc, Element el)
    {
        try
        {
            foreach (var mid in el.GetMaterialIds(false))
            {
                if (doc.GetElement(mid) is not Material m) continue;
                var sa = doc.GetElement(m.StructuralAssetId) as PropertySetElement;
                var asset = sa?.GetStructuralAsset();
                if (asset == null) continue;
                // Revit stores density internally as mass per cubic foot; convert to kg/m3.
                if (asset.Density > 0)
                {
                    var kg = DensityToKgM3(asset.Density);
                    if (kg != null) return kg;
                }
            }
        }
        catch { }
        return null;
    }

    // Revit's internal structural density unit is pounds-mass per cubic foot in the API's
    // unit-less double; convert to kg/m3. Guarded so an unexpected magnitude never silently
    // produces a nonsense tonnage.
    private static double? DensityToKgM3(double revitDensity)
    {
        const double LbPerFt3ToKgPerM3 = 16.0184634;
        var kg = revitDensity * LbPerFt3ToKgPerM3;
        return kg > 100 && kg < 25000 ? kg : (double?)null;
    }

    private static double DefaultDensity(Element el)
    {
        var name = ((el.Category?.Name ?? "") + " " + (el.Name ?? "")).ToLowerInvariant();
        return name.Contains("concrete") || name.Contains("бетон") || name.Contains("foundation") ||
               name.Contains("floor") || name.Contains("wall")
            ? ConcreteDensity : SteelDensity;
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
