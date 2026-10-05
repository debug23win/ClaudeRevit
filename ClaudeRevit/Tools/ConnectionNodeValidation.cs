using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

internal sealed record NodeValidation(int Passed,int Failed,int Incomplete,object Readiness,List<object> Checks);
internal static class ConnectionNodeValidation
{
    public static NodeValidation Check(Document doc,NodeRecord node)
    {
        var checks=new List<object>();int passed=0,failed=0,incomplete=0;
        var elements=new Dictionary<string,Element>();var cache=new Dictionary<string,List<Solid>>();
        void Result(string kind,string? a,string? b,string state,object detail)
        {if(state=="passed")passed++;else if(state=="failed")failed++;else incomplete++;checks.Add(new {kind,a,b,status=state,detail});}
        foreach(var pair in node.Bindings)
        {
            ToolContext.ThrowIfCancelled();var e=doc.GetElement(pair.Value.UniqueId);
            if(e==null){Result("existence",pair.Key,null,"failed",new {error="Bound element was deleted."});continue;}
            elements[pair.Key]=e;
            if(pair.Value.Signature.Length>0&&ConnectionNodes.Signature(e)!=pair.Value.Signature)Result("external_edit",pair.Key,null,"incomplete",new {action="Refresh node before asserting its interfaces."});
        }
        List<Solid> Solids(string key) {if(!cache.TryGetValue(key,out var s)){s=ConnectionNodes.Solids(elements[key]);cache[key]=s;}return s;}
        double? Length(Element e,string? name)
        {if(string.IsNullOrWhiteSpace(name))return null;var p=NativeToolUtil.Parameter(e,name,"");return p?.StorageType==StorageType.Double&&p.Definition.GetDataType()==SpecTypeId.Length?p.AsDouble()*Units.MmPerFoot:null;}
        foreach(var p in node.Spec.Parts)foreach(var target in p.CutTargets)
        {
            if(!elements.ContainsKey(p.Key)||!elements.ContainsKey(target))continue;
            bool registered=elements[p.Key] is FamilyInstance cutter&&InstanceVoidCutUtils.InstanceVoidCutExists(elements[target],cutter);
            var rule=node.Spec.Rules.FirstOrDefault(r=>r.Kind=="bolt"&&r.B==p.Key&&r.StackParts.Contains(target));
            BoreEvidence? bore=null;
            if(rule!=null&&Length(elements[p.Key],rule.HoleDiameterParameter) is { } diameter)
            {var frame=ConnectionNodes.Frame(elements[p.Key]);var axis=frame.OfVector(new XYZ(rule.LocalAxisB[0],rule.LocalAxisB[1],rule.LocalAxisB[2])).Normalize();bore=ConnectionOpeningGeometry.Inspect(elements[target],frame.Origin,axis,diameter,rule.ToleranceMm);}
            Result("void_cut",p.Key,target,!registered||bore?.Verified==false?"failed":bore?.Verified==true?"passed":"incomplete",new {relationship_registered=registered,geometry_verified=bore?.Verified,bore,reason="The relationship alone is insufficient; a declared bolt-axis/diameter check inspects an actual cylindrical through bore."});
        }
        foreach(var r in node.Spec.Rules)
        {
            ToolContext.ReportProgress(checks.Count,node.Spec.Rules.Count+node.Bindings.Count,"checking connection node");
            if(!elements.TryGetValue(r.A,out var a)||!elements.TryGetValue(r.B,out var b))continue;
            try
            {
                switch(r.Kind)
                {
                    case "clash":
                    {
                        var sa=Solids(r.A);var sb=Solids(r.B);if(sa.Count==0||sb.Count==0){Result("clash",r.A,r.B,"incomplete",new {reason="No inspectable solid geometry."});break;}
                        double volume=0;foreach(var x in sa)foreach(var y in sb){ToolContext.ThrowIfCancelled();using var hit=BooleanOperationsUtils.ExecuteBooleanOperation(x,y,BooleanOperationsType.Intersect);volume+=hit.Volume*Math.Pow(Units.MmPerFoot,3);}
                        Result("clash",r.A,r.B,volume<=r.MaxVolumeMm3?"passed":"failed",new {intersection_volume_mm3=volume,maximum_mm3=r.MaxVolumeMm3});break;
                    }
                    case "contact":
                    {
                        var face=ConnectionNodes.Face(doc,r.FaceReference!,b);var solids=Solids(r.A);
                        if(solids.Count==0){Result("contact",r.A,r.B,"incomplete",new {reason="No inspectable solid geometry."});break;}
                        // Mesh vertices are in world coordinates from GetInstanceGeometry.
                        double min=double.PositiveInfinity;bool overlapsFace=false;
                        foreach(var solid in solids)foreach(Face f in solid.Faces)
                        {var mesh=f.Triangulate();foreach(var point in mesh.Vertices){ToolContext.ThrowIfCancelled();var projected=face.Project(point);if(projected!=null&&face.IsInside(projected.UVPoint)){overlapsFace=true;min=Math.Min(min,(point-face.Origin).DotProduct(face.FaceNormal)*Units.MmPerFoot);}}}
                        Result("contact",r.A,r.B,overlapsFace&&Math.Abs(min-r.ExpectedOffsetMm)<=r.ToleranceMm?"passed":"failed",new {nearest_projected_vertex_offset_mm=double.IsFinite(min)?(double?)min:null,expected_offset_mm=r.ExpectedOffsetMm,tolerance_mm=r.ToleranceMm,coverage="Finite planar face and tessellated solid vertices; curved contact is not supported."});break;
                    }
                    case "bolt":
                    {
                        if(a is not FamilyInstance||b is not FamilyInstance){Result("bolt",r.A,r.B,"failed",new {reason="Bolt/hole axes require family instances."});break;}
                        var fa=ConnectionNodes.Frame(a);var fb=ConnectionNodes.Frame(b);
                        XYZ Axis(Transform f,double[] v)=>f.OfVector(new XYZ(v[0],v[1],v[2])).Normalize();
                        var axis=Axis(fa,r.LocalAxisA);var origin=NativeToolUtil.Mm(fa.Origin);
                        var metrics=ConnectionMath.Axes(origin,NativeToolUtil.Vector(axis),NativeToolUtil.Mm(fb.Origin),NativeToolUtil.Vector(Axis(fb,r.LocalAxisB)));
                        bool ok=metrics.DistanceMm<=r.ToleranceMm&&metrics.AngleDeg<=.1;
                        var diameter=Length(a,r.BoltDiameterParameter);var hole=Length(b,r.HoleDiameterParameter);var grip=Length(a,r.GripParameter);
                        if(diameter.HasValue&&hole.HasValue)ok&=diameter.Value>0&&hole.Value>=diameter.Value;
                        bool forbidden=r.ForbiddenPlanes.Any(p=>ConnectionMath.InForbiddenZone(origin,p,(diameter??0)/2,r.ToleranceMm));ok&=!forbidden;
                        var intervals=new List<object>();double lo=double.PositiveInfinity,hi=double.NegativeInfinity;
                        foreach(var key in r.StackParts)
                        {var e=elements[key];var positions=ConnectionNodes.Corners(e).Select(p=>ConnectionMath.Dot(ConnectionMath.Sub(p,origin),NativeToolUtil.Vector(axis))).ToArray();var min=positions.Min();var max=positions.Max();lo=Math.Min(lo,min);hi=Math.Max(hi,max);intervals.Add(new {part=key,start_mm=min,end_mm=max,method="projected bounding box, conservative envelope"});}
                        double? stack=r.StackParts.Count>0?hi-lo:null;
                        if(grip.HasValue&&stack.HasValue)ok&=grip.Value+r.ToleranceMm>=stack.Value;
                        var bores=r.StackParts.Select(key=>new {part=key,evidence=ConnectionOpeningGeometry.Inspect(elements[key],fa.Origin,axis,diameter??double.PositiveInfinity,r.ToleranceMm)}).ToArray();
                        if(bores.Any(bore=>bore.evidence.Verified==false))ok=false;
                        if(hole.HasValue)ok&=bores.All(bore=>bore.evidence.DiameterMm==null||Math.Abs(bore.evidence.DiameterMm.Value-hole.Value)<=r.ToleranceMm);
                        bool full=diameter.HasValue&&hole.HasValue&&grip.HasValue&&r.StackParts.Count>0&&bores.All(bore=>bore.evidence.Verified==true);
                        Result("bolt",r.A,r.B,!ok?"failed":full?"passed":"incomplete",new {centerline_distance_mm=metrics.DistanceMm,axis_angle_deg=metrics.AngleDeg,bolt_diameter_mm=diameter,hole_diameter_mm=hole,grip_mm=grip,stack_envelope_mm=stack,intervals,in_forbidden_zone=forbidden,
                            opening_geometry_verified=bores.Length>0&&bores.All(bore=>bore.evidence.Verified==true),bores,coverage="Actual axes and inner cylindrical through-bore geometry for every declared stack part; conservative grip envelope. Capacity and thread engagement are not certified."});break;
                    }
                }
            }
            catch(OperationCanceledException){throw;}
            catch(Autodesk.Revit.Exceptions.RegenerationFailedException){throw;}
            catch(Exception ex){Result(r.Kind,r.A,r.B,"incomplete",new {error=ex.Message});}
        }
        int solidsCount=elements.Count(p=>Solids(p.Key).Count>0);bool geometry=elements.Count==node.Bindings.Count;
        return new(passed,failed,incomplete,new
        {
            geometry=geometry?"elements_present":"missing_elements",parts_with_solids=solidsCount,
            interfaces=node.Spec.Rules.Count==0?"not_tested":failed>0?"failed":incomplete>0?"partially_checked":"declared_checks_passed",
            calculation=node.Spec.CalculationEvidence==null?"not_performed":"external_evidence_unverified",calculation_evidence=node.Spec.CalculationEvidence,
            documentation=node.Spec.DocumentationEvidence==null?"not_prepared":"external_evidence_unverified",documentation_evidence=node.Spec.DocumentationEvidence
        },checks);
    }
}
public sealed class ValidateConnectionNode:IRevitTool
{
    public string Name=>"validate_connection_node";
    public string Description=>"Read-only check of a saved connection node: missing/stale parts, declared solid clashes, planar face contact, actual family bolt/hole axes, diameter/grip length parameters, forbidden splice zones and registered void cuts. Reports passed/failed/incomplete separately. Actual cylindrical through-bores are checked for declared stack parts. Does not certify structural capacity; absent dimensions/checks stay incomplete.";
    public bool RequiresTransaction=>false;
    public InputSchema InputSchema=>NativeToolUtil.Schema(new(){["node_key"]=NativeToolUtil.Field("string","Saved document-local node key.")},"node_key");
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {var doc=NativeToolUtil.Doc(app);var key=ToolInput.RequiredString(input,"node_key");var (_,node)=ConnectionNodes.Find(doc,key);if(node==null)throw new ToolInputException("Node not found.");return Services.Json.Serialize(new {node_key=key,revision=node.Revision,validation=ConnectionNodeValidation.Check(doc,node)});}
}

public sealed class InspectStructuralCapabilities:IRevitTool
{
    public string Name=>"inspect_structural_capabilities";
    public string Description=>"Inspect loaded native structural framing/column family types, size parameters and steel connection types before generating custom geometry. Lists existing schedules too. A ribbon tab alone does not prove a detailed connection package/type is loaded or compatible. Prefer create_beam/create_structural_column and create_structural_connection with a detailed type; report absent resources before offering custom fallback.";
    public bool RequiresTransaction=>false;
    public InputSchema InputSchema=>NativeToolUtil.Schema(new());
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        var doc=NativeToolUtil.Doc(app);
        var types=new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().Where(s=>s.Category?.Id.Value is (long)BuiltInCategory.OST_StructuralFraming or (long)BuiltInCategory.OST_StructuralColumns).Take(501).ToArray();
        var connections=new FilteredElementCollector(doc).OfClass(typeof(StructuralConnectionHandlerType)).Cast<StructuralConnectionHandlerType>().Take(201).ToArray();
        return Services.Json.Serialize(new {member_types=types.Take(500).Select(s=>new {id=s.Id.Value,family=s.FamilyName,name=s.Name,category=s.Category?.Name,parameters=s.Parameters.Cast<Parameter>().Where(p=>p.StorageType==StorageType.Double).Take(30).Select(p=>new {name=p.Definition.Name,spec=p.Definition.GetDataType().TypeId,value=p.AsDouble()}).ToArray()}).ToArray(),
            connection_types=connections.Take(200).Select(t=>new {id=t.Id.Value,name=t.Name}).ToArray(),truncated=types.Length>500||connections.Length>200,
            schedules=new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Where(s=>!s.IsTemplate).Take(100).Select(s=>new {id=s.Id.Value,name=s.Name}).ToArray(),
            next_step=connections.Length==0?"No loaded detailed types found. Ask about installing/loading the steel connection package and approved types before custom fallback.":"Inspect compatible loaded types and build native structural members before applying a detailed connection."});
    }
}
