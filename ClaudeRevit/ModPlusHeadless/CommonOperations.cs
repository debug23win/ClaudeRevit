#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace ModPlus.Headless;

// Granular API operations with explicit element IDs and parameter selectors.
internal static class CommonOperations
{
    internal static bool Supports(string operation) => operation is "set_parameters" or "copy_parameter_values" or "number_elements" or "replace_parameter_text" or "set_element_visibility" or "set_element_color" or "delete_elements" or "join_geometry" or "unjoin_geometry" or "switch_join_order" or "duplicate_views" or "change_element_type" or "assign_workset";
    internal static object Apply(string operation, JsonElement args, Document doc)
    {
        if (operation is "join_geometry" or "unjoin_geometry" or "switch_join_order")
        {
            JsonElement[] pairs = args.GetProperty("pairs").EnumerateArray().ToArray();
            if (pairs.Length < 1 || pairs.Length > 10000) throw new ArgumentException("Supply 1..10000 element pairs.");
            foreach (JsonElement pair in pairs)
            {
                if (pair.GetArrayLength() != 2) throw new ArgumentException("Each pair must contain exactly two IDs.");
                Element first = Element(doc, pair[0].GetInt64()), second = Element(doc, pair[1].GetInt64());
                if (first.Id == second.Id) throw new ArgumentException("Cannot join an element to itself.");
                bool joined = JoinGeometryUtils.AreElementsJoined(doc, first, second);
                if (operation == "join_geometry" && !joined) JoinGeometryUtils.JoinGeometry(doc, first, second);
                if (operation == "unjoin_geometry" && joined) JoinGeometryUtils.UnjoinGeometry(doc, first, second);
                if (operation == "switch_join_order") { if (!joined) throw new ArgumentException("The elements must be joined first."); JoinGeometryUtils.SwitchJoinOrder(doc, first, second); }
            }
            return new { processedPairs = pairs.Length };
        }
        Element[] elements = Ids(args).Select(id => Element(doc, id)).ToArray();
        switch (operation)
        {
            case "copy_parameter_values":
            {
                Element source = Element(doc, args.GetProperty("source_element_id").GetInt64());
                JsonElement[] mappings = args.GetProperty("parameters").EnumerateArray().ToArray();
                if (mappings.Length < 1 || mappings.Length > 100) throw new ArgumentException("Supply 1..100 parameter mappings.");
                foreach (JsonElement mapping in mappings)
                {
                    Parameter from = Resolve(source, mapping.GetProperty("from"));
                    if (!from.HasValue) throw new ArgumentException("The source parameter has no value: " + from.Definition.Name);
                    object value = from.StorageType switch
                    {
                        StorageType.String => from.AsString() ?? "",
                        StorageType.Integer => from.AsInteger(),
                        StorageType.Double => from.AsDouble(),
                        StorageType.ElementId => from.AsElementId().Value,
                        _ => throw new ArgumentException("Unsupported source parameter storage type.")
                    };
                    foreach (Element element in elements)
                    {
                        Parameter to = Resolve(element, mapping.GetProperty("to"));
                        if (to.StorageType != from.StorageType || !to.Definition.GetDataType().Equals(from.Definition.GetDataType())) throw new ArgumentException("Source and target parameter storage/data types must match.");
                        Set(to, JsonSerializer.SerializeToElement(value));
                    }
                }
                return new { sourceId = source.Id.Value, updated = elements.Select(e => e.Id.Value).ToArray(), mappingCount = mappings.Length };
            }
            case "set_parameters":
            {
                JsonElement[] values = args.GetProperty("values").EnumerateArray().ToArray();
                if (values.Length < 1 || values.Length > 100) throw new ArgumentException("Supply 1..100 parameter values.");
                foreach (Element element in elements) foreach (JsonElement entry in values) Set(Resolve(element, entry.GetProperty("parameter")), entry.GetProperty("value"));
                return new { updated = elements.Select(e => e.Id.Value).ToArray() };
            }
            case "number_elements":
            {
                long start = args.GetProperty("start").GetInt64(), step = args.TryGetProperty("step", out JsonElement s) ? s.GetInt64() : 1;
                string prefix = args.TryGetProperty("prefix", out JsonElement p) ? p.GetString() : "", suffix = args.TryGetProperty("suffix", out JsonElement suf) ? suf.GetString() : "";
                int width = args.TryGetProperty("width", out JsonElement w) ? w.GetInt32() : 0;
                if (width < 0 || width > 32) throw new ArgumentException("width must be 0..32.");
                var result = new List<object>();
                for (int i = 0; i < elements.Length; i++)
                {
                    Parameter parameter = Resolve(elements[i], args.GetProperty("parameter"));
                    long number = checked(start + i * step);
                    string text = prefix + number.ToString(width > 0 ? "D" + width : "G", CultureInfo.InvariantCulture) + suffix;
                    if (parameter.StorageType == StorageType.String) Set(parameter, JsonSerializer.SerializeToElement(text));
                    else if (parameter.StorageType == StorageType.Integer && prefix.Length == 0 && suffix.Length == 0 && width == 0) Set(parameter, JsonSerializer.SerializeToElement(checked((int)number)));
                    else throw new ArgumentException("Numbering needs a string parameter, or an integer parameter without formatting.");
                    result.Add(new { elementId = elements[i].Id.Value, value = text });
                }
                return new { rows = result };
            }
            case "replace_parameter_text":
            {
                string search = args.GetProperty("find").GetString(), replacement = args.GetProperty("replace").GetString();
                if (string.IsNullOrEmpty(search) || replacement == null) throw new ArgumentException("find must be non-empty and replace must be a string.");
                StringComparison comparison = args.TryGetProperty("ignore_case", out JsonElement ic) && ic.GetBoolean() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                int changed = 0;
                foreach (Element element in elements)
                {
                    Parameter parameter = Resolve(element, args.GetProperty("parameter"));
                    if (parameter.StorageType != StorageType.String) throw new ArgumentException("Text replacement requires a string parameter.");
                    string before = parameter.AsString() ?? "", after = before.Replace(search, replacement, comparison);
                    if (before != after) { Set(parameter, JsonSerializer.SerializeToElement(after)); changed++; }
                }
                return new { updatedCount = changed };
            }
            case "set_element_visibility":
            {
                View view = Element(doc, args.GetProperty("view_id").GetInt64()) as View ?? throw new ArgumentException("Supply a view ID.");
                bool hidden = args.GetProperty("hidden").GetBoolean();
                List<ElementId> ids = elements.Where(e => e.IsHidden(view) != hidden).Select(e => e.Id).ToList();
                if (hidden && elements.Any(e => !e.IsHidden(view) && !e.CanBeHidden(view))) throw new ArgumentException("An element cannot be hidden in this view.");
                if (ids.Count > 0) { if (hidden) view.HideElements(ids); else view.UnhideElements(ids); }
                return new { updated = ids.Select(id => id.Value).ToArray(), hidden };
            }
            case "set_element_color":
            {
                View view = Element(doc, args.GetProperty("view_id").GetInt64()) as View ?? throw new ArgumentException("Supply a view ID.");
                string rgb = args.GetProperty("color").GetString()?.TrimStart('#');
                if (rgb == null || rgb.Length != 6) throw new ArgumentException("color must be #RRGGBB.");
                uint value = uint.Parse(rgb, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var color = new Color((byte)(value >> 16), (byte)(value >> 8), (byte)value);
                foreach (Element element in elements)
                {
                    OverrideGraphicSettings settings = view.GetElementOverrides(element.Id);
                    settings.SetProjectionLineColor(color); settings.SetCutLineColor(color);
                    view.SetElementOverrides(element.Id, settings);
                }
                return new { updated = elements.Select(e => e.Id.Value).ToArray(), semantics = "Projection/cut line colors; existing other overrides are preserved." };
            }
            case "delete_elements":
                return new { deleted = doc.Delete(elements.Select(e => e.Id).ToList()).Select(id => id.Value).ToArray(), note = "The result includes dependent elements deleted by Revit." };
            case "duplicate_views":
            {
                string optionName = args.GetProperty("option").GetString();
                ViewDuplicateOption option = Enum.Parse<ViewDuplicateOption>(optionName, false);
                if (!Enum.IsDefined(option)) throw new ArgumentException("Unknown view duplicate option.");
                return new { rows = elements.Select(e =>
                {
                    View view = e as View ?? throw new ArgumentException("Supply view IDs.");
                    if (!view.CanViewBeDuplicated(option)) throw new ArgumentException("View " + view.Id + " cannot be duplicated with " + option);
                    return new { sourceId = view.Id.Value, createdId = view.Duplicate(option).Value };
                }).ToArray() };
            }
            case "change_element_type":
            {
                ElementId typeId = (Element(doc, args.GetProperty("type_id").GetInt64()) as ElementType ?? throw new ArgumentException("Supply an ElementType ID.")).Id;
                return new { rows = elements.Select(e =>
                {
                    if (!e.IsValidType(typeId)) throw new ArgumentException("The type is incompatible with element " + e.Id);
                    long before = e.Id.Value;
                    ElementId replacement = e.ChangeTypeId(typeId);
                    return new { sourceId = before, resultId = replacement == ElementId.InvalidElementId ? before : replacement.Value };
                }).ToArray() };
            }
            case "assign_workset":
            {
                if (!doc.IsWorkshared) throw new ArgumentException("The document is not workshared.");
                int worksetId = args.GetProperty("workset_id").GetInt32();
                if (!new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Any(w => w.Id.IntegerValue == worksetId)) throw new ArgumentException("Supply a user workset ID.");
                foreach (Element element in elements) Set(element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM), JsonSerializer.SerializeToElement(worksetId));
                return new { updated = elements.Select(e => e.Id.Value).ToArray(), worksetId };
            }
        }
        throw new ArgumentException("Unsupported common operation.");
    }
    private static long[] Ids(JsonElement args)
    {
        long[] ids = args.GetProperty("element_ids").EnumerateArray().Select(i => i.GetInt64()).ToArray();
        if (ids.Length < 1 || ids.Length > 10000 || ids.Distinct().Count() != ids.Length) throw new ArgumentException("Supply 1..10000 unique IDs; their order defines numbering order.");
        return ids;
    }
    private static Element Element(Document doc, long id) => doc.GetElement(new ElementId(id)) ?? throw new ArgumentException("Missing element " + id);
    private static Parameter Resolve(Element element, JsonElement selector)
    {
        JsonProperty[] keys = selector.EnumerateObject().ToArray();
        if (keys.Length != 1) throw new ArgumentException("Choose exactly one parameter selector: built_in, guid, id, or name.");
        Parameter parameter = keys[0].Name switch
        {
            "built_in" => element.get_Parameter(Enum.Parse<BuiltInParameter>(keys[0].Value.GetString(), false)),
            "guid" => element.get_Parameter(Guid.Parse(keys[0].Value.GetString())),
            "id" => element.Parameters.Cast<Parameter>().SingleOrDefault(p => p.Id.Value == keys[0].Value.GetInt64()),
            "name" => ByName(element, keys[0].Value.GetString()),
            _ => throw new ArgumentException("Unknown parameter selector.")
        };
        return parameter ?? throw new ArgumentException("The parameter is unavailable on element " + element.Id);
    }
    private static Parameter ByName(Element element, string name)
    {
        IList<Parameter> matches = element.GetParameters(name);
        if (matches.Count != 1) throw new ArgumentException("Parameter name is missing or ambiguous: " + name + ". Prefer a GUID or built-in ID.");
        return matches[0];
    }
    private static void Set(Parameter parameter, JsonElement value)
    {
        if (parameter == null || parameter.IsReadOnly) throw new ArgumentException("A parameter is unavailable or read-only.");
        bool accepted = parameter.StorageType switch
        {
            StorageType.String => (parameter.AsString() ?? "") == value.GetString() || parameter.Set(value.GetString()),
            StorageType.Integer => parameter.AsInteger() == value.GetInt32() || parameter.Set(value.GetInt32()),
            StorageType.ElementId => parameter.AsElementId()?.Value == value.GetInt64() || parameter.Set(new ElementId(value.GetInt64())),
            StorageType.Double => SetDouble(parameter, value),
            _ => throw new ArgumentException("Unsupported parameter storage type.")
        };
        if (!accepted) throw new ArgumentException("Revit rejected the parameter value.");
    }
    private static bool SetDouble(Parameter parameter, JsonElement value)
    {
        double number = Scalar(value, parameter.Definition.GetDataType());
        return parameter.AsDouble().Equals(number) || parameter.Set(number);
    }
    private static double Scalar(JsonElement value, ForgeTypeId dataType)
    {
        double number;
        if (value.ValueKind == JsonValueKind.Number) number = value.GetDouble(); // explicitly internal units
        else
        {
            number = value.GetProperty("value").GetDouble();
            string unit = value.GetProperty("unit").GetString();
            ForgeTypeId unitId = unit switch { "mm" => UnitTypeId.Millimeters, "m" => UnitTypeId.Meters, "ft" => UnitTypeId.Feet, "deg" => UnitTypeId.Degrees, "rad" => UnitTypeId.Radians, "internal" => null, _ => throw new ArgumentException("Supported units: mm, m, ft, deg, rad, internal.") };
            if (unitId != null)
            {
                if (!UnitUtils.IsValidUnit(dataType, unitId)) throw new ArgumentException("The unit is incompatible with this parameter's data type.");
                number = UnitUtils.ConvertToInternalUnits(number, unitId);
            }
        }
        if (!double.IsFinite(number)) throw new ArgumentException("A finite parameter value is required.");
        return number;
    }
}
