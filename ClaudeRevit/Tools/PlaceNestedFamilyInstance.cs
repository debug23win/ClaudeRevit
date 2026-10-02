using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class PlaceNestedFamilyInstance : IRevitTool
{
    public string Name => "place_nested_family_instance";
    public string Description => "Place a loaded native family type inside the active RFA, then associate child instance parameters to parent family parameters by name/GUID. Supports unhosted OneLevelBased and explicit face-hosted WorkPlaneBased families. Choose the appropriate family template for other hosting kinds. Coordinates mm; child shared status is reported, not changed.";
    public bool RequiresTransaction => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["type_id"] = NativeToolUtil.Field("integer", "Loaded child FamilySymbol in active RFA."),
        ["point_mm"] = NativeToolUtil.Any("Placement [x,y,z]."),
        ["face_reference"] = NativeToolUtil.Field("string", "Stable face reference for WorkPlaneBased."),
        ["document_key"] = NativeToolUtil.Field("string", "Required with face_reference."),
        ["reference_direction"] = NativeToolUtil.Any("Unit tangent on face, required for WorkPlaneBased."),
        ["associations"] = NativeToolUtil.Any("Map child parameter exact name/GUID to existing parent family parameter name/GUID.")
    }, "type_id", "point_mm");
    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = NativeToolUtil.Doc(app); var fm = FamilyEditorUtil.Manager(doc);
        var symbol = NativeToolUtil.Element(doc, ToolInput.RequiredLong(input, "type_id")) as FamilySymbol ?? throw new ToolInputException("type_id must be a loaded FamilySymbol in this RFA.");
        if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
        var point = NativeToolUtil.Point(input["point_mm"]);
        FamilyInstance instance;
        if (symbol.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased)
            instance = doc.FamilyCreate.NewFamilyInstance(point, symbol, StructuralType.NonStructural);
        else if (symbol.Family.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased)
        {
            if (NativeToolUtil.Text(input, "document_key") != Services.DocumentSessions.Key(doc)) throw new ToolInputException("Face reference document differs; inspect again.");
            instance = doc.FamilyCreate.NewFamilyInstance(Reference.ParseFromStableRepresentation(doc, ToolInput.RequiredString(input, "face_reference")), point,
                NativeToolUtil.Point(input["reference_direction"], false).Normalize(), symbol);
        }
        else throw new ToolInputException("Unsupported nested hosting: " + symbol.Family.FamilyPlacementType + ". Use an unhosted/work-plane family or an explicit host workflow.");
        if (input.TryGetValue("associations", out var associations))
            foreach (var association in associations.EnumerateObject())
            {
                var guid = Guid.TryParse(association.Name, out _) ? association.Name : "";
                var child = NativeToolUtil.Parameter(instance, guid.Length == 0 ? association.Name : "", guid) ?? throw new ToolInputException("Missing child parameter: " + association.Name);
                if (!fm.CanElementParameterBeAssociated(child)) throw new ToolInputException("Cannot associate child parameter: " + association.Name);
                fm.AssociateElementParameterToFamilyParameter(child, FamilyDocumentScope.Parameter(fm, association.Value.GetString() ?? ""));
            }
        doc.Regenerate();
        return Services.Json.Serialize(new { instance_id = instance.Id.Value, child_family = symbol.Family.Name, type = symbol.Name,
            shared = symbol.Family.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger() == 1 });
    }
}
