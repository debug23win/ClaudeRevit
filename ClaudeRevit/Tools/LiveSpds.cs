using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

internal sealed class LiveSpdsRecord
{
    public string Key {get;set;}="";
    public string Profile {get;set;}="";
    public long CategoryId {get;set;}
    public string Scope {get;set;}="";
    public string[] SourceUniqueIds {get;set;}=[];
    public JsonElement Mapping {get;set;}
    public string[] Groups {get;set;}=[];
    public string[] ScheduleUniqueIds {get;set;}=[];
    public Dictionary<string,Guid> Parameters {get;set;}=new();
}

// Persistent mappings live in the RVT. Ordinary Revit edits and edits made by any agent
// use the same dynamic updater, in the triggering transaction and its Undo step.
internal sealed class LiveSpdsUpdater:IUpdater
{
    private static readonly Guid Id=new("eac7c404-6d15-45b6-95cf-dc64ad75ce65");
    private readonly UpdaterId _id;
    private static LiveSpdsUpdater? _instance;
    internal static readonly BuiltInCategory[] Categories=[BuiltInCategory.OST_StructuralFraming,BuiltInCategory.OST_StructuralColumns,BuiltInCategory.OST_GenericModel,BuiltInCategory.OST_StructuralFoundation,BuiltInCategory.OST_Walls,BuiltInCategory.OST_Floors,BuiltInCategory.OST_Roofs];
    private LiveSpdsUpdater(AddInId addin)=>_id=new UpdaterId(addin,Id);
    public static void Register(UIControlledApplication app)
    {
        _instance=new LiveSpdsUpdater(app.ActiveAddInId);
        // Required, not optional: a file with calculated columns must warn when this
        // updater is absent, rather than silently retain old mass/quantity values.
        UpdaterRegistry.RegisterUpdater(_instance,false);
        foreach(var category in Categories)
        {
            var filter=new ElementCategoryFilter(category);
            foreach(var change in new[]{Element.GetChangeTypeAny(),Element.GetChangeTypeElementAddition(),Element.GetChangeTypeElementDeletion()})UpdaterRegistry.AddTrigger(_instance._id,filter,change);
        }
    }
    public static void Unregister(){if(_instance!=null){UpdaterRegistry.UnregisterUpdater(_instance._id);_instance=null;}}
    public string GetAdditionalInformation()=>"Keeps SPDS schedule model parameters current; required for derived mass matrices.";
    public ChangePriority GetChangePriority()=>ChangePriority.Annotations;
    public UpdaterId GetUpdaterId()=>_id;
    public string GetUpdaterName()=>"ClaudeRevit live SPDS quantities";
    public void Execute(UpdaterData data)
    {
        var doc=data.GetDocument();if(doc.IsFamilyDocument)return;
        var changed=data.GetModifiedElementIds().Concat(data.GetAddedElementIds()).Select(doc.GetElement).Where(e=>e?.Category!=null).Select(e=>e!.Category.Id.Value).ToHashSet();
        bool deleted=data.GetDeletedElementIds().Count>0;
        foreach(var (_,record) in LiveSpds.All(doc))
            if(deleted||changed.Contains(record.CategoryId))LiveSpds.Refresh(doc,record,false);
    }
}

internal static class LiveSpds
{
    private static readonly Guid SchemaId=new("0cb995ac-172f-436c-934f-d81311b4cc4f");
    private static readonly string[] TextKeys=["row_key","profile","grade","size","mark","designation","name","notes","unit","error"];
    private static readonly string[] NumericKeys=["position","quantity","mass","total","include"];
    public static Schema Schema()
    {
        var s=Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaId);if(s!=null)return s;
        var b=new SchemaBuilder(SchemaId);b.SetSchemaName("ClaudeRevitLiveSpdsV1");b.SetReadAccessLevel(AccessLevel.Public);b.SetWriteAccessLevel(AccessLevel.Public);b.AddSimpleField("Json",typeof(string));return b.Finish();
    }
    public static IEnumerable<(DataStorage Storage,LiveSpdsRecord Record)> All(Document doc)
    {
        var s=Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaId);if(s==null)yield break;
        foreach(var storage in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>())
        {var entity=storage.GetEntity(s);if(!entity.IsValid())continue;var r=JsonSerializer.Deserialize<LiveSpdsRecord>(entity.Get<string>(s.GetField("Json")));if(r!=null)yield return(storage,r);}
    }
    public static void Save(DataStorage storage,LiveSpdsRecord record)
    {var s=Schema();var e=new Entity(s);e.Set(s.GetField("Json"),JsonSerializer.Serialize(record));storage.SetEntity(e);}
    public static List<Element> Sources(Document doc,LiveSpdsRecord r)
    {
        var all=new FilteredElementCollector(doc).OfCategoryId(new ElementId(r.CategoryId)).WhereElementIsNotElementType().ToElements();
        if(all.Count>10000)throw new ToolInputException("Live SPDS category exceeds 10000 instances; narrow the model/category before creating derived tables.");
        var selected=r.SourceUniqueIds.ToHashSet(StringComparer.Ordinal);
        return all.Where(e=>r.Scope=="entire_category"||selected.Contains(e.UniqueId)).ToList();
    }
    public static void Bind(Document doc,LiveSpdsRecord r)
    {
        var category=Category.GetCategory(doc,new ElementId(r.CategoryId));if(category==null||!category.AllowsBoundParameters)throw new ToolInputException("Category does not support project parameters.");
        var app=doc.Application;var previous=app.SharedParametersFilename;var path=Path.Combine(Path.GetTempPath(),"ClaudeRevit-spds-"+Guid.NewGuid().ToString("N")+".txt");
        try
        {
            File.WriteAllText(path,"# ClaudeRevit derived SPDS parameters\n*META\tVERSION\tMINVERSION\nMETA\t2\t1\n*GROUP\tID\tNAME\nGROUP\t1\tClaudeRevit\n*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\n",Encoding.UTF8);
            app.SharedParametersFilename=path;var file=app.OpenSharedParameterFile()??throw new InvalidOperationException("Cannot open temporary shared parameter definitions.");var group=file.Groups.get_Item("ClaudeRevit");
            foreach(var key in TextKeys.Concat(NumericKeys).Concat(Enumerable.Range(0,r.Groups.Length).Select(g=>"group_"+g)))
            {
                ToolContext.ThrowIfCancelled();
                var bytes=SHA256.HashData(Encoding.UTF8.GetBytes("ClaudeRevit.LiveSpds.v1|"+r.Key+"|"+r.CategoryId+"|"+key));var guid=new Guid(bytes[..16]);r.Parameters[key]=guid;
                var existing=SharedParameterElement.Lookup(doc,guid);
                var spec=TextKeys.Contains(key)?SpecTypeId.String.Text:key is "position" or "include"?SpecTypeId.Int.Integer:key is "mass" or "total"||key.StartsWith("group_")?SpecTypeId.Mass:SpecTypeId.Number;
                if(existing!=null){if(existing.GetDefinition().GetDataType()!=spec)throw new ToolInputException("Derived parameter identity has an incompatible data type.");continue;}
                var options=new ExternalDefinitionCreationOptions("CR_SPDS_"+Convert.ToHexString(bytes)[..8]+"_"+key,spec){GUID=guid,UserModifiable=false,Description="Calculated from model parameters for live SPDS table "+r.Key};
                var definition=group.Definitions.Create(options);var set=app.Create.NewCategorySet();set.Insert(category);
                if(!doc.ParameterBindings.Insert(definition,app.Create.NewInstanceBinding(set),GroupTypeId.Data))throw new ToolInputException("Cannot bind derived SPDS model parameter: "+key);
            }
            doc.Regenerate();
        }
        finally{app.SharedParametersFilename=previous;try{File.Delete(path);}catch{}}
    }
    private static void Set(Element e,Guid guid,object value,bool mass=false)
    {
        var p=e.get_Parameter(guid)??throw new ToolInputException("A live SPDS parameter binding is missing; recreate this table mapping.");
        if(value is string text){if((!p.HasValue||p.AsString()!=text)&&!p.Set(text))throw new InvalidOperationException("Cannot update live table text.");}
        else if(value is int integer){if((!p.HasValue||p.AsInteger()!=integer)&&!p.Set(integer))throw new InvalidOperationException("Cannot update live table count.");}
        else{double n=Convert.ToDouble(value,System.Globalization.CultureInfo.InvariantCulture);if(mass)n=UnitUtils.ConvertToInternalUnits(n,UnitTypeId.Kilograms);if(!double.IsFinite(n))throw new ToolInputException("Nonfinite live quantity.");if((!p.HasValue||Math.Abs(p.AsDouble()-n)>1e-10*Math.Max(1,Math.Abs(n)))&&!p.Set(n))throw new InvalidOperationException("Cannot update live table amount.");}
    }
    public static (int Count,List<object> Errors) Refresh(Document doc,LiveSpdsRecord r,bool strict)
    {
        var all=new FilteredElementCollector(doc).OfCategoryId(new ElementId(r.CategoryId)).WhereElementIsNotElementType().ToElements();if(all.Count>10000)throw new ToolInputException("Live SPDS category exceeds 10000 instances.");
        var selected=r.SourceUniqueIds.ToHashSet(StringComparer.Ordinal);var items=new List<(Element Element,SpdsItem Item,Dictionary<string,object> Values)>();var errors=new List<object>();
        foreach(var e in all)
        {
            if(strict)ToolContext.ThrowIfCancelled();
            bool include=r.Scope=="entire_category"||selected.Contains(e.UniqueId);Set(e,r.Parameters["include"],include?1:0);if(!include)continue;
            try{var i=SpdsSource.ReadElement(doc,e,r.Mapping);var values=Services.SpdsTables.ModelValues(r.Profile,i,r.Groups);items.Add((e,i,values));Set(e,r.Parameters["error"],"");}
            catch(Exception ex) when(ex is ToolInputException or ArgumentException)
            {
                if(strict)throw new ToolInputException($"Source {e.Id.Value}: {ex.Message}");errors.Add(new {id=e.Id.Value,error=ex.Message});
                Set(e,r.Parameters["error"],"НЕПОЛНЫЕ ДАННЫЕ: "+ex.Message);
                // Never keep stale numeric quantities after a bad edit. The visible error
                // column and title explicitly mark the remaining totals as incomplete.
                foreach(var key in r.Parameters.Keys.Where(k=>k is "quantity" or "mass" or "total"||k.StartsWith("group_")))Set(e,r.Parameters[key],0d,key is "mass" or "total"||key.StartsWith("group_"));
            }
        }
        var ordered=items.OrderBy(i=>i.Item.Profile,StringComparer.Ordinal).ThenBy(i=>i.Item.Grade,StringComparer.Ordinal).ThenBy(i=>r.Profile=="steel_rollup"?i.Item.Size:r.Profile=="timber_materials"?i.Item.Name:i.Item.Mark,Comparer<string>.Create(Services.SpdsTables.CompareNatural));
        var positions=new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(var (e,i,values) in ordered)
        {
            var row=(string)values["row_key"];if(!positions.TryGetValue(row,out int pos)){pos=positions.Count+1;positions[row]=pos;}
            values["position"]=pos;
            foreach(var (key,value) in values)if(r.Parameters.TryGetValue(key,out var guid))Set(e,guid,value,key is "mass" or "total"||key.StartsWith("group_"));
        }
        foreach(var uid in r.ScheduleUniqueIds)
        {
            if(doc.GetElement(uid) is not ViewSchedule s)continue;
            var errorId=SharedParameterElement.Lookup(doc,r.Parameters["error"])?.Id;
            foreach(var id in s.Definition.GetFieldOrder()){var field=s.Definition.GetField(id);if(field.ParameterId==errorId&&field.IsHidden!=(errors.Count==0))field.IsHidden=errors.Count==0;}
            string marker=" — НЕПОЛНЫЕ ДАННЫЕ";var title=s.Name.EndsWith(marker,StringComparison.Ordinal)?s.Name[..^marker.Length]:s.Name;
            if(errors.Count>0)title+=marker;if(s.Name!=title)s.Name=title;
        }
        return(items.Count,errors);
    }
    public static ViewSchedule CreateSchedule(Document doc,LiveSpdsRecord r,string name,string font,double size,bool gradeTotals=false)
    {
        var schedule=ViewSchedule.CreateSchedule(doc,new ElementId(r.CategoryId));schedule.Name=name;var d=schedule.Definition;d.IsItemized=false;d.ShowHeaders=true;d.ShowTitle=true;
        var columns=Services.SpdsTables.Columns(r.Profile,r.Groups);var added=new Dictionary<string,ScheduleField>();
        ScheduleField Field(string key,bool hidden=false,string? heading=null,double width=15)
        {
            var id=SharedParameterElement.Lookup(doc,r.Parameters[key])?.Id??throw new InvalidOperationException("Missing shared parameter: "+key);var candidates=d.GetSchedulableFields().Where(f=>f.ParameterId==id&&f.FieldType==ScheduleFieldType.Instance).ToArray();
            if(candidates.Length!=1)throw new ToolInputException("Derived field is not schedulable: "+key);var field=d.AddField(candidates[0]);field.ColumnHeading=heading??key;field.IsHidden=hidden;field.GridColumnWidth=field.SheetColumnWidth=width/Units.MmPerFoot;added[key]=field;
            var style=field.GetStyle();style.FontName=font;style.TextSize=size*72/25.4;style.FontHorizontalAlignment=TextKeys.Contains(key)?HorizontalAlignmentStyle.Left:HorizontalAlignmentStyle.Center;
            var flags=style.GetCellStyleOverrideOptions();flags.Font=true;flags.FontSize=true;flags.HorizontalAlignment=true;style.SetCellStyleOverrideOptions(flags);field.SetStyle(style);
            if(key is "mass" or "total"||key.StartsWith("group_")){using var format=new FormatOptions(r.Profile=="steel_rollup"?UnitTypeId.Tonnes:UnitTypeId.Kilograms){UseDefault=false,Accuracy=r.Profile=="steel_rollup"?.1:.001};field.SetFormatOptions(format);}
            bool total=key is "quantity" or "total"||key.StartsWith("group_");if(total){if(!field.CanTotal())throw new ToolInputException("Cannot total derived field.");field.DisplayType=ScheduleFieldDisplayType.Totals;}
            return field;
        }
        for(int index=0;index<columns.Count;index++)
        {
            var c=columns[index];string key=r.Profile=="steel_rollup"&&index>=4&&index<columns.Count-1?"group_"+(index-4):c.Key;
            if(gradeTotals&&key is "profile" or "size" or "position")continue;Field(key,false,c.Heading,c.WidthMm);
        }
        Field("include",true);Field("row_key",true);if(!added.ContainsKey("position"))Field("position",true);Field("error",true,"Проверка данных",50);
        d.AddFilter(new ScheduleFilter(added["include"].FieldId,ScheduleFilterType.Equal,1));
        if(r.Profile=="steel_rollup")
        {
            if(!gradeTotals)d.AddSortGroupField(new ScheduleSortGroupField(added["profile"].FieldId,ScheduleSortOrder.Ascending){ShowFooter=true,ShowFooterTitle=true,ShowFooterCount=false});
            d.AddSortGroupField(new ScheduleSortGroupField(added["grade"].FieldId,ScheduleSortOrder.Ascending){ShowFooter=!gradeTotals,ShowFooterTitle=!gradeTotals,ShowFooterCount=false});
        }
        if(!gradeTotals){d.AddSortGroupField(new ScheduleSortGroupField(added["position"].FieldId,ScheduleSortOrder.Ascending));d.AddSortGroupField(new ScheduleSortGroupField(added["row_key"].FieldId,ScheduleSortOrder.Ascending));}
        d.ShowGrandTotal=r.Profile=="steel_rollup";d.ShowGrandTotalTitle=d.ShowGrandTotal;d.ShowGrandTotalCount=false;d.GrandTotalTitle="Всего масса металла";
        var seed=new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().FirstOrDefault()??throw new ToolInputException("No text style to duplicate.");var tt=(TextNoteType)seed.Duplicate("SPDS live "+Guid.NewGuid().ToString("N")[..8]);tt.get_Parameter(BuiltInParameter.TEXT_FONT).Set(font);tt.get_Parameter(BuiltInParameter.TEXT_SIZE).Set(size/Units.MmPerFoot);schedule.BodyTextTypeId=schedule.HeaderTextTypeId=schedule.TitleTextTypeId=tt.Id;
        doc.Regenerate();if(r.Profile=="steel_rollup"){int start=gradeTotals?1:4;int end=start+r.Groups.Length-1;if(end>=start&&schedule.CanGroupHeaders(0,start,0,end))schedule.GroupHeaders(0,start,0,end,"Масса по группам конструкций, т");}
        return schedule;
    }
}
