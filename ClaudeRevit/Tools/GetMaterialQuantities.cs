using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class GetMaterialQuantities : IRevitTool
{
    public string Name => "get_material_quantities";
    public string Description => "Material takeoff for element_ids, selection, categories or all model instances. Revit material volume in m3, base surface area and painted area in m2 reported separately. Paging is explicit: quantities are totals for THIS PAGE only; accumulate pages for a full-model total. No estimates for elements without material quantities.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["scope"] = NativeToolUtil.Field("string", "selection (default), ids, categories, all"),
        ["element_ids"] = NativeToolUtil.Array("integer", "Explicit elements."),
        ["categories"] = NativeToolUtil.Array("string", "BuiltInCategory names, e.g. OST_Walls."),
        ["offset"] = NativeToolUtil.Field("integer", "Page offset; default 0."),
        ["limit"] = NativeToolUtil.Field("integer", "Page size 1..5000; default 1000.")
    });
    private sealed class Total
    {
        public double Volume, BaseArea, PaintArea;
        public HashSet<long> Elements = new();
    }
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app);
        var scope = NativeToolUtil.Text(input, "scope", input.ContainsKey("element_ids") ? "ids" : "selection");
        IEnumerable<ElementId> selected = scope switch
        {
            "ids" => NativeToolUtil.Ids(ToolInput.Required(input, "element_ids")),
            "selection" => ToolContext.UiDocument(app).Selection.GetElementIds(),
            "all" => new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds(),
            "categories" => new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new ElementMulticategoryFilter(
                ToolInput.RequiredArray(input, "categories").EnumerateArray().Select(c => Enum.TryParse<BuiltInCategory>(c.GetString(), out var bic) && bic != BuiltInCategory.INVALID ? bic : throw new ToolInputException("Unknown category: " + c)).ToList())).ToElementIds(),
            _ => throw new ToolInputException("Unknown scope.")
        };
        var ids = selected.Distinct().OrderBy(id => id.Value).ToList();
        var offset = ToolInput.OptionalInt(input, "offset") ?? 0;
        var limit = ToolInput.OptionalInt(input, "limit") ?? 1000;
        if (offset < 0 || limit is < 1 or > 5000) throw new ToolInputException("offset >= 0; limit 1..5000.");
        var page = ids.Skip(offset).Take(limit).ToList();
        var totals = new Dictionary<long, Total>();
        var warnings = new List<object>();
        var noQuantities = 0;
        foreach (var id in page)
        {
            ToolContext.ThrowIfCancelled();
            var element = doc.GetElement(id);
            if (element == null) { warnings.Add(new { element_id = id.Value, error = "missing element" }); continue; }
            try
            {
                var baseIds = element.GetMaterialIds(false);
                var paintIds = element.GetMaterialIds(true);
                if (baseIds.Count == 0 && paintIds.Count == 0) noQuantities++;
                // Read a whole element before accumulating: an API error must not leave
                // a partial quantity for an element reported as failed.
                var quantities = baseIds.Union(paintIds).Select(materialId => new
                {
                    Id = materialId.Value,
                    Volume = baseIds.Contains(materialId) ? element.GetMaterialVolume(materialId) : 0,
                    BaseArea = baseIds.Contains(materialId) ? element.GetMaterialArea(materialId, false) : 0,
                    PaintArea = paintIds.Contains(materialId) ? element.GetMaterialArea(materialId, true) : 0
                }).ToArray();
                foreach (var quantity in quantities)
                {
                    if (!totals.TryGetValue(quantity.Id, out var t)) totals[quantity.Id] = t = new();
                    t.Volume += quantity.Volume; t.BaseArea += quantity.BaseArea; t.PaintArea += quantity.PaintArea; t.Elements.Add(id.Value);
                }
            }
            catch (Exception ex) { warnings.Add(new { element_id = id.Value, error = ex.Message }); }
        }
        return Services.Json.Serialize(new { scope, offset, page_count = page.Count, total_candidates = ids.Count, totals_scope = "page",
            next_offset = offset + page.Count < ids.Count ? (int?)(offset + page.Count) : null, no_material_quantities = noQuantities,
            materials = totals.OrderBy(x => x.Key).Select(x => new { material_id = x.Key, name = doc.GetElement(new ElementId(x.Key))?.Name,
                volume_m3 = x.Value.Volume * Math.Pow(0.3048, 3), base_area_m2 = x.Value.BaseArea * 0.3048 * 0.3048,
                paint_area_m2 = x.Value.PaintArea * 0.3048 * 0.3048, element_count = x.Value.Elements.Count,
                example_element_ids = x.Value.Elements.Order().Take(20) }), warnings });
    }
}
