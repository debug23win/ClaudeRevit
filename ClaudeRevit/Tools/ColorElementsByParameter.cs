using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

public sealed class ColorElementsByParameter : IRevitTool
{
    public string Name => "color_elements_by_parameter";
    public string Description => "Color selected or explicit elements by a parameter, addressed by shared GUID or exact name. categorical gives one color per raw value; gradient interpolates numeric raw values. Returns a legend of values, colors, counts and IDs; preserves unrelated view overrides. Preview defaults true; false applies.";
    public bool RequiresTransaction => false;
    public bool RequiresNoTurnGroup => true;
    public bool MutatesWithoutTransaction => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["element_ids"] = NativeToolUtil.Array("integer", "Elements; default current selection; max 10000."),
        ["view_id"] = NativeToolUtil.Field("integer", "View; default active."),
        ["parameter_name"] = NativeToolUtil.Field("string", "Exact name, or provide GUID."),
        ["parameter_guid"] = NativeToolUtil.Field("string", "Shared GUID takes precedence."),
        ["type_fallback"] = NativeToolUtil.Field("boolean", "Read type if instance lacks parameter; default false."),
        ["mode"] = NativeToolUtil.Field("string", "categorical (default) or gradient."),
        ["palette"] = NativeToolUtil.Array("string", "Categorical colors #RRGGBB."),
        ["low_color"] = NativeToolUtil.Field("string", "Gradient minimum, default #2166AC."),
        ["high_color"] = NativeToolUtil.Field("string", "Gradient maximum, default #B2182B."),
        ["minimum"] = NativeToolUtil.Field("number", "Raw/internal gradient minimum; default observed."),
        ["maximum"] = NativeToolUtil.Field("number", "Raw/internal gradient maximum; default observed."),
        ["preview"] = NativeToolUtil.Field("boolean", "Default true.")
    });
    private sealed record Row(long Id, string Key, string Display, double? Number);
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var view = input.TryGetValue("view_id", out var vi) ? NativeToolUtil.Element(doc, vi.GetInt64()) as View : doc.ActiveView;
        if (view == null || view.IsTemplate || !view.AreGraphicsOverridesAllowed()) throw new ToolInputException("Select a view that allows graphics overrides.");
        var ids = input.TryGetValue("element_ids", out var ei) ? NativeToolUtil.Ids(ei) : app.ActiveUIDocument.Selection.GetElementIds().ToList();
        if (ids.Count is < 1 or > 10000) throw new ToolInputException("Select 1..10000 elements.");
        var rows = new List<Row>(); var missing = new List<long>();
        foreach (var id in ids)
        {
            ToolContext.ThrowIfCancelled();
            var e = NativeToolUtil.Element(doc, id.Value);
            var p = NativeToolUtil.Parameter(e, NativeToolUtil.Text(input, "parameter_name"), NativeToolUtil.Text(input, "parameter_guid"));
            if (p == null && ToolInput.Flag(input, "type_fallback") && doc.GetElement(e.GetTypeId()) is { } type)
                p = NativeToolUtil.Parameter(type, NativeToolUtil.Text(input, "parameter_name"), NativeToolUtil.Text(input, "parameter_guid"));
            if (p == null || !p.HasValue) { missing.Add(id.Value); continue; }
            double? n = p.StorageType switch { StorageType.Double => p.AsDouble(), StorageType.Integer => p.AsInteger(), _ => null };
            var raw = p.StorageType switch { StorageType.String => p.AsString() ?? "", StorageType.ElementId => p.AsElementId().Value.ToString(),
                _ => n?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "" };
            rows.Add(new(id.Value, p.StorageType + ":" + raw, p.AsValueString() ?? raw, n));
        }
        var mode = NativeToolUtil.Text(input, "mode", "categorical");
        if (mode is not ("categorical" or "gradient")) throw new ToolInputException("mode must be categorical or gradient.");
        if (mode == "gradient" && rows.Any(r => r.Number == null)) throw new ToolInputException("Gradient requires numeric parameters on every matched element.");
        var low = Rgb.Parse(NativeToolUtil.Text(input, "low_color", "#2166AC"));
        var high = Rgb.Parse(NativeToolUtil.Text(input, "high_color", "#B2182B"));
        var palette = input.TryGetValue("palette", out var pa) ? pa.EnumerateArray().Select(p => Rgb.Parse(p.GetString() ?? "")).ToArray() : ParameterColors.Palette.Select(Rgb.Parse).ToArray();
        if (palette.Length is < 1 or > 256) throw new ToolInputException("Supply 1..256 palette colors.");
        var min = ToolInput.OptionalDouble(input, "minimum") ?? rows.Select(r => r.Number ?? 0).DefaultIfEmpty().Min();
        var max = ToolInput.OptionalDouble(input, "maximum") ?? rows.Select(r => r.Number ?? 0).DefaultIfEmpty().Max();
        if (mode == "gradient") ParameterColors.Fraction(min, min, max);
        var groups = rows.GroupBy(r => r.Key).OrderBy(g => g.Key, StringComparer.Ordinal).Select((g, index) => new
        {
            value = g.Key, display = g.First().Display, number = g.First().Number,
            color = mode == "gradient" ? Rgb.Interpolate(low, high, ParameterColors.Fraction(g.First().Number!.Value, min, max)) : palette[index % palette.Length],
            ids = g.Select(r => r.Id).ToArray()
        }).ToList();
        var fill = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(f => f.GetFillPattern().IsSolidFill)
            ?? throw new InvalidOperationException("No solid fill pattern exists.");
        var preview = NativeToolUtil.Preview(input);
        var (_, warnings) = NativeToolUtil.Commit(doc, "Claude: parameter colors", preview, () =>
        {
            foreach (var group in groups)
            foreach (var id in group.ids)
            {
                ToolContext.ThrowIfCancelled();
                var c = new Color(group.color.R, group.color.G, group.color.B);
                using var settings = view.GetElementOverrides(new ElementId(id));
                settings.SetProjectionLineColor(c).SetCutLineColor(c).SetSurfaceForegroundPatternId(fill.Id).SetSurfaceForegroundPatternColor(c)
                    .SetSurfaceForegroundPatternVisible(true).SetCutForegroundPatternId(fill.Id).SetCutForegroundPatternColor(c).SetCutForegroundPatternVisible(true);
                view.SetElementOverrides(new ElementId(id), settings);
            }
            return true;
        });
        return Json.Serialize(new { preview, view_id = view.Id.Value, mode, minimum = min, maximum = max, missing_parameter_ids = missing,
            palette_reused = mode == "categorical" && groups.Count > palette.Length,
            legend = groups.Select(g => new { g.value, g.display, raw_numeric = g.number, color = g.color.Hex, count = g.ids.Length, element_ids = g.ids }), warnings });
    }
}
