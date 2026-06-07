using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class SetViewPhase : IRevitTool
{
    public string Name => "set_view_phase";

    public string Description =>
        "Sets the 'Phase' parameter on a view (controls which model state is shown — Existing, New Construction, " +
        "etc.). Optionally also set 'Phase Filter' (which controls how each phase's elements are graphically " +
        "distinguished). Use get_phases for phase ids; for phase filter ids, list elements via query_elements " +
        "on category 'PhaseFilters' or find them in Revit UI.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["view_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "View id." }),
            ["phase_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Phase id (from get_phases)." }),
            ["phase_filter_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Optional phase filter id." })
        },
        Required = ["view_id"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var viewId = new ElementId(input["view_id"].GetInt64());
        var view = doc.GetElement(viewId) as View
            ?? throw new InvalidOperationException("view_id is not a view.");

        var changes = new List<string>();

        if (input.TryGetValue("phase_id", out var pid) && pid.ValueKind == JsonValueKind.Number)
        {
            var phaseParam = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
            if (phaseParam == null || phaseParam.IsReadOnly)
                throw new InvalidOperationException("View has no settable Phase parameter.");
            phaseParam.Set(new ElementId(pid.GetInt64()));
            changes.Add("phase");
        }

        if (input.TryGetValue("phase_filter_id", out var pfid) && pfid.ValueKind == JsonValueKind.Number)
        {
            var filterParam = view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER);
            if (filterParam == null || filterParam.IsReadOnly)
                throw new InvalidOperationException("View has no settable Phase Filter parameter.");
            filterParam.Set(new ElementId(pfid.GetInt64()));
            changes.Add("phase_filter");
        }

        if (changes.Count == 0)
            throw new InvalidOperationException("Provide at least one of phase_id or phase_filter_id.");

        return JsonSerializer.Serialize(new
        {
            view = view.Name,
            changed = changes
        });
    }
}
