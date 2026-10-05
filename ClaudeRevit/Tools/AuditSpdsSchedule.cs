using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
namespace ClaudeRevit.Tools;

public sealed class AuditSpdsSchedule : IRevitTool
{
    public string Name=>"audit_spds_schedule";
    public string Description=>"Inspect live schedule fields/physical units/totals, source nested-parent counting, linked model status and actual sheet placements. Reports overlaps, printable-area overflow and column widths; does not certify text wrapping or every SPDS drawing requirement. Use export_image for visual review. No static rows are created.";
    public bool RequiresTransaction=>false;
    public InputSchema InputSchema=>NativeToolUtil.Schema(new(){["schedule_id"]=NativeToolUtil.Field("integer","Native live ViewSchedule."),["sheet_margin_mm"]=NativeToolUtil.Field("number","Agreed uniform sheet margin, default 5 mm; title-block-specific safe area still needs review.")},"schedule_id");
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        var doc=NativeToolUtil.Doc(app);var s=NativeToolUtil.Element(doc,input["schedule_id"].GetInt64()) as ViewSchedule??throw new ToolInputException("Not a native schedule.");
        double margin=ToolInput.OptionalDouble(input,"sheet_margin_mm")??5;if(!double.IsFinite(margin)||margin<0||margin>100)throw new ToolInputException("Margin must be 0..100 mm.");
        var placements=new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>().Where(p=>p.ScheduleId==s.Id&&!p.IsTitleblockRevisionSchedule).ToArray();
        var layout=placements.Select(p=>
        {
            var sheet=doc.GetElement(p.OwnerViewId) as ViewSheet;var bb=p.get_BoundingBox(sheet);var box=sheet?.Outline;
            if(bb==null||box==null)return (object)new {id=p.Id.Value,status="incomplete",reason="No sheet or actual schedule bounds."};
            bool inside=bb.Min.X>=box.Min.U+margin/Units.MmPerFoot&&bb.Min.Y>=box.Min.V+margin/Units.MmPerFoot&&bb.Max.X<=box.Max.U-margin/Units.MmPerFoot&&bb.Max.Y<=box.Max.V-margin/Units.MmPerFoot;
            var others=new FilteredElementCollector(doc,sheet!.Id).WhereElementIsNotElementType().Where(e=>e.Id!=p.Id&&e is ScheduleSheetInstance or Viewport or TextNote).Select(e=>new {e.Id,bounds=e.get_BoundingBox(sheet)}).Where(x=>x.bounds!=null&&x.bounds.Min.X<bb.Max.X&&x.bounds.Max.X>bb.Min.X&&x.bounds.Min.Y<bb.Max.Y&&x.bounds.Max.Y>bb.Min.Y).Select(x=>x.Id.Value).Take(100).ToArray();
            return new {id=p.Id.Value,sheet_id=sheet.Id.Value,inside_uniform_margin=inside,overlapping_element_ids=others,status=inside&&others.Length==0?"passed":"failed",actual_width_mm=(bb.Max.X-bb.Min.X)*Units.MmPerFoot,actual_height_mm=(bb.Max.Y-bb.Min.Y)*Units.MmPerFoot};
        }).ToArray();
        var record=LiveSpds.All(doc).FirstOrDefault(p=>p.Record.ScheduleUniqueIds.Contains(s.UniqueId)).Record;
        var sources=record==null?[]:LiveSpds.Sources(doc,record);
        return Services.Json.Serialize(new {id=s.Id.Value,live=s.Definition.GetFieldCount()>0,include_linked_files=s.Definition.CanIncludeLinkedFiles()&&s.Definition.IncludeLinkedFiles,
            fields=s.Definition.GetFieldOrder().Select(id=>s.Definition.GetField(id)).Select(f=>new {name=f.GetName(),heading=f.ColumnHeading,parameter_id=f.ParameterId.Value,spec=f.GetSpecTypeId().TypeId,total=f.DisplayType.ToString(),hidden=f.IsHidden,width_mm=f.SheetColumnWidth*Units.MmPerFoot}).ToArray(),
            nesting_policy=record?.NestingPolicy,nested_sources=sources.OfType<FamilyInstance>().Where(f=>f.SuperComponent!=null).Select(f=>new {id=f.Id.Value,parent_id=f.SuperComponent.Id.Value}).ToArray(),placements=layout,
            linked_documents=new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Select(l=>new {id=l.Id.Value,loaded=l.GetLinkDocument()!=null,name=l.Name}).Take(100).ToArray(),
            visual_review="Uniform margins and rectangular overlaps only. Check title-block zones, split schedule headers, border style and text wrapping in an exported view before issuing drawings."});
    }
}
