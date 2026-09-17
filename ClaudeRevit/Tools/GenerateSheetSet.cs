using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Drawing-set generator: one view + one sheet per level, in one call. Creating a plan, applying the
// template, making a sheet, numbering it and centring the viewport is ~6 clicks per level — times
// every level, twice, for every discipline. This does the whole set and keeps numbering consistent.
public class GenerateSheetSet : IRevitTool
{
    public string Name => "generate_sheet_set";

    public string Description =>
        "Generate a drawing set: for each level, create a view (floor_plan / ceiling_plan / structural_plan / " +
        "area_plan), optionally apply a view template, create a sheet and place the view centred on it. " +
        "Sheet numbers come from a template — {index} (running), {level} — e.g. \"A-1{index}\". " +
        "Skips levels that already have a sheet with the resulting number, so it's safe to re-run. " +
        "Returns every view/sheet created. Use list_view_templates and list_title_blocks to pick inputs.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["view_type"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                @enum = new[] { "floor_plan", "ceiling_plan", "structural_plan", "area_plan" },
                description = "What to create per level. Default 'floor_plan'."
            }),
            ["levels"] = JsonSerializer.SerializeToElement(new
            {
                type = "array", description = "Level names. Omit for every level in the model.", items = new { type = "string" }
            }),
            ["sheet_number_template"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "e.g. \"A-1{index}\" or \"{level}-PLAN\". Default \"A-{index}\"."
            }),
            ["sheet_name_template"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Default \"{level} - {view_type}\"."
            }),
            ["view_template_name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "View template applied to each new view."
            }),
            ["title_block_name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Title block type name. Omit to use the first available."
            }),
            ["start_index"] = JsonSerializer.SerializeToElement(new
            {
                type = "integer", description = "First {index} value. Default 1."
            }),
            ["scale"] = JsonSerializer.SerializeToElement(new
            {
                type = "integer", description = "View scale denominator, e.g. 100 for 1:100."
            })
        },
        Required = []
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var viewType = input.TryGetValue("view_type", out var vt) ? vt.GetString() ?? "floor_plan" : "floor_plan";
        var numTpl = Str(input, "sheet_number_template", "A-{index}");
        var nameTpl = Str(input, "sheet_name_template", "{level} - {view_type}");
        var startIndex = input.TryGetValue("start_index", out var si) ? si.GetInt32() : 1;

        var levels = SelectLevels(doc, input);
        if (levels.Count == 0)
            return Services.Json.Serialize(new { created = 0, note = "No levels matched." });

        var vft = ViewFamilyTypeFor(doc, viewType)
            ?? throw new InvalidOperationException($"No view family type available for '{viewType}'.");

        var titleBlock = TitleBlock(doc, input);
        var template = TemplateView(doc, input);

        // Existing numbers, so a re-run tops up the set instead of failing on duplicates.
        var existingNumbers = new HashSet<string>(
            new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Select(s => s.SheetNumber), StringComparer.OrdinalIgnoreCase);

        var created = new List<object>();
        var skipped = new List<object>();
        var index = startIndex;

        foreach (var lvl in levels)
        {
            var number = Render(numTpl, lvl.Name, viewType, index);
            if (existingNumbers.Contains(number))
            {
                skipped.Add(new { level = lvl.Name, sheet_number = number, reason = "sheet number already exists" });
                index++;
                continue;
            }

            try
            {
                var view = ViewPlan.Create(doc, vft.Id, lvl.Id);
                if (template != null) { try { view.ViewTemplateId = template.Id; } catch { } }
                if (input.TryGetValue("scale", out var sc) && sc.ValueKind == JsonValueKind.Number)
                { try { view.Scale = sc.GetInt32(); } catch { } }

                var sheet = ViewSheet.Create(doc, titleBlock?.Id ?? ElementId.InvalidElementId);
                sheet.SheetNumber = number;
                try { sheet.Name = Render(nameTpl, lvl.Name, viewType, index); } catch { }

                // Centre the viewport on the sheet: without an explicit point Revit drops it at the
                // origin, which usually lands off the title block.
                var centre = SheetCentre(doc, sheet);
                Viewport vp;
                if (Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                    vp = Viewport.Create(doc, sheet.Id, view.Id, centre);
                else
                {
                    skipped.Add(new { level = lvl.Name, sheet_number = number, reason = "view can't be placed on a sheet" });
                    index++;
                    continue;
                }

                existingNumbers.Add(number);
                created.Add(new
                {
                    level = lvl.Name,
                    view_id = view.Id.Value,
                    view_name = view.Name,
                    sheet_id = sheet.Id.Value,
                    sheet_number = number,
                    sheet_name = sheet.Name,
                    viewport_id = vp.Id.Value
                });
                index++;
            }
            catch (Exception ex)
            {
                skipped.Add(new { level = lvl.Name, reason = Short(ex.Message) });
                index++;
            }
        }

        return Services.Json.Serialize(new
        {
            view_type = viewType,
            levels_processed = levels.Count,
            created = created.Count,
            skipped,
            sheets = created
        });
    }

    private static XYZ SheetCentre(Document doc, ViewSheet sheet)
    {
        try
        {
            var bb = sheet.get_BoundingBox(null);
            if (bb != null) return (bb.Min + bb.Max) * 0.5;
        }
        catch { }
        // A1 landscape mid-point in feet as a sane fallback.
        return new XYZ(1.36, 0.96, 0);
    }

    private static List<Level> SelectLevels(Document doc, IReadOnlyDictionary<string, JsonElement> input)
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => l.Elevation).ToList();

        if (!input.TryGetValue("levels", out var ls) || ls.ValueKind != JsonValueKind.Array) return all;

        var wanted = ls.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
        return all.Where(l => wanted.Any(w => string.Equals(w, l.Name, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private static ViewFamilyType? ViewFamilyTypeFor(Document doc, string viewType)
    {
        var family = viewType switch
        {
            "ceiling_plan" => ViewFamily.CeilingPlan,
            "structural_plan" => ViewFamily.StructuralPlan,
            "area_plan" => ViewFamily.AreaPlan,
            _ => ViewFamily.FloorPlan
        };
        return new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(v => v.ViewFamily == family);
    }

    private static FamilySymbol? TitleBlock(Document doc, IReadOnlyDictionary<string, JsonElement> input)
    {
        var blocks = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType()
            .Cast<FamilySymbol>().ToList();
        if (blocks.Count == 0) return null;

        if (input.TryGetValue("title_block_name", out var tb) && tb.ValueKind == JsonValueKind.String)
        {
            var want = tb.GetString() ?? "";
            var match = blocks.FirstOrDefault(b =>
                string.Equals(b.Name, want, StringComparison.OrdinalIgnoreCase) ||
                $"{b.FamilyName}: {b.Name}".IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
            if (match != null) return match;
        }
        return blocks[0];
    }

    private static View? TemplateView(Document doc, IReadOnlyDictionary<string, JsonElement> input)
    {
        if (!input.TryGetValue("view_template_name", out var vt) || vt.ValueKind != JsonValueKind.String) return null;
        var name = vt.GetString() ?? "";
        return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .FirstOrDefault(v => v.IsTemplate && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string Render(string template, string level, string viewType, int index)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] != '{') { sb.Append(template[i]); continue; }
            var close = template.IndexOf('}', i + 1);
            if (close < 0) { sb.Append(template[i]); continue; }
            var key = template.Substring(i + 1, close - i - 1).Trim();
            sb.Append(key switch
            {
                "index" => index.ToString(),
                "level" => level,
                "view_type" => viewType.Replace('_', ' '),
                _ => ""
            });
            i = close;
        }
        return sb.ToString();
    }

    private static string Str(IReadOnlyDictionary<string, JsonElement> input, string key, string fallback)
    {
        if (input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s!;
        }
        return fallback;
    }

    private static string Short(string s) => s.Length <= 140 ? s : s.Substring(0, 140) + "…";
}
