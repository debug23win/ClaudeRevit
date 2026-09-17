using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Host ↔ hosted navigation. "Which wall is this door in?" and the reverse "what's hosted in this
// wall?" are constant questions when editing, and without them the model has to guess from
// geometry. Also reports rebar hosts, which is how reinforcement is checked.
public class GetElementHosts : IRevitTool
{
    public string Name => "get_element_hosts";

    public string Description =>
        "For each element, report its HOST (the wall a door sits in, the element rebar reinforces, the face a " +
        "family is placed on) and, with direction='hosted', the reverse: every element hosted BY the given " +
        "element. Returns ids, names, categories and types so you can act on the result directly.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["element_ids"] = JsonSerializer.SerializeToElement(new
            {
                type = "array",
                minItems = 1,
                description = "Elements to inspect.",
                items = new { type = "integer" }
            }),
            ["direction"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                @enum = new[] { "host", "hosted" },
                description = "'host' (default) = find what hosts these elements. 'hosted' = find what these elements host."
            })
        },
        Required = ["element_ids"]
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var ids = input["element_ids"].EnumerateArray().Select(e => new ElementId(e.GetInt64())).ToList();
        var reverse = input.TryGetValue("direction", out var d) &&
                      string.Equals(d.GetString(), "hosted", StringComparison.OrdinalIgnoreCase);

        var results = ids.Select(id =>
        {
            var el = doc.GetElement(id);
            if (el == null) return (object)new { id = id.Value, error = "not found" };

            try
            {
                return reverse
                    ? new { id = id.Value, name = el.Name, category = el.Category?.Name, hosted = HostedBy(doc, el), error = (string?)null }
                    : (object)new { id = id.Value, name = el.Name, category = el.Category?.Name, host = HostOf(doc, el), error = (string?)null };
            }
            catch (Exception ex) { return new { id = id.Value, error = ex.Message }; }
        }).ToList();

        return Services.Json.Serialize(results);
    }

    private static object? HostOf(Document doc, Element el)
    {
        // Family instances (doors, windows, face-based families) carry Host directly.
        if (el is FamilyInstance fi && fi.Host != null) return Describe(doc, fi.Host);

        // Rebar and area/path reinforcement expose their host through GetHostId().
        switch (el)
        {
            case Rebar r: return Describe(doc, doc.GetElement(r.GetHostId()));
            case AreaReinforcement ar: return Describe(doc, doc.GetElement(ar.GetHostId()));
            case PathReinforcement pr: return Describe(doc, doc.GetElement(pr.GetHostId()));
        }

        // Fall back to the generic host parameter (covers several hosted categories).
        var p = el.get_Parameter(BuiltInParameter.HOST_ID_PARAM);
        if (p != null && p.StorageType == StorageType.ElementId)
        {
            var hid = p.AsElementId();
            if (hid != ElementId.InvalidElementId) return Describe(doc, doc.GetElement(hid));
        }
        return null;
    }

    // The reverse lookup has no direct API, so scan the plausible hosted categories once and match
    // on host id — bounded and predictable, unlike a whole-document sweep.
    private static List<object> HostedBy(Document doc, Element host)
    {
        var found = new List<object>();

        foreach (var fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                     .Cast<FamilyInstance>())
        {
            try { if (fi.Host != null && fi.Host.Id == host.Id) found.Add(Describe(doc, fi)!); }
            catch { }
        }
        foreach (var r in new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>())
        {
            try { if (r.GetHostId() == host.Id) found.Add(Describe(doc, r)!); }
            catch { }
        }
        foreach (var ar in new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcement)).Cast<AreaReinforcement>())
        {
            try { if (ar.GetHostId() == host.Id) found.Add(Describe(doc, ar)!); }
            catch { }
        }
        foreach (var pr in new FilteredElementCollector(doc).OfClass(typeof(PathReinforcement)).Cast<PathReinforcement>())
        {
            try { if (pr.GetHostId() == host.Id) found.Add(Describe(doc, pr)!); }
            catch { }
        }
        return found;
    }

    private static object? Describe(Document doc, Element? e) => e == null ? null : new
    {
        id = e.Id.Value,
        name = e.Name,
        category = e.Category?.Name,
        type_name = doc.GetElement(e.GetTypeId())?.Name
    };
}
