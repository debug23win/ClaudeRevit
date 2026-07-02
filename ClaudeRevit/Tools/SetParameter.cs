using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class SetParameter : IRevitTool
{
    public string Name => "set_parameter";

    public string Description =>
        "Sets a parameter value on a single element. The element is identified by id, the parameter by name. " +
        "Numeric parameters expect feet for length and square feet for area (Revit's internal units) - convert from meters first. " +
        "For ElementId-typed parameters (references to a type/material/level, e.g. a rebar bar type), pass EITHER the " +
        "element's numeric id OR its name as a string - the name is resolved to the matching element automatically. " +
        "Use query_elements or get_element_parameters to discover an element's parameters.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["element_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Target element id." }),
            ["parameter_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Parameter name (case-sensitive)." }),
            ["value"] = JsonSerializer.SerializeToElement(new
            {
                description = "New value. String for text params (or a type/material/level NAME for ElementId params), " +
                              "number for length/integer/double params, boolean for yes/no params, or a numeric id for ElementId params.",
                oneOf = new object[]
                {
                    new { type = "string" },
                    new { type = "number" },
                    new { type = "boolean" },
                    new { type = "integer" }
                }
            })
        },
        Required = ["element_id", "parameter_name", "value"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var id = new ElementId(input["element_id"].GetInt64());
        var element = doc.GetElement(id)
            ?? throw new InvalidOperationException($"Element {id.Value} not found.");

        var paramName = input["parameter_name"].GetString()!;
        var param = element.LookupParameter(paramName)
            ?? throw new InvalidOperationException(
                $"Parameter '{paramName}' not found on element {id.Value} ({element.Category?.Name}).");

        if (param.IsReadOnly)
            throw new InvalidOperationException($"Parameter '{paramName}' is read-only.");

        var value = input["value"];
        string extra = "";
        bool ok;
        switch (param.StorageType)
        {
            case StorageType.String:
                ok = param.Set(value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString());
                break;

            case StorageType.Integer:
                if (value.ValueKind == JsonValueKind.True) ok = param.Set(1);
                else if (value.ValueKind == JsonValueKind.False) ok = param.Set(0);
                else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var iv)) ok = param.Set(iv);
                else throw Mismatch(paramName, "Integer", "an integer or boolean", value);
                break;

            case StorageType.Double:
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var dv)) ok = param.Set(dv);
                else throw Mismatch(paramName, "Double", "a number (feet for lengths)", value);
                break;

            case StorageType.ElementId:
                ok = SetElementIdParam(doc, param, paramName, value, out extra);
                if (!ok) throw new InvalidOperationException(extra);
                break;

            default:
                throw new InvalidOperationException($"Unsupported parameter storage type: {param.StorageType}");
        }

        if (!ok)
            throw new InvalidOperationException(
                $"Failed to set '{paramName}' - the value was rejected as invalid for this parameter.");

        return JsonSerializer.Serialize(new
        {
            element_id = id.Value,
            parameter = paramName,
            storage_type = param.StorageType.ToString(),
            new_value = param.AsValueString() ?? param.AsString() ?? value.ToString(),
            note = string.IsNullOrEmpty(extra) ? null : extra
        });
    }

    // Sets an ElementId-typed parameter. Accepts a numeric id directly, or resolves a string
    // NAME to a matching type / material / level and validates it via Parameter.Set (letting
    // Revit itself confirm the element is an allowed value for this parameter).
    private static bool SetElementIdParam(Document doc, Parameter param, string paramName, JsonElement value, out string detail)
    {
        detail = "";

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var raw))
        {
            if (param.Set(new ElementId(raw))) return true;
            detail = $"Element id {raw} is not a valid value for ElementId parameter '{paramName}'.";
            return false;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            detail = $"Parameter '{paramName}' is ElementId-typed and expects a numeric id or a type/element name (string), " +
                     $"but received a {value.ValueKind} value.";
            return false;
        }

        var name = value.GetString() ?? "";

        // Candidates whose Name matches, searched where ElementId params usually point:
        // element types (rebar bar type, wall type, family type...), then materials, then levels.
        var candidates = new List<Element>();
        void AddNamed(FilteredElementCollector c)
        {
            foreach (var e in c)
            {
                string n;
                try { n = e.Name; } catch { continue; }
                if (n == name) candidates.Add(e);
            }
        }
        AddNamed(new FilteredElementCollector(doc).WhereElementIsElementType());
        AddNamed(new FilteredElementCollector(doc).OfClass(typeof(Material)));
        AddNamed(new FilteredElementCollector(doc).OfClass(typeof(Level)));

        foreach (var cand in candidates)
        {
            try
            {
                if (param.Set(cand.Id))
                {
                    detail = $"resolved name '{name}' to {cand.GetType().Name} id {cand.Id.Value}";
                    return true;
                }
            }
            catch { /* wrong element for this parameter - try the next candidate */ }
        }

        detail = candidates.Count == 0
            ? $"No type/material/level named '{name}' found for ElementId parameter '{paramName}'. " +
              "Pass the element's numeric id instead, or check the exact name."
            : $"Found {candidates.Count} element(s) named '{name}', but none is a valid value for parameter '{paramName}'.";
        return false;
    }

    private static InvalidOperationException Mismatch(string paramName, string storage, string expected, JsonElement value) =>
        new($"Parameter '{paramName}' has storage type {storage} and expects {expected}, " +
            $"but received a {value.ValueKind} value: {value}.");
}
