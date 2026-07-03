using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class SetRebarCover : IRevitTool
{
    public string Name => "set_rebar_cover";

    public string Description =>
        "Sets the concrete clear cover on a structural host element (wall, floor, beam, column, " +
        "foundation), per face group: top_mm (walls: exterior), bottom_mm (walls: interior) and " +
        "other_mm (edges / all remaining faces; the only one framing and columns have). Provide " +
        "at least one value in millimetres. A matching rebar cover type is reused if one exists, " +
        "otherwise it is created automatically.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["host_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Element id of the structural host." }),
            ["top_mm"] = JsonSerializer.SerializeToElement(new { type = "number", minimum = 0, description = "Cover for the top face (floors) / exterior face (walls), in mm." }),
            ["bottom_mm"] = JsonSerializer.SerializeToElement(new { type = "number", minimum = 0, description = "Cover for the bottom face (floors) / interior face (walls), in mm." }),
            ["other_mm"] = JsonSerializer.SerializeToElement(new { type = "number", minimum = 0, description = "Cover for the remaining faces (edges); for beams/columns this is the single cover value, in mm." })
        },
        Required = ["host_id"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var host = ReinforcementHelpers.GetValidRebarHost(doc, input);

        double? topMm = ToolInput.OptionalDouble(input, "top_mm");
        double? bottomMm = ToolInput.OptionalDouble(input, "bottom_mm");
        double? otherMm = ToolInput.OptionalDouble(input, "other_mm");
        if (topMm == null && bottomMm == null && otherMm == null)
            throw new InvalidOperationException("Provide at least one of top_mm / bottom_mm / other_mm.");

        // Each face group maps to whichever of its candidate parameters the host actually
        // has (floors: TOP/BOTTOM/OTHER; walls: EXTERIOR/INTERIOR/OTHER; framing/columns:
        // a single CLEAR_COVER).
        var requests = new (string Label, double? Mm, BuiltInParameter[] Candidates)[]
        {
            ("top", topMm, new[] { BuiltInParameter.CLEAR_COVER_TOP, BuiltInParameter.CLEAR_COVER_EXTERIOR }),
            ("bottom", bottomMm, new[] { BuiltInParameter.CLEAR_COVER_BOTTOM, BuiltInParameter.CLEAR_COVER_INTERIOR }),
            ("other", otherMm, new[] { BuiltInParameter.CLEAR_COVER_OTHER, BuiltInParameter.CLEAR_COVER })
        };

        // One collector pass for the whole call: reused for distance matching and name
        // uniqueness, with newly created types appended so repeated values are memoized.
        var coverTypes = new FilteredElementCollector(doc).OfClass(typeof(RebarCoverType))
            .Cast<RebarCoverType>().ToList();

        var applied = new List<object>();
        var skipped = new List<object>();
        foreach (var (label, mm, candidates) in requests)
        {
            if (mm == null) continue;

            Parameter? param = candidates
                .Select(bip => host.get_Parameter(bip))
                .FirstOrDefault(p => p != null);
            if (param == null)
            {
                skipped.Add(new { face = label, reason = "The host has no cover parameter for this face group." });
                continue;
            }
            if (param.IsReadOnly)
            {
                skipped.Add(new { face = label, reason = $"Parameter '{param.Definition.Name}' is read-only." });
                continue;
            }

            var coverType = FindOrCreateCoverType(doc, mm.Value, coverTypes);
            param.Set(coverType.Id);
            applied.Add(new
            {
                face = label,
                parameter = param.Definition.Name,
                cover_type = coverType.Name,
                cover_mm = Math.Round(coverType.CoverDistance * 304.8, 1)
            });
        }

        return JsonSerializer.Serialize(new
        {
            host_id = host.Id.Value,
            applied,
            skipped
        });
    }

    private static RebarCoverType FindOrCreateCoverType(Document doc, double mm, List<RebarCoverType> known)
    {
        var existing = known.FirstOrDefault(t => Math.Abs(t.CoverDistance * 304.8 - mm) < 0.05);
        if (existing != null) return existing;

        var baseName = $"Cover {mm:0.#} mm";
        var name = baseName;
        var taken = known.Select(t => t.Name).ToHashSet();
        for (int i = 2; taken.Contains(name); i++)
            name = $"{baseName} ({i})";
        var created = RebarCoverType.Create(doc, name, mm / 304.8);
        known.Add(created);
        return created;
    }
}
