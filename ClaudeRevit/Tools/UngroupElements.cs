using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class UngroupElements : IRevitTool
{
    public string Name => "ungroup_elements";

    public string Description =>
        "Ungroups one or more groups, leaving the member elements behind as independent objects. " +
        "Returns the ids of the freed members per group.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["group_ids"] = JsonSerializer.SerializeToElement(new
            {
                type = "array", minItems = 1,
                description = "Group instance ids to ungroup.",
                items = new { type = "integer" }
            })
        },
        Required = ["group_ids"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var ids = input["group_ids"].EnumerateArray()
            .Select(e => new ElementId(e.GetInt64())).ToList();

        var results = new List<object>();
        var skipped = new List<object>();

        foreach (var id in ids)
        {
            var group = doc.GetElement(id) as Group;
            if (group == null) { skipped.Add(new { id = id.Value, reason = "not a Group" }); continue; }
            try
            {
                var members = group.UngroupMembers();
                results.Add(new
                {
                    ungrouped_id = id.Value,
                    member_count = members.Count,
                    member_ids = members.Take(20).Select(m => m.Value).ToList(),
                    member_ids_truncated = members.Count > 20
                });
            }
            catch (Exception ex)
            {
                skipped.Add(new { id = id.Value, reason = ex.Message });
            }
        }

        return JsonSerializer.Serialize(new
        {
            ungrouped_count = results.Count,
            results,
            skipped_count = skipped.Count,
            skipped
        });
    }
}
