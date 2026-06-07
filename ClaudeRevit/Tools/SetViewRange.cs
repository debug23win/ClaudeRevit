using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class SetViewRange : IRevitTool
{
    public string Name => "set_view_range";

    public string Description =>
        "Sets the view range planes on a plan view. Pass only the planes you want to change; " +
        "offsets are in feet relative to each plane's level. Common use: 'set the cut plane to 4 ft above level' " +
        "or 'lower the bottom plane to -2 ft'. Defaults to the active view.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["view_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Optional view id (plan view only)." }),
            ["top_offset_ft"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Top clip plane offset (feet)." }),
            ["cut_offset_ft"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Cut plane offset (feet)." }),
            ["bottom_offset_ft"] = JsonSerializer.SerializeToElement(new { type = "number", description = "Bottom clip plane offset (feet)." }),
            ["depth_offset_ft"] = JsonSerializer.SerializeToElement(new { type = "number", description = "View depth plane offset (feet)." })
        },
        Required = []
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        ViewPlan view;
        if (input.TryGetValue("view_id", out var vid) && vid.ValueKind == JsonValueKind.Number)
            view = doc.GetElement(new ElementId(vid.GetInt64())) as ViewPlan
                ?? throw new InvalidOperationException("view_id is not a plan view.");
        else
            view = doc.ActiveView as ViewPlan
                ?? throw new InvalidOperationException("Active view is not a plan view.");

        var range = view.GetViewRange();

        void TrySetOffset(string key, PlanViewPlane plane)
        {
            if (input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number)
                range.SetOffset(plane, v.GetDouble());
        }

        TrySetOffset("top_offset_ft", PlanViewPlane.TopClipPlane);
        TrySetOffset("cut_offset_ft", PlanViewPlane.CutPlane);
        TrySetOffset("bottom_offset_ft", PlanViewPlane.BottomClipPlane);
        TrySetOffset("depth_offset_ft", PlanViewPlane.ViewDepthPlane);

        view.SetViewRange(range);

        return JsonSerializer.Serialize(new
        {
            view = view.Name,
            top_offset_ft = range.GetOffset(PlanViewPlane.TopClipPlane),
            cut_offset_ft = range.GetOffset(PlanViewPlane.CutPlane),
            bottom_offset_ft = range.GetOffset(PlanViewPlane.BottomClipPlane),
            depth_offset_ft = range.GetOffset(PlanViewPlane.ViewDepthPlane)
        });
    }
}
