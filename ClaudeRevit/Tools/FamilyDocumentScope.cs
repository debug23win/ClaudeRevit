using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

internal sealed class FamilyDocumentScope : IDisposable
{
    public Document Document { get; }
    private readonly bool _close;
    public FamilyDocumentScope(UIApplication app, IReadOnlyDictionary<string, JsonElement> input)
    {
        var source = NativeToolUtil.Doc(app);
        if (source.IsModifiable) throw new InvalidOperationException("Finish the current transaction before inspecting a family.");
        if (input.TryGetValue("family_id", out var id) && id.ValueKind == JsonValueKind.Number)
        {
            var family = NativeToolUtil.Element(source, id.GetInt64()) as Family ?? throw new ToolInputException("family_id must identify a loaded Family, not its type or instance.");
            if (!family.IsEditable || family.IsInPlace) throw new ToolInputException("This family cannot be opened as an editable RFA.");
            Document = source.EditFamily(family); _close = true;
        }
        else { FamilyEditorUtil.Manager(source); Document = source; }
    }
    public void Dispose() { if (_close && Document.IsValidObject) Document.Close(false); }
    public static FamilyParameter Parameter(FamilyManager fm, string nameOrGuid)
    {
        var byGuid = Guid.TryParse(nameOrGuid, out var guid);
        var matches = fm.Parameters.Cast<FamilyParameter>().Where(p => byGuid ? p.IsShared && p.GUID == guid : p.Definition.Name == nameOrGuid).ToList();
        if (matches.Count != 1) throw new ToolInputException($"Family parameter '{nameOrGuid}' is missing or ambiguous. Inspect GUIDs first.");
        return matches[0];
    }
    public static void Set(FamilyManager fm, FamilyParameter p, JsonElement value)
    {
        if (p.IsDeterminedByFormula || p.IsReadOnly || p.IsReporting) throw new ToolInputException($"'{p.Definition.Name}' cannot be driven directly.");
        switch (p.StorageType)
        {
            case StorageType.String: fm.Set(p, value.GetString() ?? ""); break;
            case StorageType.Integer:
                fm.Set(p, value.ValueKind == JsonValueKind.True ? 1 : value.ValueKind == JsonValueKind.False ? 0 : value.GetInt32()); break;
            case StorageType.ElementId: fm.Set(p, new ElementId(value.GetInt64())); break;
            case StorageType.Double:
                var number = value.GetDouble();
                if (!double.IsFinite(number)) throw new ToolInputException("Parameter value must be finite.");
                var spec = p.Definition.GetDataType();
                if (spec == SpecTypeId.Length) number /= Units.MmPerFoot;
                else if (spec == SpecTypeId.Angle) number *= Math.PI / 180;
                else if (spec == SpecTypeId.Area) number /= 0.3048 * 0.3048;
                else if (spec == SpecTypeId.Volume) number /= Math.Pow(0.3048, 3);
                fm.Set(p, number); break;
            default: throw new ToolInputException("Unsupported parameter storage type.");
        }
    }
}
