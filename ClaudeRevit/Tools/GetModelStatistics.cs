using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class GetModelStatistics : IRevitTool
{
    public string Name => "get_model_statistics";

    public string Description =>
        "Returns summary statistics about the model: element counts by major category, " +
        "view/sheet/family counts, warnings count, and file size if saved. Also reports " +
        "changes_since_previous_call — per-category count deltas versus the previous call in " +
        "this Revit session — so unexpected disappearances (e.g. a door lost to a cascade " +
        "delete) are visible immediately.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>(),
        Required = []
    };

    public bool RequiresTransaction => false;

    // Last snapshot per document (session-scoped) — the basis for change reporting.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, int>>
        LastCounts = new();

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        long fileSizeKb = 0;
        if (!string.IsNullOrEmpty(doc.PathName) && File.Exists(doc.PathName))
            fileSizeKb = new FileInfo(doc.PathName).Length / 1024;

        var majorCategories = new (string Name, BuiltInCategory Bic)[]
        {
            ("walls", BuiltInCategory.OST_Walls),
            ("floors", BuiltInCategory.OST_Floors),
            ("roofs", BuiltInCategory.OST_Roofs),
            ("doors", BuiltInCategory.OST_Doors),
            ("windows", BuiltInCategory.OST_Windows),
            ("rooms", BuiltInCategory.OST_Rooms),
            ("columns", BuiltInCategory.OST_Columns),
            ("structural_columns", BuiltInCategory.OST_StructuralColumns),
            ("structural_framing", BuiltInCategory.OST_StructuralFraming),
            ("furniture", BuiltInCategory.OST_Furniture),
            ("grids", BuiltInCategory.OST_Grids),
            ("levels", BuiltInCategory.OST_Levels)
        };

        var counts = majorCategories.ToDictionary(
            mc => mc.Name,
            mc => new FilteredElementCollector(doc)
                .OfCategory(mc.Bic)
                .WhereElementIsNotElementType()
                .GetElementCount());

        var viewCount = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Count(v => !v.IsTemplate);
        var sheetCount = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).GetElementCount();
        var familyCount = new FilteredElementCollector(doc).OfClass(typeof(Family)).GetElementCount();
        var materialCount = new FilteredElementCollector(doc).OfClass(typeof(Material)).GetElementCount();
        var warningCount = doc.GetWarnings().Count;

        var totalElements = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .GetElementCount();

        // Per-category deltas vs. the previous call in this session (null on the first call).
        // The instance hash keeps two successive unsaved documents with the same title from
        // inheriting each other's snapshot.
        var snapshotKey = doc.Title + "|" + doc.PathName + "|" + doc.GetHashCode();
        Dictionary<string, int>? changes = null;
        if (LastCounts.TryGetValue(snapshotKey, out var previous))
        {
            changes = counts
                .Select(kv => (kv.Key, Delta: kv.Value - previous.GetValueOrDefault(kv.Key)))
                .Where(x => x.Delta != 0)
                .ToDictionary(x => x.Key, x => x.Delta);
        }
        LastCounts[snapshotKey] = new Dictionary<string, int>(counts);

        return JsonSerializer.Serialize(new
        {
            document = doc.Title,
            path = string.IsNullOrEmpty(doc.PathName) ? "(unsaved)" : doc.PathName,
            file_size_kb = fileSizeKb,
            total_elements = totalElements,
            elements_by_category = counts,
            changes_since_previous_call = changes,
            views = viewCount,
            sheets = sheetCount,
            families = familyCount,
            materials = materialCount,
            warnings = warningCount
        });
    }
}
