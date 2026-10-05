using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

internal sealed class NodeBinding
{
    public string UniqueId { get; set; } = "";
    public bool Owned { get; set; }
    public string Signature { get; set; } = "";
}
internal sealed class NodeRecord
{
    public string Key { get; set; } = "";
    public int Revision { get; set; }
    public bool AutoUpdate { get; set; }
    public bool NeedsRefresh { get; set; }
    public string? LastUpdateError { get; set; }
    public ConnectionSpec Spec { get; set; } = new();
    public Dictionary<string,NodeBinding> Bindings { get; set; } = new();
}
internal static class ConnectionNodes
{
    private static readonly Guid SchemaId=new("90311745-5cdd-4fa1-8d83-d6402fe2b58d");
    private static Schema Schema()
    {
        var s=Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaId);if(s!=null)return s;
        var b=new SchemaBuilder(SchemaId);b.SetSchemaName("ClaudeRevitConnectionNodeV1");b.SetReadAccessLevel(AccessLevel.Public);b.SetWriteAccessLevel(AccessLevel.Public);b.AddSimpleField("Json",typeof(string));return b.Finish();
    }
    public static IEnumerable<(DataStorage Storage,NodeRecord Record)> All(Document doc)
    {
        var schema=Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaId);if(schema==null)yield break;
        foreach(var data in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>())
        {var e=data.GetEntity(schema);if(!e.IsValid())continue;var r=JsonSerializer.Deserialize<NodeRecord>(e.Get<string>(schema.GetField("Json")),ConnectionSpec.Options);if(r!=null)yield return(data,r);}
    }
    public static (DataStorage? Storage,NodeRecord? Record) Find(Document doc,string key)
    { var rows=All(doc).Where(p=>p.Record.Key==key).Take(2).ToArray();if(rows.Length>1)throw new ToolInputException("Duplicate saved node key; inspect copied DataStorage before updating.");return rows.Length==0?(null,null):(rows[0].Storage,rows[0].Record); }
    public static void Save(DataStorage storage,NodeRecord record)
    {var text=JsonSerializer.Serialize(record,ConnectionSpec.Options);if(text.Length>1_000_000)throw new ToolInputException("Node manifest is too large.");var schema=Schema();var e=new Entity(schema);e.Set(schema.GetField("Json"),text);storage.SetEntity(e);}
    public static Element Resolve(Document doc,NodeBinding b)=>doc.GetElement(b.UniqueId)??throw new ToolInputException("A bound node element was deleted. Supply its replacement element_id or explicitly remove that part.");
    public static Transform Frame(Element e)
    {
        if(e is FamilyInstance f)return f.GetTransform();
        var t=Transform.Identity;
        if(e.Location is LocationPoint p)t.Origin=p.Point;
        else if(e.Location is LocationCurve c){t.Origin=c.Curve.GetEndPoint(0);t.BasisX=(c.Curve.GetEndPoint(1)-t.Origin).Normalize();var z=Math.Abs(t.BasisX.DotProduct(XYZ.BasisZ))>.99?XYZ.BasisY:XYZ.BasisZ;t.BasisY=z.CrossProduct(t.BasisX).Normalize();t.BasisZ=t.BasisX.CrossProduct(t.BasisY);}
        return t;
    }
    public static string Signature(Element e)
    {
        var t=Frame(e);var bb=e.get_BoundingBox(null);
        return Services.Json.Serialize(new { type=e.GetTypeId().Value,origin=NativeToolUtil.Mm(t.Origin),x=NativeToolUtil.Vector(t.BasisX),y=NativeToolUtil.Vector(t.BasisY),
            bbox=bb==null?null:new { min=NativeToolUtil.Vector(bb.Min),max=NativeToolUtil.Vector(bb.Max) },
            type_parameters=e.Document.GetElement(e.GetTypeId())?.Parameters.Cast<Parameter>().Where(p=>p.HasValue&&p.StorageType==StorageType.Double).OrderBy(p=>p.Id.Value).Select(p=>new {id=p.Id.Value,value=p.AsDouble()}).ToArray(),
            parameters=e.Parameters.Cast<Parameter>().Where(p=>p.HasValue&&!p.Definition.Name.StartsWith("CR_SPDS_",StringComparison.Ordinal)).OrderBy(p=>p.Id.Value).Select(p=>new {id=p.Id.Value,value=p.StorageType switch {StorageType.Double=>p.AsDouble().ToString("R",System.Globalization.CultureInfo.InvariantCulture),StorageType.Integer=>p.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture),StorageType.String=>p.AsString(),StorageType.ElementId=>p.AsElementId().Value.ToString(System.Globalization.CultureInfo.InvariantCulture),_=>null}}).ToArray() });
    }
    public static PlanarFace Face(Document doc,string reference,Element? expected=null)
    {var r=Reference.ParseFromStableRepresentation(doc,reference);var host=NativeToolUtil.Element(doc,r.ElementId.Value);if(expected!=null&&host.Id!=expected.Id)throw new ToolInputException("face_reference belongs to a different host.");return host.GetGeometryObjectFromReference(r) as PlanarFace??throw new ToolInputException("A stable planar face reference is required.");}
    public static List<Solid> Solids(Element e)
    {
        var result=new List<Solid>();int count=0;
        void Walk(GeometryElement? g,int depth)
        {if(g==null)return;if(depth>12)throw new ToolInputException("Geometry nesting exceeds 12 levels.");foreach(var o in g){ToolContext.ThrowIfCancelled();if(++count>50000)throw new ToolInputException("Geometry inspection limit exceeded.");if(o is Solid s&&s.Volume>1e-12)result.Add(s);else if(o is GeometryInstance i)Walk(i.GetInstanceGeometry(),depth+1);}}
        Walk(e.get_Geometry(new Options { DetailLevel=ViewDetailLevel.Fine }),0);return result;
    }
    public static double[][] Corners(Element e)
    {var b=e.get_BoundingBox(null)??throw new ToolInputException("Element has no bounding box.");return (from x in new[]{b.Min.X,b.Max.X} from y in new[]{b.Min.Y,b.Max.Y} from z in new[]{b.Min.Z,b.Max.Z} select NativeToolUtil.Mm(b.Transform.OfPoint(new XYZ(x,y,z)))).ToArray();}
    public static void Position(Document doc,Element e,XYZ point,XYZ? x,XYZ? y)
    {
        if(e.Location is not LocationPoint)throw new ToolInputException("Managed placement requires a point-based instance; use beam/column tools for curve-driven members.");
        if(e.Pinned)throw new ToolInputException("Unpin the part before changing its placement.");
        var frame=Frame(e);ElementTransformUtils.MoveElement(doc,e.Id,point-frame.Origin);doc.Regenerate();
        if(x==null)return;
        if(e is FamilyInstance fi&&fi.Mirrored)throw new ToolInputException("Mirrored instances require explicit unmirroring; a rotation cannot fix a reflection.");
        void Rotate(XYZ axis,double angle){if(Math.Abs(angle)>1e-9){ElementTransformUtils.RotateElement(doc,e.Id,Line.CreateBound(point,point+axis),angle);doc.Regenerate();}}
        frame=Frame(e);var angle=frame.BasisX.AngleTo(x);var axis=frame.BasisX.CrossProduct(x);
        if(angle>1e-9)Rotate(axis.GetLength()<1e-9?frame.BasisY:axis.Normalize(),angle);
        frame=Frame(e);var twist=Math.Atan2(x.DotProduct(frame.BasisY.CrossProduct(y!)),frame.BasisY.DotProduct(y!));Rotate(x,twist);
        frame=Frame(e);if(frame.BasisX.AngleTo(x)>1e-6||frame.BasisY.AngleTo(y!)>1e-6)throw new ToolInputException("Revit constraints prevented the requested orientation.");
    }
    public static void Parameters(Element e,IEnumerable<ConnectionParameter> values,IReadOnlyDictionary<string,Element>? elements=null)
    {
        foreach(var v in values)
        {
            var value=v.Value;
            if(v.ValueFrom is { } from)
            {
                var source=elements?.GetValueOrDefault(from.PartKey)??throw new ToolInputException("Dependency source is unavailable.");
                if(from.Scope=="type")source=source.Document.GetElement(source.GetTypeId())??throw new ToolInputException("Dependency has no type.");
                var driver=NativeToolUtil.Parameter(source,from.Name??"",from.Guid??"");
                if(driver?.StorageType!=StorageType.Double||!driver.HasValue||driver.Definition.GetDataType()!=SpecTypeId.Length)throw new ToolInputException("Dependent dimension source must be a real Length parameter.");
                value=JsonSerializer.SerializeToElement(driver.AsDouble()*Units.MmPerFoot*from.Scale+from.OffsetMm);
            }
            var p=NativeToolUtil.Parameter(e,v.Name??"",v.Guid??"")??throw new ToolInputException("Parameter missing from part.");
            if(p.IsReadOnly)throw new ToolInputException($"{p.Definition.Name} is read-only.");
            if(v.Unit=="mm"&&(p.StorageType!=StorageType.Double||p.Definition.GetDataType()!=SpecTypeId.Length))throw new ToolInputException("mm only applies to length parameters.");
            double number=p.StorageType==StorageType.Double?value.GetDouble()/(v.Unit=="mm"?Units.MmPerFoot:1):0;
            int integer=p.StorageType==StorageType.Integer?value.ValueKind==JsonValueKind.True?1:value.ValueKind==JsonValueKind.False?0:value.GetInt32():0;
            if(p.StorageType==StorageType.Double&&!double.IsFinite(number))throw new ToolInputException("Nonfinite dependent dimension.");
            var ok=p.StorageType switch
            {StorageType.Double=>p.HasValue&&Math.Abs(p.AsDouble()-number)<1e-10||p.Set(number),StorageType.Integer=>p.HasValue&&p.AsInteger()==integer||p.Set(integer),StorageType.String=>p.AsString()==value.GetString()||p.Set(value.GetString()??""),StorageType.ElementId=>p.AsElementId().Value==value.GetInt64()||p.Set(new ElementId(value.GetInt64())),_=>false};
            if(!ok)throw new ToolInputException($"Revit rejected {p.Definition.Name}.");
        }
    }
}

public sealed class UpsertConnectionNode:IRevitTool
{
    public string Name=>"upsert_connection_node";
    public string Description=>"Create/update/refresh a saved connection node by stable node_key and part keys. Atomic one Undo, default preview=true. Spec contains parts, rules and optional evidence; omit spec to refresh saved relative/face placements after member edits. Creates only loaded unhosted point family instances, reuses native members by ID. Native steel connections should be preferred when compatible types exist. Missing parts require explicit remove_missing_parts=true. Validate representative node before replication. See get_tool_source or docs/structural-workflows.md for limits.";
    public bool RequiresTransaction=>false;
    public bool RequiresNoTurnGroup=>true;
    public bool MutatesWithoutTransaction=>true;
    public InputSchema InputSchema=>NativeToolUtil.Schema(new()
    {
        ["node_key"]=NativeToolUtil.Field("string","Stable document-local node key, max 128 chars."),
        ["spec"]=NativeToolUtil.Any("{parts:[{key,role:member|plate|splice|bolt|hole|cope|rib|other,element_id?,family_type_id?,level_id?,point_mm?,axis_x?,axis_y?,relative_to?,face_reference?,offset_mm?,parameters:[{name? OR guid?,value? OR value_from:{part_key,name? OR guid?,scope:instance|type,scale?:1,offset_mm?:0},unit:internal|mm}],cut_targets:[keys]}],rules:[{kind:clash|contact|bolt,a,b,tolerance_mm?:1,max_volume_mm3?:1,face_reference?:stable face on b for contact,expected_offset_mm?:0,local_axis_a?:[0,0,1],local_axis_b?:[0,0,1],bolt_diameter_parameter?,hole_diameter_parameter?,grip_parameter?,stack_parts?:[keys],forbidden_planes?:[{origin_mm,normal,half_width_mm}]}],calculation_evidence?,documentation_evidence?}. Use full spec on updates; omit to refresh. Diameter/grip names must be actual Length parameters."),
        ["preview"]=NativeToolUtil.Field("boolean","Default true: roll back and return a change report; preview IDs are temporary."),
        ["expected_revision"]=NativeToolUtil.Field("integer","Optional optimistic revision check, 0 for a new node."),
        ["remove_missing_parts"]=NativeToolUtil.Field("boolean","Default false. Allow removal of omitted managed parts. Referenced members are only unbound."),
        ["require_valid"]=NativeToolUtil.Field("boolean","Default true: failed declared checks prevent apply. Incomplete checks are reported separately.")
        ,["auto_update"]=NativeToolUtil.Field("boolean","Default true for new nodes; existing nodes preserve their setting. Follows saved relative placements and value_from Length dependencies after member/type edits. Failures mark needs_refresh and preserve dependent geometry.")
    },"node_key");
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        var doc=NativeToolUtil.Doc(app);var key=ToolInput.RequiredString(input,"node_key");if(key.Length>128)throw new ToolInputException("node_key exceeds 128 chars.");
        var (storage,old)=ConnectionNodes.Find(doc,key);
        if(old==null&&ConnectionNodes.All(doc).Take(100).Count()>=100)throw new ToolInputException("At most 100 managed nodes per document.");
        if(input.TryGetValue("expected_revision",out var rev)&&rev.GetInt32()!=(old?.Revision??0))throw new ToolInputException("Node revision changed; reload before updating.");
        var spec=input.TryGetValue("spec",out var raw)?raw.Deserialize<ConnectionSpec>(ConnectionSpec.Options)??throw new ToolInputException("Empty spec."):old?.Spec??throw new ToolInputException("New nodes need a spec.");
        spec=JsonSerializer.Deserialize<ConnectionSpec>(JsonSerializer.Serialize(spec,ConnectionSpec.Options),ConnectionSpec.Options)!;
        var ordered=spec.OrderedParts();var preview=NativeToolUtil.Preview(input);var remove=ToolInput.Flag(input,"remove_missing_parts");
        var omitted=old?.Bindings.Keys.Except(spec.Parts.Select(p=>p.Key)).ToArray()??[];
        if(omitted.Length>0&&!remove)throw new ToolInputException("Omitted parts require remove_missing_parts=true.");
        var record=new NodeRecord {Key=key,Revision=(old?.Revision??0)+1,Spec=spec,AutoUpdate=input.TryGetValue("auto_update",out var auto)?auto.GetBoolean():old?.AutoUpdate??true};
        var created=new List<long>();var updated=new List<long>();var deleted=new List<long>();var unbound=new List<string>();var parameterChanges=new List<object>();
        Dictionary<string,Element> elements=new();
        object? validation=null;
        var (result,warnings)=NativeToolUtil.Commit(doc,"Claude: connection node "+key,preview,()=>
        {
            var other=ConnectionNodes.All(doc).Where(n=>n.Record.Key!=key).SelectMany(n=>n.Record.Bindings.Values).Select(b=>b.UniqueId).ToHashSet();
            // Remove obsolete cut relationships before type swaps/deletion. A retained
            // hole part must not continue cutting targets removed from the new manifest.
            if(old!=null)foreach(var previous in old.Spec.Parts)foreach(var target in previous.CutTargets)
            {
                if(spec.Parts.Any(p=>p.Key==previous.Key&&p.CutTargets.Contains(target)))continue;
                var cutter=old.Bindings.TryGetValue(previous.Key,out var cb)?doc.GetElement(cb.UniqueId) as FamilyInstance:null;
                var host=old.Bindings.TryGetValue(target,out var hb)?doc.GetElement(hb.UniqueId):null;
                if(cutter!=null&&host!=null&&InstanceVoidCutUtils.InstanceVoidCutExists(host,cutter))InstanceVoidCutUtils.RemoveInstanceVoidCut(doc,host,cutter);
            }
            foreach(var p in ordered)
            {
                ToolContext.ReportProgress(elements.Count,ordered.Count,"connection node parts");
                var binding=old?.Bindings.GetValueOrDefault(p.Key);Element? e=null;bool owned=false;
                if(binding!=null){e=doc.GetElement(binding.UniqueId);owned=binding.Owned;}
                if(p.ElementId!=null)
                {var requested=NativeToolUtil.Element(doc,p.ElementId.Value);if(e!=null&&requested.UniqueId!=e.UniqueId&&owned)throw new ToolInputException("Cannot replace an owned binding with an unrelated ID; remove it explicitly first.");if(e==null||requested.UniqueId!=e.UniqueId)owned=false;e=requested;}
                if(e==null)
                {
                    if(binding!=null)throw new ToolInputException("A managed part is missing; provide a replacement element_id or remove it explicitly.");
                    var type=NativeToolUtil.Element(doc,p.FamilyTypeId??0) as FamilySymbol??throw new ToolInputException("family_type_id must be a loaded FamilySymbol.");
                    if(type.Family.FamilyPlacementType!=FamilyPlacementType.OneLevelBased)throw new ToolInputException("Only unhosted point-based families can be generated here. Use native beam/column/hosted placement tools then bind their IDs.");
                    if(!type.IsActive)type.Activate();doc.Regenerate();
                    var level=p.LevelId!=null?NativeToolUtil.Element(doc,p.LevelId.Value) as Level:null;
                    e=level==null?doc.Create.NewFamilyInstance(XYZ.Zero,type,StructuralType.NonStructural):doc.Create.NewFamilyInstance(XYZ.Zero,type,level,StructuralType.NonStructural);
                    owned=true;created.Add(e.Id.Value);
                }
                if(e is ElementType)throw new ToolInputException("Bind instances, not type IDs.");
                if(other.Contains(e.UniqueId)&& (p.PointMm!=null||p.RelativeTo!=null||p.FaceReference!=null||p.FamilyTypeId!=null||p.Parameters.Count>0))throw new ToolInputException("This part is shared with another node; update its owning node first.");
                if(elements.Values.Any(x=>x.Id==e.Id))throw new ToolInputException("The same element cannot occupy two part keys.");
                if(p.FamilyTypeId!=null&&e.GetTypeId().Value!=p.FamilyTypeId.Value)
                {var changed=TypeChangePreservation.Change(e,new ElementId(p.FamilyTypeId.Value),true);e=changed.Element;parameterChanges.Add(new {part=p.Key,changes=changed.Changes});}
                XYZ? point=p.PointMm!=null?new XYZ(p.PointMm[0],p.PointMm[1],p.PointMm[2])/Units.MmPerFoot:null;
                if(p.RelativeTo!=null){var parent=ConnectionNodes.Frame(elements[p.RelativeTo]);var o=p.OffsetMm??new double[3];point=parent.OfPoint(new XYZ(o[0],o[1],o[2])/Units.MmPerFoot);}
                if(p.FaceReference!=null){var face=ConnectionNodes.Face(doc,p.FaceReference);var o=p.OffsetMm??new double[3];point=face.Origin+(face.XVector*o[0]+face.YVector*o[1]+face.FaceNormal*o[2])/Units.MmPerFoot;}
                if(point!=null)ConnectionNodes.Position(doc,e,point,p.AxisX==null?null:new XYZ(p.AxisX[0],p.AxisX[1],p.AxisX[2]).Normalize(),p.AxisY==null?null:new XYZ(p.AxisY[0],p.AxisY[1],p.AxisY[2]).Normalize());
                else if(p.AxisX!=null)ConnectionNodes.Position(doc,e,ConnectionNodes.Frame(e).Origin,new XYZ(p.AxisX[0],p.AxisX[1],p.AxisX[2]).Normalize(),new XYZ(p.AxisY![0],p.AxisY[1],p.AxisY[2]).Normalize());
                ConnectionNodes.Parameters(e,p.Parameters,elements);elements[p.Key]=e;record.Bindings[p.Key]=new(){UniqueId=e.UniqueId,Owned=owned};
                if(!created.Contains(e.Id.Value))updated.Add(e.Id.Value);
            }
            doc.Regenerate();
            foreach(var p in spec.Parts)foreach(var target in p.CutTargets)
            {var cut=elements[p.Key] as FamilyInstance??throw new ToolInputException("Cutters must be FamilyInstances.");var host=elements[target];if(!InstanceVoidCutUtils.CanBeCutWithVoid(host)||!InstanceVoidCutUtils.IsVoidInstanceCuttingElement(cut))throw new ToolInputException("Requested host/cutter does not support unattached void cuts.");if(!InstanceVoidCutUtils.InstanceVoidCutExists(host,cut))InstanceVoidCutUtils.AddInstanceVoidCut(doc,host,cut);}
            var removal=omitted.Select(k=>old!.Bindings[k]).Where(b=>b.Owned).Select(b=>doc.GetElement(b.UniqueId)).Where(e=>e!=null).Cast<Element>().ToArray();
            var allowed=removal.Select(e=>e.Id.Value).ToHashSet();
            foreach(var e in removal)
            {if(other.Contains(e.UniqueId))throw new ToolInputException("Removed part belongs to another node.");if(doc.GetElement(e.Id)==null)continue;var ids=doc.Delete(e.Id);if(ids.Any(i=>!allowed.Contains(i.Value)))throw new ToolInputException("Removal would delete unmanaged dependents; preview and resolve these first.");deleted.AddRange(ids.Select(i=>i.Value));}
            unbound.AddRange(omitted.Where(k=>!old!.Bindings[k].Owned));
            doc.Regenerate();
            // Refresh IDs in a copied spec so type replacement IDs do not invalidate later refresh.
            foreach(var p in spec.Parts){if(!record.Bindings[p.Key].Owned)p.ElementId=elements[p.Key].Id.Value;record.Bindings[p.Key].Signature=ConnectionNodes.Signature(elements[p.Key]);}
            storage??=DataStorage.Create(doc);ConnectionNodes.Save(storage,record);
            return new { node_key=key,revision=record.Revision,storage_id=preview?(long?)null:storage.Id.Value,created_ids=created.ToArray(),updated_ids=updated.ToArray(),deleted_ids=deleted.ToArray(),unbound_keys=unbound.ToArray(),parameter_changes=parameterChanges,bindings=elements.ToDictionary(p=>p.Key,p=>p.Value.Id.Value) };
        },()=>
        {
            var check=ConnectionNodeValidation.Check(doc,record);validation=check;
            if((!input.TryGetValue("require_valid",out var v)||v.GetBoolean())&&check.Failed>0)throw new ToolInputException("Node geometry checks failed; entire update rolled back. Use preview with require_valid=false to inspect the report.");
        });
        return Services.Json.Serialize(new {preview,preview_ids_are_temporary=preview,result,validation,warnings,auto_update=record.AutoUpdate,refresh="Relative placements and declared value_from dimensions follow source edits through the required updater. Missing/cyclic/invalid dependencies are flagged; inspect get_connection_node before manual refresh."});
    }
}

public sealed class GetConnectionNode:IRevitTool
{
    public string Name=>"get_connection_node";
    public string Description=>"Read persisted connection node manifests, live part IDs and external edit/deletion flags. Omit node_key to list node keys/revisions. Storage is in the RVT; keys are document-local. Calculation/documentation evidence is an annotation, not verification.";
    public bool RequiresTransaction=>false;
    public InputSchema InputSchema=>NativeToolUtil.Schema(new(){["node_key"]=NativeToolUtil.Field("string","Optional node key.")});
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        var doc=NativeToolUtil.Doc(app);var key=NativeToolUtil.Text(input,"node_key");
        if(key.Length==0)return Services.Json.Serialize(new {nodes=ConnectionNodes.All(doc).Take(1000).Select(p=>new {key=p.Record.Key,revision=p.Record.Revision,part_count=p.Record.Bindings.Count}).ToArray(),limit=1000});
        var (_,r)=ConnectionNodes.Find(doc,key);if(r==null)throw new ToolInputException("Node not found.");
        var parts=r.Bindings.Select(p=>{var e=doc.GetElement(p.Value.UniqueId);return new {key=p.Key,id=e?.Id.Value,owned=p.Value.Owned,missing=e==null,externally_changed=e!=null&&ConnectionNodes.Signature(e)!=p.Value.Signature};}).ToArray();
        return Services.Json.Serialize(new {node_key=key,revision=r.Revision,auto_update=r.AutoUpdate,last_update_error=r.LastUpdateError,spec=JsonSerializer.SerializeToElement(r.Spec,ConnectionSpec.Options),parts,needs_refresh=r.NeedsRefresh||parts.Any(p=>p.missing||p.externally_changed)});
    }
}

public sealed class PlanTrussLayout:IRevitTool
{
    public string Name=>"plan_truss_layout";
    public string Description=>"Read-only global truss/bridge layout: consistent node/diagonal parity across spans, two side frames, upper/lower cross bracing, modular staggered chord splices and explicit end remainders. Returns mm points/member axes, no Revit edits or structural sizing. Use native structural beam/column types and steel connections first; custom node specs only for unavailable connections.";
    public bool RequiresTransaction=>false;
    public InputSchema InputSchema=>NativeToolUtil.Schema(new(){["spans_mm"]=NativeToolUtil.Array("number","1..20 spans."),["width_mm"]=NativeToolUtil.Field("number","Distance between side frame axes, not automatically clear walkway width."),["height_mm"]=NativeToolUtil.Field("number","Truss axis height; not a calculated optimum."),["panel_mm"]=NativeToolUtil.Field("number","Global panel length."),["module_mm"]=NativeToolUtil.Field("number","Default 11700."),["module_divisions"]=NativeToolUtil.Field("integer","Default 1."),["stagger_mm"]=NativeToolUtil.Field("number","Default half module subdivision." )},"spans_mm","width_mm","height_mm","panel_mm");
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {var module=ToolInput.OptionalDouble(input,"module_mm")??11700;var div=ToolInput.OptionalInt(input,"module_divisions")??1;var plan=TrussLayout.Build(input["spans_mm"].EnumerateArray().Select(x=>x.GetDouble()).ToArray(),input["width_mm"].GetDouble(),input["height_mm"].GetDouble(),input["panel_mm"].GetDouble(),module,div,ToolInput.OptionalDouble(input,"stagger_mm")??module/div/2);return JsonSerializer.Serialize(new {plan,units="mm",limitations="Layout only. End segments are explicit remainders. Sections, loads and joint design require separate engineering verification."},ConnectionSpec.Options);}
}
