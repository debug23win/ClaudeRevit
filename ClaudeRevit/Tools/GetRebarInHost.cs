using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class GetRebarInHost : IRevitTool
{
    public string Name => "get_rebar_in_host";

    public string Description =>
        "Lists ALL reinforcement placed in a structural host element: rebar sets (bar type, " +
        "quantity, total length), path reinforcement and area (mesh) reinforcement. " +
        "Use to inspect existing reinforcement before adding or modifying it.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["host_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Element id of the structural host." })
        },
        Required = ["host_id"]
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var host = doc.GetElement(new ElementId(input["host_id"].GetInt64()))
            ?? throw new InvalidOperationException("Host element not found.");

        var hostData = RebarHostData.GetRebarHostData(host);
        if (hostData == null || !hostData.IsValidHost())
            throw new InvalidOperationException(
                $"Element {host.Id.Value} ({host.Category?.Name}) is not a valid rebar host.");

        var rebars = hostData.GetRebarsInHost()
            .Select(r => new
            {
                id = r.Id.Value,
                bar_type = doc.GetElement(r.GetTypeId())?.Name,
                quantity = r.Quantity,
                total_length_ft = Math.Round(r.TotalLength, 2)
            })
            .ToList();

        var paths = hostData.GetPathReinforcementsInHost()
            .Select(p => new
            {
                id = p.Id.Value,
                path_type = p.PathReinforcementType?.Name,
                orientation = p.PrimaryBarOrientation.ToString(),
                number_of_bars = p.get_Parameter(BuiltInParameter.PATH_REIN_NUMBER_OF_BARS)?.AsInteger(),
                spacing_ft = p.get_Parameter(BuiltInParameter.PATH_REIN_SPACING) is { } sp
                    ? Math.Round(sp.AsDouble(), 4) : (double?)null
            })
            .ToList();

        var areas = hostData.GetAreaReinforcementsInHost()
            .Select(a => new
            {
                id = a.Id.Value,
                area_type = doc.GetElement(a.GetTypeId())?.Name
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            host_id = host.Id.Value,
            rebar_sets = rebars.Count,
            total_bars = rebars.Sum(r => r.quantity),
            rebars,
            path_reinforcements = paths,
            area_reinforcements = areas
        });
    }
}
