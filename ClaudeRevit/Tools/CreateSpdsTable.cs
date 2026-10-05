using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class CreateSpdsTable:IRevitTool
{
    public string Name=>"create_spds_table";
    public string Description=>"Create LIVE native ViewSchedules from actual model parameter mappings. Steel form 2 has a construction-group mass matrix, profile/grade subtotals, overall total and a separate LIVE grade-total schedule. Timber material/element statements and scheme form 7 supported. Bound calculated model parameters update in the same Revit transaction after source instance/type edits, additions or deletions; masses are summed before display rounding. No drafting snapshots. Preview=true by default. Existing approved schedules should be reused first. Clarify category, source scope, nested-instance counting, units, form, edition and formatting. Registered add-in updater must remain available; Revit warns if missing.";
    public bool RequiresTransaction=>false;
    public bool MutatesWithoutTransaction=>true;
    public bool RequiresNoTurnGroup=>true;
    public InputSchema InputSchema=>NativeToolUtil.Schema(new()
    {
        ["profile"]=NativeToolUtil.Field("string","steel_rollup, timber_materials, timber_elements, scheme_specification."),["standard_edition"]=NativeToolUtil.Field("string","2026 or agreed legacy 2020."),
        ["table_key"]=NativeToolUtil.Field("string","Unique persistent table key, 1..64 characters. Reusing a key is rejected; edit the existing schedule/mapping deliberately."),
        ["category"]=NativeToolUtil.Field("string","OST_StructuralFraming, OST_StructuralColumns, OST_GenericModel, OST_StructuralFoundation, OST_Walls, OST_Floors or OST_Roofs. Single category per live table."),
        ["scope"]=NativeToolUtil.Field("string","selected: fixed explicitly approved instance IDs; entire_category: includes future category additions. Clarify nested parent/child counting and steel/timber classification first."),
        ["source_ids"]=NativeToolUtil.Array("integer","Required for selected scope: distinct source instances, same category, at most 5000. Values stay live; new instances are included only with entire_category scope."),
        ["field_mapping"]=NativeToolUtil.Any("Map profile/grade/size/group or name/mark/designation/notes/unit and numeric mass_kg/amount/quantity to {parameter_name OR parameter_guid OR parameter_id,scope:instance|type,unit?}. Text may use explicit approved {constant:'text'}. Numeric mass needs kg/t unless physical Mass. Physical amounts require m/m2/m3. mass_kg/amount are per unit and multiplied by quantity, default count=1. Mappings may not reference CR_SPDS derived parameters."),
        ["construction_groups"]=NativeToolUtil.Array("string","Steel: approved 1..20 matrix column groups, in desired order; each source group must match one. Future unknown groups visibly mark totals incomplete."),
        ["name"]=NativeToolUtil.Field("string","Unique schedule name."),["font_name"]=NativeToolUtil.Field("string","Agreed installed font."),["text_height_mm"]=NativeToolUtil.Field("number","Default 2.5, 1.8..7 mm."),["preview"]=NativeToolUtil.Field("boolean","Default true: rollback model parameters and schedules after validating.")
    },"profile","standard_edition","table_key","category","scope","field_mapping","name","font_name");
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        var doc=NativeToolUtil.Doc(app);if(doc.IsFamilyDocument)throw new ToolInputException("Live project schedules require an RVT; edit family parameters then load them into a project.");
        var profile=ToolInput.RequiredString(input,"profile");Services.SpdsTables.Columns(profile);var edition=ToolInput.RequiredString(input,"standard_edition");if(edition is not ("2020" or "2026"))throw new ToolInputException("Choose standard edition 2020/2026.");
        var key=ToolInput.RequiredString(input,"table_key");if(key.Length>64)throw new ToolInputException("table_key must contain 1..64 characters.");var existing=LiveSpds.All(doc).ToArray();if(existing.Any(p=>p.Record.Key==key))throw new ToolInputException("Table key already exists; use its live schedule instead of creating duplicates.");if(existing.Length>=16)throw new ToolInputException("At most 16 derived live tables per document.");
        var category=CategoryResolve.Parse(ToolInput.RequiredString(input,"category"));if(!LiveSpdsUpdater.Categories.Contains(category))throw new ToolInputException("This category has no live derived updater; use create_spds_schedule for direct native fields.");
        var scope=ToolInput.RequiredString(input,"scope");if(scope is not ("selected" or "entire_category"))throw new ToolInputException("Choose explicit selected/entire_category scope.");
        var r=new LiveSpdsRecord{Key=key,Profile=profile,CategoryId=(long)category,Scope=scope,Mapping=input["field_mapping"].Clone()};
        if(r.Mapping.ValueKind!=JsonValueKind.Object)throw new ToolInputException("field_mapping must be an object.");
        var allDerived=existing.SelectMany(p=>p.Record.Parameters.Values).ToHashSet();
        foreach(var mapping in r.Mapping.EnumerateObject())
        {
            var value=mapping.Value;if(value.ValueKind!=JsonValueKind.Object)throw new ToolInputException("Each field mapping must be an object.");
            if(value.TryGetProperty("parameter_name",out var pn)&&pn.GetString()?.StartsWith("CR_SPDS_",StringComparison.OrdinalIgnoreCase)==true||value.TryGetProperty("parameter_guid",out var pg)&&Guid.TryParse(pg.GetString(),out var guid)&&allDerived.Contains(guid)||value.TryGetProperty("parameter_id",out var pid)&&doc.GetElement(new ElementId(pid.GetInt64())) is SharedParameterElement shared&&allDerived.Contains(shared.GuidValue))throw new ToolInputException("Live source mappings cannot depend on derived SPDS parameters; this creates update cycles.");
        }
        if(scope=="selected")r.SourceUniqueIds=NativeToolUtil.Ids(input.TryGetValue("source_ids",out var ids)?ids:throw new ToolInputException("Selected scope needs source_ids."),5000).Select(id=>{var e=NativeToolUtil.Element(doc,id.Value);if(e is ElementType||e.Category?.Id.Value!=(long)category)throw new ToolInputException("Source must be an instance in the requested category.");return e.UniqueId;}).ToArray();
        r.Groups=input.TryGetValue("construction_groups",out var groups)?groups.EnumerateArray().Select(g=>g.GetString()??"").ToArray():[];
        if(profile=="steel_rollup"&&(r.Groups.Length is <1 or >20||r.Groups.Any(string.IsNullOrWhiteSpace)||r.Groups.Distinct().Count()!=r.Groups.Length))throw new ToolInputException("Steel needs 1..20 distinct approved construction_groups.");if(profile!="steel_rollup"&&r.Groups.Length>0)throw new ToolInputException("Construction group columns apply only to steel.");
        var sources=LiveSpds.Sources(doc,r);if(sources.Count==0)throw new ToolInputException("No model sources in the agreed scope.");var rows=sources.Select(e=>SpdsSource.ReadElement(doc,e,r.Mapping)).ToList();var previewTable=Services.SpdsTables.Build(profile,rows);foreach(var i in rows)Services.SpdsTables.ModelValues(profile,i,r.Groups);
        var name=ToolInput.RequiredString(input,"name");Services.GeometryPreflight.Name(name);var font=ToolInput.RequiredString(input,"font_name");var size=ToolInput.OptionalDouble(input,"text_height_mm")??2.5;if(!double.IsFinite(size)||size<1.8||size>7)throw new ToolInputException("Text height must be 1.8..7 mm.");bool preview=NativeToolUtil.Preview(input);
        var (result,warnings)=NativeToolUtil.Commit(doc,"Claude: live SPDS schedules",preview,()=>
        {
            LiveSpds.Bind(doc,r);LiveSpds.Refresh(doc,r,true);
            var schedules=new List<ViewSchedule>{LiveSpds.CreateSchedule(doc,r,name,font,size)};
            if(profile=="steel_rollup")schedules.Add(LiveSpds.CreateSchedule(doc,r,name+" — по маркам металла",font,size,true));
            r.ScheduleUniqueIds=schedules.Select(s=>s.UniqueId).ToArray();LiveSpds.Save(Autodesk.Revit.DB.ExtensibleStorage.DataStorage.Create(doc),r);
            foreach(var s in schedules)ModelProvenance.Write(s,new {generator="live_spds_table",profile,standard_edition=edition,live=true,scope,table_key=key,source_count=sources.Count});
            return new {profile,standard_edition=edition,table_key=key,live=true,source_count=sources.Count,schedules=schedules.Select(s=>new {id=preview?(long?)null:s.Id.Value,name=s.Name}).ToArray(),total_mass_kg=previewTable.TotalMassKg,columns=Services.SpdsTables.Columns(profile,r.Groups),derived_parameters=r.Parameters};
        });
        return Services.Json.Serialize(new {preview,result,warnings,next_step="Place live schedules on sheets and inspect export_image for wrapping, borders and totals. Grade-total steel schedule is separate and must be placed below the main table. Derived fields require ClaudeRevit updater; source errors show an incomplete-data title/column instead of stale totals."});
    }
}
