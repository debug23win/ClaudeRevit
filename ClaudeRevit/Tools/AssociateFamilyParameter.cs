using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

// Associates a parameter of a nested element (a rebar/hoop instance, an array, …) with a
// family parameter, so the family parameter drives it. This is the mechanism the scripts
// used to wire dozens of sub-instances to their controlling family parameters.
public class AssociateFamilyParameter : IRevitTool
{
    public string Name => "associate_family_parameter";

    public string Description =>
        "In the Family Editor, associates a parameter of a nested element with a family parameter, so " +
        "the family parameter drives that element (e.g. wire a nested rebar instance's diameter, or an " +
        "array's element count, to a family parameter). Identify the element parameter by its name " +
        "(as shown on the element) or a BuiltInParameter enum name (e.g. ARRAY_NUMBER). Types must be " +
        "compatible. Works only in the Family Editor.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["element_id"] = JsonSerializer.SerializeToElement(new
            {
                type = "integer",
                description = "Id of the nested element whose parameter is being driven."
            }),
            ["element_parameter"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Name of the parameter on that element, or a BuiltInParameter enum name " +
                              "(e.g. ARRAY_NUMBER)."
            }),
            ["family_parameter"] = JsonSerializer.SerializeToElement(new
            {
                type = "string",
                description = "Name of the family parameter that should drive it."
            })
        },
        Required = ["element_id", "element_parameter", "family_parameter"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("No document is open.");
        var fm = FamilyEditorUtil.Manager(doc);

        var id = new ElementId(input["element_id"].GetInt64());
        var element = doc.GetElement(id)
            ?? throw new InvalidOperationException($"Element {id.Value} not found.");

        var elemParamName = input["element_parameter"].GetString() ?? "";
        var elemParam = ResolveElementParameter(element, elemParamName)
            ?? throw new InvalidOperationException(
                $"Parameter '{elemParamName}' not found on element {id.Value} ({element.GetType().Name}). " +
                "Use get_element_parameters, or pass a BuiltInParameter enum name.");

        var famParamName = input["family_parameter"].GetString() ?? "";
        var famParam = FamilyEditorUtil.Require(fm, famParamName);

        if (!fm.CanElementParameterBeAssociated(elemParam))
            throw new InvalidOperationException(
                $"The parameter '{elemParamName}' on element {id.Value} cannot be associated " +
                "(it may be read-only or computed).");

        try
        {
            fm.AssociateElementParameterToFamilyParameter(elemParam, famParam);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Association failed: {ex.Message}. The family parameter's type must match the element " +
                "parameter's type (e.g. a Length family parameter for a length, an Integer for a count).");
        }
        doc.Regenerate();

        return JsonSerializer.Serialize(new
        {
            associated = true,
            element_id = id.Value,
            element_parameter = elemParamName,
            family_parameter = famParamName
        });
    }

    // Resolve by visible name first; fall back to a BuiltInParameter enum name (how arrays'
    // ARRAY_NUMBER and similar system parameters are reached).
    private static Parameter? ResolveElementParameter(Element element, string name)
    {
        var byName = element.LookupParameter(name);
        if (byName != null) return byName;

        if (Enum.TryParse<BuiltInParameter>(name, ignoreCase: false, out var bip))
        {
            try { return element.get_Parameter(bip); } catch { return null; }
        }
        return null;
    }
}
