using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Progressive tool loading. Only a small CORE toolset is sent to the model on every request;
// the specialised long tail (rebar, MEP, schedules, sheets, annotation, view creation,
// family-editor authoring, export, groups, visibility) is loaded on demand. The model calls
// find_tools with a short query to pull in the group it needs — the chat loop intercepts the
// call, reveals the matching group(s) for the rest of the session, and rebuilds the tool list so
// they become callable on the very next round. This Execute() is the fallback for the manual
// "Run tool" window (it searches and lists, but only the chat loop can actually reveal).
public class FindTools : IRevitTool
{
    public string Name => "find_tools";

    public string Description =>
        "Loads specialised tools that are NOT in your default toolset. You start with a core set (model " +
        "inspection, the common modelling verbs, code). For anything else — rebar/reinforcement, MEP " +
        "(duct/pipe), schedules, sheets, annotation (tags/dimensions/text/spot/revision), view creation " +
        "(sections/elevations/callouts/3D/camera/crop), family-editor authoring (parameters/formulas), " +
        "export (PDF/DWG/image), groups, visibility/filters — call find_tools with a short query (e.g. " +
        "\"rebar stirrups\", \"section view\", \"tag doors\", \"quantity schedule\"). The matching tools " +
        "load immediately and you call them on the next step. Prefer this over execute_csharp when a " +
        "dedicated tool likely exists.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["query"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "A few words describing the operation you need a tool for " +
                              "(e.g. \"create section\", \"place rebar\", \"schedule of walls\")."
            })
        },
        Required = ["query"]
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var query = input.TryGetValue("query", out var q) && q.ValueKind == JsonValueKind.String
            ? q.GetString() ?? "" : "";
        // No reveal here (the manual Run-tool window has no session): just report matches.
        return ToolCatalog.Search(ToolRegistry.Instance.All, query).Message;
    }
}
