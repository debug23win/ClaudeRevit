using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
namespace ClaudeRevit.Tools;

internal sealed class ConnectionNodeUpdater : IUpdater
{
    private static ConnectionNodeUpdater? _instance;
    private readonly UpdaterId _id;
    private ConnectionNodeUpdater(AddInId addin) => _id=new(addin,new Guid("dc199cba-4d39-4c9d-9858-dab654bd7116"));
    public static void Register(UIControlledApplication app)
    {
        _instance=new(app.ActiveAddInId);UpdaterRegistry.RegisterUpdater(_instance,false);
        var filter=new LogicalOrFilter(new ElementClassFilter(typeof(FamilyInstance)),new ElementClassFilter(typeof(FamilySymbol)));
        foreach(var change in new[]{Element.GetChangeTypeAny(),Element.GetChangeTypeElementDeletion()})UpdaterRegistry.AddTrigger(_instance._id,filter,change);
    }
    public static void Unregister(){if(_instance!=null){UpdaterRegistry.UnregisterUpdater(_instance._id);_instance=null;}}
    public string GetAdditionalInformation()=>"Follows declared connection-node member dimensions/placements. Missing or invalid inputs require explicit refresh.";
    public ChangePriority GetChangePriority()=>ChangePriority.Structure;
    public UpdaterId GetUpdaterId()=>_id;
    public string GetUpdaterName()=>"ClaudeRevit dependent connection nodes";
    public void Execute(UpdaterData data)
    {
        var doc=data.GetDocument();if(doc.IsFamilyDocument)return;
        foreach(var (storage,node) in ConnectionNodes.All(doc).Where(n=>n.Record.AutoUpdate).Take(100).ToArray())
        {
            var elements=node.Bindings.ToDictionary(p=>p.Key,p=>doc.GetElement(p.Value.UniqueId));
            if(elements.All(p=>p.Value!=null&&ConnectionNodes.Signature(p.Value)==node.Bindings[p.Key].Signature))continue;
            var sources=node.Spec.Parts.Where(p=>p.Role=="member").Select(p=>p.Key)
                .Concat(node.Spec.Parts.Where(p=>p.RelativeTo!=null).Select(p=>p.RelativeTo!))
                .Concat(node.Spec.Parts.SelectMany(p=>p.Parameters).Where(p=>p.ValueFrom!=null).Select(p=>p.ValueFrom!.PartKey)).ToHashSet();
            if(elements.All(p=>p.Value!=null)&&sources.All(key=>ConnectionNodes.Signature(elements[key]!)==node.Bindings[key].Signature))
            {node.NeedsRefresh=true;node.LastUpdateError="A managed part was edited independently. Source members are unchanged; deliberate manual refresh is required.";ConnectionNodes.Save(storage,node);continue;}
            using var sub=new SubTransaction(doc);sub.Start();
            try
            {
                if(elements.Values.Any(e=>e==null))throw new ToolInputException("A node part was deleted; supply an explicit replacement.");
                var map=elements.ToDictionary(p=>p.Key,p=>p.Value!);
                foreach(var p in node.Spec.OrderedParts())
                {
                    var e=map[p.Key];
                    if(p.RelativeTo!=null)
                    {var o=p.OffsetMm??new double[3];var parent=ConnectionNodes.Frame(map[p.RelativeTo]);var point=parent.OfPoint(new XYZ(o[0],o[1],o[2])/Units.MmPerFoot);ConnectionNodes.Position(doc,e,point,p.AxisX==null?null:new XYZ(p.AxisX[0],p.AxisX[1],p.AxisX[2]).Normalize(),p.AxisY==null?null:new XYZ(p.AxisY[0],p.AxisY[1],p.AxisY[2]).Normalize());}
                    else if(p.FaceReference!=null)
                    {var face=ConnectionNodes.Face(doc,p.FaceReference);var o=p.OffsetMm??new double[3];ConnectionNodes.Position(doc,e,face.Origin+(face.XVector*o[0]+face.YVector*o[1]+face.FaceNormal*o[2])/Units.MmPerFoot,null,null);}
                    ConnectionNodes.Parameters(e,p.Parameters.Where(v=>v.ValueFrom!=null),map);
                }
                doc.Regenerate();
                foreach(var pair in map)node.Bindings[pair.Key].Signature=ConnectionNodes.Signature(pair.Value);
                var check=ConnectionNodeValidation.Check(doc,node);if(check.Failed>0)throw new ToolInputException("Updated dependent node failed declared interface checks.");
                node.NeedsRefresh=false;node.LastUpdateError=null;node.Revision++;ConnectionNodes.Save(storage,node);sub.Commit();
            }
            catch(Autodesk.Revit.Exceptions.RegenerationFailedException){throw;}
            catch(OperationCanceledException){throw;}
            catch(Exception ex)
            {if(sub.GetStatus()==TransactionStatus.Started)sub.RollBack();var failed=ConnectionNodes.Find(doc,node.Key).Record??node;failed.NeedsRefresh=true;failed.LastUpdateError=ex.Message;ConnectionNodes.Save(storage,failed);}
        }
    }
}
