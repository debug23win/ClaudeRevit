using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class ListViewTemplates : IRevitTool
{
    public string Name => "list_view_templates";

    public string Description =>
        "Lists all view templates in the project: id, name, the view kind they apply to " +
        "(FloorPlan, Section, ThreeD…) and discipline (Architectural, Structural…). " +
        "Use before apply_view_template to pick a template that matches the target view's kind.";

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

        var templates = new FilteredElementCollector(doc)
            .OfClass(typeof(View)).Cast<View>()
            .Where(v => v.IsTemplate)
            .Select(v =>
            {
                string? discipline;
                try { discipline = v.Discipline.ToString(); }
                catch { discipline = null; } // not all view kinds expose a discipline
                return new
                {
                    id = v.Id.Value,
                    name = v.Name,
                    view_type = v.ViewType.ToString(),
                    discipline
                };
            })
            .OrderBy(t => t.view_type).ThenBy(t => t.name)
            .ToList();

        return JsonSerializer.Serialize(new { count = templates.Count, view_templates = templates });
    }
}
