using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Read elements out of LINKED models. Coordination questions — "what are the architect's levels?",
// "where are the MEP penetrations?" — need data that lives in another file, and Revit's normal
// collectors only ever see the host document. The subtlety that makes results usable is the link
// TRANSFORM: a linked element's coordinates are in the link's own space, so they must be mapped
// into host coordinates before they mean anything here.
public class QueryLinkedElements : IRevitTool
{
    public string Name => "query_linked_elements";

    public string Description =>
        "Read elements from a LINKED Revit model — coordination data the host document's own queries can't see. " +
        "Filter by category and optionally by a parameter value. Coordinates are returned in HOST coordinates " +
        "(the link transform is applied), so they line up with your model. " +
        "Omit `link_name` to search every loaded link. Use list_links first to see what's loaded. Read-only.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["link_name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Link to query (name or part of it). Omit to search all loaded links."
            }),
            ["category"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Category to read, e.g. 'Walls', 'Levels', 'Doors'."
            }),
            ["parameter_name"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Optional: only elements whose this parameter matches `parameter_value`."
            }),
            ["parameter_value"] = JsonSerializer.SerializeToElement(new
            {
                type = "string", description = "Value to match (case-insensitive substring)."
            }),
            ["include_parameters"] = JsonSerializer.SerializeToElement(new
            {
                type = "array", description = "Parameter names to return per element.", items = new { type = "string" }
            }),
            ["limit"] = JsonSerializer.SerializeToElement(new
            {
                type = "integer", description = "Max elements returned (default 100, max 1000).", minimum = 1, maximum = 1000
            })
        },
        Required = ["category"]
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var bic = CategoryResolve.Parse(input["category"].GetString());
        var limit = input.TryGetValue("limit", out var l) ? Math.Clamp(l.GetInt32(), 1, 1000) : 100;
        var wantParams = input.TryGetValue("include_parameters", out var ip) && ip.ValueKind == JsonValueKind.Array
            ? ip.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : new List<string>();

        string? filterParam = input.TryGetValue("parameter_name", out var fp) ? fp.GetString() : null;
        string? filterValue = input.TryGetValue("parameter_value", out var fv) ? fv.GetString() : null;

        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>().ToList();
        if (links.Count == 0)
            return Services.Json.Serialize(new { error = "No Revit links in this document. Use link_revit_model first." });

        if (input.TryGetValue("link_name", out var ln) && ln.ValueKind == JsonValueKind.String)
        {
            var want = ln.GetString() ?? "";
            links = links.Where(x => x.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (links.Count == 0)
                return Services.Json.Serialize(new { error = $"No loaded link matches '{want}'." });
        }

        var results = new List<object>();
        var perLink = new List<object>();
        var unloaded = new List<string>();

        foreach (var link in links)
        {
            // An unloaded link has no document — report it instead of silently returning nothing.
            var ldoc = link.GetLinkDocument();
            if (ldoc == null) { unloaded.Add(link.Name); continue; }

            var xf = link.GetTotalTransform();
            var found = 0;

            foreach (var el in new FilteredElementCollector(ldoc).OfCategory(bic).WhereElementIsNotElementType())
            {
                if (results.Count >= limit) break;
                try
                {
                    if (!string.IsNullOrEmpty(filterParam))
                    {
                        var p = el.LookupParameter(filterParam);
                        var text = p == null ? null : ValueOf(p);
                        if (text == null) continue;
                        if (!string.IsNullOrEmpty(filterValue) &&
                            text.IndexOf(filterValue!, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    }

                    var pt = PointOf(el);
                    var host = pt != null ? xf.OfPoint(pt) : null;   // link space → host space

                    var extras = new Dictionary<string, string?>();
                    foreach (var pn in wantParams)
                    {
                        var p = el.LookupParameter(pn);
                        extras[pn] = p == null ? null : ValueOf(p);
                    }

                    results.Add(new
                    {
                        link = link.Name,
                        id = el.Id.Value,
                        name = el.Name,
                        category = el.Category?.Name,
                        type_name = ldoc.GetElement(el.GetTypeId())?.Name,
                        level = el.LevelId != ElementId.InvalidElementId ? ldoc.GetElement(el.LevelId)?.Name : null,
                        host_x_mm = host != null ? Math.Round(Units.FeetToMm(host.X), 1) : (double?)null,
                        host_y_mm = host != null ? Math.Round(Units.FeetToMm(host.Y), 1) : (double?)null,
                        host_z_mm = host != null ? Math.Round(Units.FeetToMm(host.Z), 1) : (double?)null,
                        parameters = extras.Count > 0 ? extras : null
                    });
                    found++;
                }
                catch { }
            }
            perLink.Add(new { link = link.Name, document = ldoc.Title, matched = found });
        }

        return Services.Json.Serialize(new
        {
            links_searched = perLink,
            unloaded_links = unloaded,
            count = results.Count,
            truncated = results.Count >= limit,
            note = "Coordinates are in HOST coordinates (link transform applied). Linked elements are read-only.",
            elements = results
        });
    }

    private static string? ValueOf(Parameter p) => p.StorageType switch
    {
        StorageType.String => p.AsString(),
        StorageType.Integer => p.AsInteger().ToString(),
        StorageType.Double => p.AsValueString() ?? p.AsDouble().ToString("F3"),
        StorageType.ElementId => p.AsValueString() ?? p.AsElementId().Value.ToString(),
        _ => p.AsValueString()
    };

    private static XYZ? PointOf(Element el)
    {
        try
        {
            switch (el.Location)
            {
                case LocationPoint lp: return lp.Point;
                case LocationCurve lc: return lc.Curve.Evaluate(0.5, true);
            }
            if (el is Level lvl) return new XYZ(0, 0, lvl.Elevation);
            var bb = el.get_BoundingBox(null);
            return bb != null ? (bb.Min + bb.Max) * 0.5 : null;
        }
        catch { return null; }
    }
}
