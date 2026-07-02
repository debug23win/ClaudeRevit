using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class ListRebarCoverTypes : IRevitTool
{
    public string Name => "list_rebar_cover_types";

    public string Description =>
        "Lists rebar cover types (concrete clear cover settings) in the project: id, name and " +
        "cover distance. Use before set_rebar_cover / create_rebar_cover_type.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>(),
        Required = []
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var types = new FilteredElementCollector(doc).OfClass(typeof(RebarCoverType))
            .Cast<RebarCoverType>()
            .Select(t => new
            {
                id = t.Id.Value,
                name = t.Name,
                cover_mm = Math.Round(t.CoverDistance * 304.8, 1)
            })
            .OrderBy(t => t.cover_mm)
            .ToList();

        return JsonSerializer.Serialize(new { count = types.Count, cover_types = types });
    }
}
