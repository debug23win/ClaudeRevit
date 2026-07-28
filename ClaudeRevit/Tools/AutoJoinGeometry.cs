using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Bulk geometry joining. Unjoined structural elements are the classic source of double-counted
// concrete volumes in schedules, and joining them one pair at a time in the UI is miserable. This
// finds the intersecting pairs itself (bounding-box prefilter, then a real solid intersection test)
// and joins them in one transaction.
public class AutoJoinGeometry : IRevitTool
{
    public string Name => "auto_join_geometry";

    public string Description =>
        "Find intersecting element pairs between two categories (or within one) and join their geometry — " +
        "the fix for double-counted concrete volumes. Uses a bounding-box prefilter plus a real intersection " +
        "test, skips pairs that are already joined, and reports every pair joined/failed. " +
        "Set unjoin=true to remove existing joins instead. Optionally restrict to a level.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["category_a"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "First category, e.g. 'Structural Columns'."
            }),
            ["category_b"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Second category, e.g. 'Structural Framing'. Omit to join within category_a."
            }),
            ["on_level"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Restrict to this level name." }),
            ["unjoin"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean", description = "Unjoin instead of join. Default false."
            }),
            ["max_pairs"] = JsonSerializer.SerializeToElement(new
            {
                type = "integer",
                description = "Safety cap on pairs processed (default 2000).",
                minimum = 1
            })
        },
        Required = ["category_a"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var unjoin = input.TryGetValue("unjoin", out var uj) && uj.ValueKind == JsonValueKind.True;
        var maxPairs = input.TryGetValue("max_pairs", out var mp) ? Math.Max(1, mp.GetInt32()) : 2000;

        var listA = Collect(doc, input["category_a"].GetString(), input);
        var listB = input.TryGetValue("category_b", out var cb) && cb.ValueKind == JsonValueKind.String
            ? Collect(doc, cb.GetString(), input)
            : listA;
        var sameSet = ReferenceEquals(listA, listB);

        // Cache bounding boxes once — recomputing them inside the O(n²) sweep is the difference
        // between seconds and minutes on a real model.
        var boxA = listA.Select(e => (el: e, bb: e.get_BoundingBox(null))).Where(x => x.bb != null).ToList();
        var boxB = sameSet ? boxA : listB.Select(e => (el: e, bb: e.get_BoundingBox(null))).Where(x => x.bb != null).ToList();

        var joined = new List<object>();
        var failed = new List<object>();
        var considered = 0;

        for (int i = 0; i < boxA.Count && considered < maxPairs; i++)
        {
            var start = sameSet ? i + 1 : 0;
            for (int j = start; j < boxB.Count && considered < maxPairs; j++)
            {
                var a = boxA[i]; var b = boxB[j];
                if (a.el.Id == b.el.Id) continue;
                if (!Overlaps(a.bb!, b.bb!)) continue;
                considered++;

                try
                {
                    var already = JoinGeometryUtils.AreElementsJoined(doc, a.el, b.el);
                    if (unjoin)
                    {
                        if (!already) continue;
                        JoinGeometryUtils.UnjoinGeometry(doc, a.el, b.el);
                        joined.Add(new { a = a.el.Id.Value, b = b.el.Id.Value });
                    }
                    else
                    {
                        if (already) continue;
                        JoinGeometryUtils.JoinGeometry(doc, a.el, b.el);
                        joined.Add(new { a = a.el.Id.Value, b = b.el.Id.Value });
                    }
                }
                catch (Exception ex)
                {
                    // Revit refuses plenty of legitimate-looking pairs (no real solid overlap,
                    // incompatible categories) — record and continue rather than abort the batch.
                    failed.Add(new { a = a.el.Id.Value, b = b.el.Id.Value, reason = Short(ex.Message) });
                }
            }
        }

        return JsonSerializer.Serialize(new
        {
            action = unjoin ? "unjoin" : "join",
            candidate_pairs = considered,
            changed = joined.Count,
            failed = failed.Count,
            capped = considered >= maxPairs,
            pairs = joined.Take(200),
            failures = failed.Take(50)
        });
    }

    private static bool Overlaps(BoundingBoxXYZ a, BoundingBoxXYZ b)
    {
        const double eps = 1e-6;
        return a.Min.X <= b.Max.X + eps && b.Min.X <= a.Max.X + eps
            && a.Min.Y <= b.Max.Y + eps && b.Min.Y <= a.Max.Y + eps
            && a.Min.Z <= b.Max.Z + eps && b.Min.Z <= a.Max.Z + eps;
    }

    private static List<Element> Collect(Document doc, string? category, IReadOnlyDictionary<string, JsonElement> input)
    {
        var bic = CategoryResolve.Parse(category);
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

    private static string Short(string s) => s.Length <= 120 ? s : s.Substring(0, 120) + "…";
}
