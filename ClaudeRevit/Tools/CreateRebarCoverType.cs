using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class CreateRebarCoverType : IRevitTool
{
    public string Name => "create_rebar_cover_type";

    public string Description =>
        "Creates a rebar cover type (concrete clear cover setting) with the given name and " +
        "cover distance. Use list_rebar_cover_types first to check what already exists, and " +
        "set_rebar_cover to assign covers to a host element's faces.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Name for the new cover type." }),
            ["cover_mm"] = JsonSerializer.SerializeToElement(new { type = "number", minimum = 0, description = "Clear cover distance in millimetres." })
        },
        Required = ["name", "cover_mm"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var name = input["name"].GetString();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("name is empty.");
        var coverMm = input["cover_mm"].GetDouble();
        if (coverMm < 0)
            throw new InvalidOperationException("cover_mm must be non-negative.");

        var existing = new FilteredElementCollector(doc).OfClass(typeof(RebarCoverType))
            .Cast<RebarCoverType>().ToList();
        if (existing.Any(t => t.Name == name))
            throw new InvalidOperationException(
                $"A rebar cover type named '{name}' already exists. Existing types: " +
                string.Join(", ", existing.Select(t => $"'{t.Name}' ({Math.Round(t.CoverDistance * Units.MmPerFoot, 1)} mm)")));

        var created = RebarCoverType.Create(doc, name, coverMm / Units.MmPerFoot);

        return JsonSerializer.Serialize(new
        {
            id = created.Id.Value,
            type = "RebarCoverType",
            name = created.Name,
            cover_mm = Math.Round(created.CoverDistance * Units.MmPerFoot, 1)
        });
    }
}
