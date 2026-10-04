using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class CreateStructuralConnection : IRevitTool
{
    public string Name => "create_structural_connection";
    public string Description => "Create a native StructuralConnectionHandler linking actual structural member IDs. Optional connection_type_id chooses a loaded native detailed connection type; omit for a generic logical connection (no fabrication geometry). Verify connected member IDs and generated geometry. Generic connection is explicitly reported and does not certify joint design. One Undo call; no DirectShape imitation.";
    public bool RequiresTransaction => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["member_ids"]=NativeToolUtil.Array("integer","2..10 native structural column/framing member IDs."),
        ["connection_type_id"]=NativeToolUtil.Field("integer","Optional loaded StructuralConnectionHandlerType from list_structural_connection_types.")
    },"member_ids");
    public void Preflight(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        var doc=NativeToolUtil.Doc(app);var ids=NativeToolUtil.Ids(input["member_ids"],10);
        if(ids.Count<2)throw new ToolInputException("A connection needs at least two members.");
        foreach(var id in ids)
        {
            var e=NativeToolUtil.Element(doc,id.Value);
            if(e is not FamilyInstance||e.Category?.Id.Value is not ((long)BuiltInCategory.OST_StructuralColumns) and not ((long)BuiltInCategory.OST_StructuralFraming))throw new ToolInputException("Member must be a native structural column or beam.");
        }
        if(input.TryGetValue("connection_type_id",out var type)&&NativeToolUtil.Element(doc,type.GetInt64()) is not StructuralConnectionHandlerType)throw new ToolInputException("connection_type_id is not a native connection type.");
    }
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        Preflight(input,app);var doc=NativeToolUtil.Doc(app);var ids=NativeToolUtil.Ids(input["member_ids"],10);
        var detailed=input.TryGetValue("connection_type_id",out var type);
        var connection=detailed?StructuralConnectionHandler.Create(doc,ids,new ElementId(type.GetInt64())):StructuralConnectionHandler.CreateGenericConnection(doc,ids);
        doc.Regenerate();
        return Services.Json.Serialize(new { id=connection.Id.Value,representation="native StructuralConnectionHandler",connected_element_ids=connection.GetConnectedElementIds().Select(i=>i.Value).ToArray(),
            requested_detailed_type=detailed,warnings=detailed?Array.Empty<string>():new[]{"Generic logical connection: no plate/bolt fabrication geometry was generated."} });
    }
}

public sealed class ListStructuralConnectionTypes : IRevitTool
{
    public string Name=>"list_structural_connection_types";
    public string Description=>"List loaded native structural connection types by ID/name. An empty catalog means no detailed connection package is loaded; generic logical links remain available. Does not load commercial fabrication packages.";
    public bool RequiresTransaction=>false;
    public InputSchema InputSchema=>NativeToolUtil.Schema(new());
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)=>Services.Json.Serialize(new
    { types=new FilteredElementCollector(NativeToolUtil.Doc(app)).OfClass(typeof(StructuralConnectionHandlerType)).Cast<StructuralConnectionHandlerType>().Select(t=>new {id=t.Id.Value,name=t.Name}).ToArray() });
}
