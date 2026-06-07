using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class SetElementPhases : IRevitTool
{
    public string Name => "set_element_phases";

    public string Description =>
        "Sets the 'Phase Created' and/or 'Phase Demolished' parameters on one or more elements. " +
        "Pass null/omit phase_demolished_id to leave it untouched; pass -1 to set demolished to 'None'. " +
        "Use get_phases to find phase ids.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["element_ids"] = JsonSerializer.SerializeToElement(new
            {
                type = "array", minItems = 1,
                description = "Elements to update.",
                items = new { type = "integer" }
            }),
            ["phase_created_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Phase id to set as Created." }),
            ["phase_demolished_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Phase id to set as Demolished. Pass -1 for 'None'." })
        },
        Required = ["element_ids"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var ids = input["element_ids"].EnumerateArray()
            .Select(e => new ElementId(e.GetInt64())).ToList();

        ElementId? phaseCreatedId = null;
        ElementId? phaseDemolishedId = null;
        if (input.TryGetValue("phase_created_id", out var pc) && pc.ValueKind == JsonValueKind.Number)
            phaseCreatedId = new ElementId(pc.GetInt64());
        if (input.TryGetValue("phase_demolished_id", out var pd) && pd.ValueKind == JsonValueKind.Number)
        {
            var v = pd.GetInt64();
            phaseDemolishedId = v < 0 ? ElementId.InvalidElementId : new ElementId(v);
        }

        if (phaseCreatedId == null && phaseDemolishedId == null)
            throw new InvalidOperationException("Provide at least one of phase_created_id or phase_demolished_id.");

        var updated = new List<long>();
        var skipped = new List<object>();

        foreach (var id in ids)
        {
            var el = doc.GetElement(id);
            if (el == null) { skipped.Add(new { id = id.Value, reason = "not found" }); continue; }
            try
            {
                if (phaseCreatedId != null)
                    el.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(phaseCreatedId);
                if (phaseDemolishedId != null)
                    el.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.Set(phaseDemolishedId);
                updated.Add(id.Value);
            }
            catch (Exception ex) { skipped.Add(new { id = id.Value, reason = ex.Message }); }
        }

        return JsonSerializer.Serialize(new
        {
            updated_count = updated.Count,
            skipped_count = skipped.Count,
            skipped
        });
    }
}
