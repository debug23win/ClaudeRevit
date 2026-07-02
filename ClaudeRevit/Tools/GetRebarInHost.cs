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
        "Lists all rebar placed in a structural host element: bar type, quantity, total length. " +
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

        return JsonSerializer.Serialize(new
        {
            host_id = host.Id.Value,
            rebar_sets = rebars.Count,
            total_bars = rebars.Sum(r => r.quantity),
            rebars
        });
    }
}
