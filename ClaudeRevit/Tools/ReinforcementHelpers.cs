using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace ClaudeRevit.Tools;

// Shared plumbing for the rebar tools (create_rebar, create_rebar_batch,
// create_area_reinforcement, create_path_reinforcement, set_rebar_cover), so type
// matching, host validation and the distribution geometry cannot drift between them.
internal static class ReinforcementHelpers
{
    // Resolves the optional bar_type_name input, defaulting to the first available type.
    public static RebarBarType ResolveBarType(Document doc, IReadOnlyDictionary<string, JsonElement> input)
    {
        var types = new FilteredElementCollector(doc)
            .OfClass(typeof(RebarBarType)).Cast<RebarBarType>();

        if (input.TryGetValue("bar_type_name", out var bt) && bt.ValueKind == JsonValueKind.String)
        {
            var wanted = bt.GetString();
            return types.FirstOrDefault(t => t.Name == wanted)
                ?? throw new InvalidOperationException(
                    $"Rebar bar type '{wanted}' not found. Call list_rebar_types to see available types.");
        }

        return types.FirstOrDefault()
            ?? throw new InvalidOperationException("No rebar bar types are loaded in this project.");
    }

    // Resolves host_id and validates the element can host rebar.
    public static Element GetValidRebarHost(Document doc, IReadOnlyDictionary<string, JsonElement> input)
    {
        var host = doc.GetElement(new ElementId(input["host_id"].GetInt64()))
            ?? throw new InvalidOperationException("Host element not found.");

        var hostData = RebarHostData.GetRebarHostData(host);
        if (hostData == null || !hostData.IsValidHost())
            throw new InvalidOperationException(
                $"Element {host.Id.Value} ({host.Category?.Name}) is not a valid rebar host. " +
                "The element must be structural (e.g. structural wall/floor/column/framing/foundation).");

        return host;
    }

    // A stable unit vector perpendicular to dir: vertical bars distribute along X,
    // horizontal bars along Z. Load-bearing geometry — keep the single copy.
    public static XYZ PerpendicularTo(XYZ dir)
    {
        var seed = Math.Abs(dir.DotProduct(XYZ.BasisZ)) > 0.9 ? XYZ.BasisX : XYZ.BasisZ;
        return (seed - dir.Multiply(seed.DotProduct(dir))).Normalize();
    }
}
