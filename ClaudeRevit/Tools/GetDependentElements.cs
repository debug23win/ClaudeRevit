using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

// Reports what depends on an element — the elements Revit would also remove or that reference
// it (dimensions, tags, hosted components), plus its host/super-component. This is the
// "why can't I delete this / what will break" check that recurred whenever a delete failed
// or an element wouldn't move independently.
public class GetDependentElements : IRevitTool
{
    public string Name => "get_dependent_elements";

    public string Description =>
        "Returns, for each given element, what depends on it: the elements Revit would delete or " +
        "invalidate along with it (dimensions/tags that reference it, hosted or nested components), plus " +
        "its host / super-component. Use it before deleting to see what would break, or to diagnose why a " +
        "delete failed or an element can't be edited independently.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["element_ids"] = JsonSerializer.SerializeToElement(new
            {
                type = "array",
                items = new { type = "integer" },
                description = "Element ids to inspect."
            })
        },
        Required = ["element_ids"]
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        if (!input.TryGetValue("element_ids", out var idsEl) || idsEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("element_ids must be an array of integers.");

        var results = new List<object>();
        foreach (var el in idsEl.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var raw))
            {
                results.Add(new { error = "not an integer id", value = el.ToString() });
                continue;
            }
            var id = new ElementId(raw);
            var element = doc.GetElement(id);
            if (element == null) { results.Add(new { id = raw, error = "not found" }); continue; }

            var dependents = new List<object>();
            try
            {
                foreach (var depId in element.GetDependentElements(null))
                {
                    if (depId == id) continue; // an element always lists itself
                    var de = doc.GetElement(depId);
                    if (de == null) continue;
                    dependents.Add(new { id = depId.Value, type = de.GetType().Name, name = SafeName(de), category = de.Category?.Name });
                    if (dependents.Count >= 100) break;
                }
            }
            catch { /* some elements don't support dependency queries */ }

            long? superComponent = null;
            if (element is FamilyInstance fi && fi.SuperComponent != null)
                superComponent = fi.SuperComponent.Id.Value;

            long? host = null;
            if (element is FamilyInstance fih && fih.Host != null)
                host = fih.Host.Id.Value;

            results.Add(new
            {
                id = raw,
                type = element.GetType().Name,
                category = element.Category?.Name,
                pinned = TryPinned(element),
                host,
                super_component = superComponent,
                dependent_count = dependents.Count,
                dependents
            });
        }

        return Json.Serialize(new { count = results.Count, elements = results });
    }

    private static string? SafeName(Element e) { try { return e.Name; } catch { return null; } }
    private static bool? TryPinned(Element e) { try { return e.Pinned; } catch { return null; } }
}
