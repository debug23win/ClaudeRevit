using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class ListWorksets : IRevitTool
{
    public string Name => "list_worksets";

    public string Description =>
        "Lists worksets in the document (workshared models only). Returns each workset's id, name, " +
        "kind (UserWorkset / FamilyWorkset / etc.), and whether it's open/editable. " +
        "If the document isn't workshared, returns a clear message.";

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

        if (!doc.IsWorkshared)
            return JsonSerializer.Serialize(new { workshared = false, message = "Document is not workshared." });

        var userWorksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToList();

        var rows = userWorksets.Select(ws => new
        {
            id = ws.Id.IntegerValue,
            name = ws.Name,
            kind = ws.Kind.ToString(),
            is_open = ws.IsOpen,
            is_editable = ws.IsEditable,
            owner = ws.Owner
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            workshared = true,
            user_workset_count = rows.Count,
            user_worksets = rows
        });
    }
}
