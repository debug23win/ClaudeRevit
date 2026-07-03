using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// create_rebar covers a linear SET (equal spacing along one vector); this covers the other
// common case — several independent single bars at arbitrary positions in a section (e.g.
// corner bars of a column cage) — in ONE call instead of N create_rebar calls.
public class CreateRebarBatch : IRevitTool
{
    public string Name => "create_rebar_batch";

    public string Description =>
        "Creates several independent straight single rebars in one structural host with one " +
        "call — for bars at arbitrary positions (e.g. corner bars of a column) where a " +
        "fixed-spacing set from create_rebar does not fit. Each bar is defined by start/end " +
        "points in feet. Bars that fail are reported individually; the rest are still created. " +
        "Rebar is associative to the host: if the host's size changes, Revit adjusts the bars.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["host_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Element id of the structural host." }),
            ["bar_type_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional rebar bar type name (defaults to the first available)." }),
            ["bars"] = JsonSerializer.SerializeToElement(new
            {
                type = "array",
                minItems = 1,
                description = "Bars to create. Each is { start: {x,y,z}, end: {x,y,z} } in feet.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        start = new
                        {
                            type = "object",
                            properties = new { x = new { type = "number" }, y = new { type = "number" }, z = new { type = "number" } },
                            required = new[] { "x", "y", "z" }
                        },
                        end = new
                        {
                            type = "object",
                            properties = new { x = new { type = "number" }, y = new { type = "number" }, z = new { type = "number" } },
                            required = new[] { "x", "y", "z" }
                        }
                    },
                    required = new[] { "start", "end" }
                }
            })
        },
        Required = ["host_id", "bars"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var host = ReinforcementHelpers.GetValidRebarHost(doc, input);
        var barType = ReinforcementHelpers.ResolveBarType(doc, input);

        var created = new List<object>();
        var failed = new List<object>();
        int index = 0;
        foreach (var barEl in input["bars"].EnumerateArray())
        {
            try
            {
                var s = barEl.GetProperty("start");
                var e = barEl.GetProperty("end");
                var start = new XYZ(s.GetProperty("x").GetDouble(), s.GetProperty("y").GetDouble(), s.GetProperty("z").GetDouble());
                var end = new XYZ(e.GetProperty("x").GetDouble(), e.GetProperty("y").GetDouble(), e.GetProperty("z").GetDouble());
                if (start.DistanceTo(end) < 0.01)
                    throw new InvalidOperationException("start and end are (nearly) identical.");

                var dir = (end - start).Normalize();
                var norm = ReinforcementHelpers.PerpendicularTo(dir);

                var curves = new List<Curve> { Line.CreateBound(start, end) };
                using var terminations = new BarTerminationsData(doc);
                var rebar = Rebar.CreateFromCurves(
                    doc, RebarStyle.Standard, barType, host, norm, curves,
                    terminations, useExistingShapeIfPossible: true, createNewShape: true);

                created.Add(new { index, id = rebar.Id.Value, length_ft = Math.Round(start.DistanceTo(end), 3) });
            }
            catch (Exception ex)
            {
                failed.Add(new { index, error = ex.Message });
            }
            index++;
        }

        if (created.Count > 0)
        {
            // Same guard as create_rebar: surface invalid geometry inside the transaction
            // (→ rollback) instead of corrupting the model.
            try
            {
                doc.Regenerate();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Revit rejected the created rebar during regeneration (all bars rolled back): " + ex.Message);
            }
        }

        return JsonSerializer.Serialize(new
        {
            host_id = host.Id.Value,
            bar_type = barType.Name,
            created_count = created.Count,
            failed_count = failed.Count,
            created,
            failed
        });
    }
}
