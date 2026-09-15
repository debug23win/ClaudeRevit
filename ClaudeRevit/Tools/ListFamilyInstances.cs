using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// Lists the placed FamilyInstance elements — family/type, position (mm) and group — in one
// call. This replaces the single most-repeated inspection script ("enumerate all
// FamilyInstances and print their family name, Y and group"), which came up again and again
// while authoring the reinforcement family.
public class ListFamilyInstances : IRevitTool
{
    public string Name => "list_family_instances";

    public string Description =>
        "Lists FamilyInstance elements in the active document — id, family, type, category, location (mm) " +
        "and group membership — in one call, sorted by position (Y then X). Optionally filter by a " +
        "family-name substring (case-insensitive), e.g. 'хомут'. Works in both the Family Editor (to see " +
        "all nested rebar/hoop instances) and projects.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["family_filter"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Only include instances whose family name contains this substring."
            }),
            ["limit"] = JsonSerializer.SerializeToElement(new
            {
                type = "integer", minimum = 1, maximum = 1000,
                description = "Max instances to return (default 300)."
            })
        },
        Required = []
    };

    public bool RequiresTransaction => false;

    private const double FeetToMm = Units.MmPerFoot;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var filter = input.TryGetValue("family_filter", out var f) && f.ValueKind == JsonValueKind.String
            ? f.GetString() : null;
        var limit = Math.Clamp(ToolInput.OptionalInt(input, "limit") ?? 300, 1, 1000);

        var all = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(fi => string.IsNullOrEmpty(filter) ||
                         (fi.Symbol?.Family?.Name?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
            .Select(fi => new { fi, p = (fi.Location as LocationPoint)?.Point })
            .OrderBy(x => x.p == null).ThenBy(x => x.p?.Y).ThenBy(x => x.p?.X)
            .ToList();

        var items = all.Take(limit).Select(x =>
        {
            var fi = x.fi;
            var groupId = fi.GroupId != null && fi.GroupId != ElementId.InvalidElementId
                ? fi.GroupId.Value : (long?)null;
            return new
            {
                id = fi.Id.Value,
                family = fi.Symbol?.Family?.Name,
                type = fi.Symbol?.Name,
                category = fi.Category?.Name,
                location_mm = x.p == null ? null : new[] { x.p.X * FeetToMm, x.p.Y * FeetToMm, x.p.Z * FeetToMm },
                group_id = groupId
            };
        }).ToList();

        return Json.Serialize(new
        {
            total = all.Count,
            returned = items.Count,
            truncated = all.Count > items.Count,
            instances = items
        });
    }
}
