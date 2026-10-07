using System;
using System.Collections.Generic;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class SetTypeParameter : IRevitTool
{
    public string Name => "set_type_parameter";

    public string Description =>
        "Sets a parameter on an element TYPE (FamilySymbol, WallType, FloorType, etc.). Changes apply to " +
        "every instance using that type. Use get_element_parameters on a type id to see available parameters " +
        "and storage types. For instance parameters, use set_parameter instead.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["type_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Type element id." }),
            ["parameter_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Parameter name (exact match)." }),
            ["value"] = JsonSerializer.SerializeToElement(new { description = "Value (string, number, integer, or boolean)." })
        },
        Required = ["type_id", "parameter_name", "value"]
    };

    public bool RequiresTransaction => true;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = ToolContext.UiDocument(app)?.Document
            ?? throw new InvalidOperationException("No document is open.");

        var typeId = new ElementId(ToolInput.RequiredLong(input, "type_id"));
        var type = doc.GetElement(typeId) as ElementType
            ?? throw new InvalidOperationException($"Element {typeId.Value} is not an ElementType.");

        var paramName = ToolInput.RequiredText(input, "parameter_name")!;
        var param = type.LookupParameter(paramName)
            ?? throw NameResolve.Missing(paramName, $"Parameter of type '{type.Name}' named", type.Parameters.Cast<Parameter>().Select(p => p.Definition?.Name));

        if (param.IsReadOnly)
            throw new InvalidOperationException($"Parameter '{paramName}' is read-only.");

        var raw = ToolInput.Required(input, "value");
        switch (param.StorageType)
        {
            case StorageType.String:
                param.Set(raw.ValueKind == JsonValueKind.String ? raw.GetString()! : raw.GetRawText());
                break;
            case StorageType.Integer:
                if (raw.ValueKind == JsonValueKind.True) param.Set(1);
                else if (raw.ValueKind == JsonValueKind.False) param.Set(0);
                else param.Set(raw.GetInt32());
                break;
            case StorageType.Double:
                param.Set(raw.GetDouble());
                break;
            case StorageType.ElementId:
                param.Set(new ElementId(raw.GetInt64()));
                break;
            default:
                throw new InvalidOperationException($"Unsupported storage type: {param.StorageType}");
        }

        return Services.Json.Serialize(new
        {
            type_id = typeId.Value,
            type_name = type.Name,
            parameter = paramName,
            new_value = param.AsValueString() ?? param.AsString() ?? raw.GetRawText()
        });
    }
}
