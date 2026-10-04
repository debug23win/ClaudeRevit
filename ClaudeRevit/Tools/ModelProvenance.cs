using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

internal static class ModelProvenance
{
    private static readonly Guid Id = new("e9a3e6ee-2ec0-4a98-bdc6-42ea38dcb17c");
    private static Schema Schema()
    {
        var schema = Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(Id);
        if (schema != null) return schema;
        var builder = new SchemaBuilder(Id); builder.SetSchemaName("ClaudeRevitModelProvenance");
        builder.SetReadAccessLevel(AccessLevel.Public); builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField("Json",typeof(string)); return builder.Finish();
    }
    public static string Representation(Element e) => e is DirectShape ? "DirectShape geometry" : e is FamilyInstance ? "native family instance" : e is GenericForm ? "native family form" : "native " + e.GetType().Name;
    public static JsonElement? Read(Element e)
    {
        var schema = Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(Id); if (schema == null) return null;
        var entity = e.GetEntity(schema); if (!entity.IsValid()) return null;
        using var json = JsonDocument.Parse(entity.Get<string>(schema.GetField("Json"))); return json.RootElement.Clone();
    }
    public static void Write(Element e, object value)
    {
        var text = Services.Json.Serialize(value); if (text.Length>32_000) throw new ToolInputException("Provenance exceeds 32,000 characters.");
        var schema = Schema(); var entity = new Entity(schema); entity.Set(schema.GetField("Json"),text); e.SetEntity(entity);
    }
    public static object Metadata(IReadOnlyDictionary<string,JsonElement> input, string generator) => new
    {
        generator, parameters = input.Where(p=>p.Key is not ("provenance" or "element_id" or "preview")).ToDictionary(p=>p.Key,p=>p.Value),
        provenance = input.TryGetValue("provenance",out var p) ? (object)p : new { status="unknown", sources=Array.Empty<string>(), dimensions=Array.Empty<object>() }
    };
}

public sealed class SetModelProvenance : IRevitTool
{
    public string Name => "set_model_provenance";
    public string Description => "Store source references, dimension certainty and linked element IDs on native BIM/DirectShape via ExtensibleStorage. metadata may contain sources:[strings], dimensions:[{name,value,unit,status:measured|assumed|unknown}], related_element_ids:[IDs], notes. Claims are user/agent annotations, not independently verified. Does not alter geometry.";
    public bool RequiresTransaction => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new() { ["element_ids"]=NativeToolUtil.Array("integer","Target IDs."),["metadata"]=NativeToolUtil.Any("Provenance JSON object, max 32,000 characters.") },"element_ids","metadata");
    public void Preflight(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    { if (input["metadata"].ValueKind!=JsonValueKind.Object || input["metadata"].GetRawText().Length>32_000) throw new ToolInputException("metadata must be an object <=32,000 characters."); }
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        Preflight(input,app); var doc=NativeToolUtil.Doc(app); var ids=NativeToolUtil.Ids(input["element_ids"]);
        foreach (var id in ids)
        {
            ToolContext.ThrowIfCancelled();var element=NativeToolUtil.Element(doc,id.Value);
            var existing=ModelProvenance.Read(element);
            if(existing is { } saved && saved.TryGetProperty("generator",out _))
            {
                var merged=System.Text.Json.Nodes.JsonNode.Parse(saved.GetRawText())!.AsObject();
                merged["provenance"]=System.Text.Json.Nodes.JsonNode.Parse(input["metadata"].GetRawText());
                ModelProvenance.Write(element,merged);
            }
            else ModelProvenance.Write(element,input["metadata"]);
        }
        return Services.Json.Serialize(new { updated_ids=ids.Select(i=>i.Value).ToArray() });
    }
}

public sealed class AuditModelProvenance : IRevitTool
{
    public string Name => "audit_model_provenance";
    public string Description => "Inspect BIM representation and source/assumption annotations. Supply IDs or inspect up to limit instances (default 200, max 2000); missing metadata is unknown. Returns actual category/class, DirectShape versus native BIM, and stored provenance. An annotation is not proof of geometry or source accuracy.";
    public bool RequiresTransaction => false;
    public InputSchema InputSchema => NativeToolUtil.Schema(new() { ["element_ids"]=NativeToolUtil.Array("integer","Optional IDs."),["limit"]=NativeToolUtil.Field("integer","1..2000, default 200.") });
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        var doc=NativeToolUtil.Doc(app);var limit=Math.Clamp(ToolInput.OptionalInt(input,"limit")??200,1,2000);
        var elements=input.TryGetValue("element_ids",out var ids) ? NativeToolUtil.Ids(ids).Select(id=>NativeToolUtil.Element(doc,id.Value)) : new FilteredElementCollector(doc).WhereElementIsNotElementType().Cast<Element>();
        var rows=elements.Take(limit+1).ToArray();return Services.Json.Serialize(new { truncated=rows.Length>limit, elements=rows.Take(limit).Select(e=>new { id=e.Id.Value,category=e.Category?.Name,representation=ModelProvenance.Representation(e),metadata=ModelProvenance.Read(e),certainty=ModelProvenance.Read(e)==null?"unknown":"annotated, unverified" }).ToArray() });
    }
}
