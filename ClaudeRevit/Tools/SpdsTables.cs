using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

public sealed class GetSpdsTableProfiles:IRevitTool
{
    public string Name=>"get_spds_table_profiles";
    public string Description=>"Inspect available SPDS steel/timber table forms, column widths and existing project schedules before making tables. Optional category lists actual schedulable parameter IDs/names via a rolled-back temporary schedule. Clarify standard edition, KM/KMD/KD scope, required form, units, grouping, font and sheet placement first. 21.101-2026 is current; legacy 2020 remains selectable by project agreement.";
    public bool RequiresTransaction=>false;
    public bool RequiresNoTurnGroup=>true;
    public InputSchema InputSchema=>NativeToolUtil.Schema(new(){["category"]=NativeToolUtil.Field("string","Optional category such as OST_StructuralFraming.")});
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        var doc=NativeToolUtil.Doc(app);object? fields=null;
        if(input.TryGetValue("category",out var category))
            fields=NativeToolUtil.Commit(doc,"Claude: inspect schedule fields",true,()=>{var s=ViewSchedule.CreateSchedule(doc,new ElementId(CategoryResolve.Parse(category.GetString()??"")));return s.Definition.GetSchedulableFields().Select(f=>new {parameter_id=f.ParameterId.Value,name=f.GetName(doc),field_type=f.FieldType.ToString()}).ToArray();}).Value;
        return Services.Json.Serialize(new {profiles=Services.SpdsTables.Profiles.Select(p=>new {profile=p,columns=Services.SpdsTables.Columns(p),reference=p=="steel_rollup"?"GOST 21.502-2016 annex L form 2":p=="timber_materials"?"GOST 21.504-2016 form 1":p=="timber_elements"?"GOST 21.504-2016 form 2":"GOST R 21.101 form 7; timber GOST 21.504-2016 clause 6.5",mode="native live ViewSchedule; steel includes live matrix and grade totals"}).ToArray(),
            editions=new[]{"2026","2020"},schedulable_fields=fields,existing_schedules=new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Take(200).Select(s=>new {id=s.Id.Value,name=s.Name,is_template=s.IsTemplate}).ToArray(),
            sources=new[]{"https://protect.gost.ru/gost/details/17bc12e8-6579-4145-b141-56855e772e7f","https://protect.gost.ru/gost/details/57d18a56-0d60-4071-8f93-053f789060ad","https://protect.gost.ru/gost/details/b4a24268-5643-4322-b59f-b60288da0806"},
            limitation="All created tables are live ViewSchedules. Derived steel matrix fields require the registered ClaudeRevit updater; Revit warns if the add-in is unavailable. Profiles implement table forms, not every drawing-set requirement. Title blocks, amendments, fonts and project standard still need review."});
    }
}
internal static class SpdsSource
{
    public static SpdsItem ReadElement(Document doc,Element source,JsonElement map)
    {
        Parameter? Parameter(Element e,JsonElement rule)
        {
            if(rule.TryGetProperty("scope",out var scope)){if(scope.GetString() is not ("instance" or "type"))throw new ToolInputException("Parameter scope must be instance/type.");if(scope.GetString()=="type")e=doc.GetElement(e.GetTypeId())??throw new ToolInputException("Source has no type.");}
            if(new[]{"parameter_id","parameter_name","parameter_guid"}.Count(k=>rule.TryGetProperty(k,out _))!=1)throw new ToolInputException("Each source mapping needs exactly one parameter identity.");
            if(rule.TryGetProperty("parameter_id",out var pid))return e.Parameters.Cast<Parameter>().FirstOrDefault(p=>p.Id.Value==pid.GetInt64());
            return NativeToolUtil.Parameter(e,rule.TryGetProperty("parameter_name",out var name)?name.GetString()??"":"",rule.TryGetProperty("parameter_guid",out var guid)?guid.GetString()??"":"");
        }
        string Text(Element e,string key)
        {if(!map.TryGetProperty(key,out var rule))return "";if(rule.TryGetProperty("constant",out var c))return c.GetString()??"";var p=Parameter(e,rule)??throw new ToolInputException($"Source {e.Id.Value} lacks {key}; clarify field mapping.");return p.AsString()??p.AsValueString()??"";}
        double? Number(Element e,string key)
        {
            if(!map.TryGetProperty(key,out var rule))return null;var p=Parameter(e,rule)??throw new ToolInputException($"Source {e.Id.Value} lacks {key}.");
            if(p.StorageType is not (StorageType.Double or StorageType.Integer))throw new ToolInputException($"{key} must be numeric; formatted text is not a measurement.");
            double n=p.StorageType==StorageType.Double?p.AsDouble():p.AsInteger();var spec=p.Definition.GetDataType();var unit=rule.TryGetProperty("unit",out var u)?u.GetString():null;
            if(key=="mass_kg")
            {
                if(spec==SpecTypeId.Mass)return UnitUtils.ConvertFromInternalUnits(n,UnitTypeId.Kilograms);
                if(spec!=SpecTypeId.Number&&spec!=SpecTypeId.Int.Integer)throw new ToolInputException("Mass field has a different physical dimension.");
                return unit switch {"kg"=>n,"t"=>n*1000,_=>throw new ToolInputException("A legacy number mass field needs explicit unit kg/t.")};
            }
            if(key=="amount")
                return spec==SpecTypeId.Volume&&unit=="m3"?UnitUtils.ConvertFromInternalUnits(n,UnitTypeId.CubicMeters):spec==SpecTypeId.Area&&unit=="m2"?UnitUtils.ConvertFromInternalUnits(n,UnitTypeId.SquareMeters):spec==SpecTypeId.Length&&unit=="m"?UnitUtils.ConvertFromInternalUnits(n,UnitTypeId.Meters):spec==SpecTypeId.Number||spec==SpecTypeId.Int.Integer?n:throw new ToolInputException("amount needs compatible m/m2/m3 unit mapping.");
            if(spec!=SpecTypeId.Number&&spec!=SpecTypeId.Int.Integer)throw new ToolInputException("quantity must be a numeric count.");return n;
        }
        var e=source;if(e is ElementType)throw new ToolInputException("Specify instance IDs for table sources.");
        return new SpdsItem {SourceId=e.Id.Value,Mark=Text(e,"mark"),Designation=Text(e,"designation"),Name=Text(e,"name"),Notes=Text(e,"notes"),Profile=Text(e,"profile"),Grade=Text(e,"grade"),Size=Text(e,"size"),Group=Text(e,"group"),Unit=Text(e,"unit"),Quantity=Number(e,"quantity")??1,MassKg=Number(e,"mass_kg"),Amount=Number(e,"amount")};
    }
    public static List<SpdsItem> Read(Document doc,JsonElement ids,JsonElement map)=>NativeToolUtil.Ids(ids,5000).Select(id=>ReadElement(doc,NativeToolUtil.Element(doc,id.Value),map)).ToList();
}

