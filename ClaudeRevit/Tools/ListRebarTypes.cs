using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class ListRebarTypes : IRevitTool
{
    public string Name => "list_rebar_types";

    public string Description =>
        "Lists available rebar bar types (with nominal diameters), rebar shapes and hook types. " +
        "Call this before create_rebar or create_area_reinforcement to get exact type names.";

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

        var barTypes = new FilteredElementCollector(doc)
            .OfClass(typeof(RebarBarType))
            .Cast<RebarBarType>()
            .Select(t => new
            {
                name = t.Name,
                diameter_mm = Math.Round(t.BarNominalDiameter * 304.8, 1)
            })
            .OrderBy(t => t.diameter_mm)
            .ToList();

        var shapes = new FilteredElementCollector(doc)
            .OfClass(typeof(RebarShape))
            .Cast<RebarShape>()
            .Select(s => s.Name)
            .OrderBy(n => n)
            .ToList();

        var hooks = new FilteredElementCollector(doc)
            .OfClass(typeof(RebarHookType))
            .Cast<RebarHookType>()
            .Select(h => h.Name)
            .OrderBy(n => n)
            .ToList();

        var areaTypes = new FilteredElementCollector(doc)
            .OfClass(typeof(AreaReinforcementType))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        return JsonSerializer.Serialize(new
        {
            bar_types = barTypes,
            shapes,
            hook_types = hooks,
            area_reinforcement_types = areaTypes
        });
    }
}
