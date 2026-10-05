using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class CreateSpdsSchedule:IRevitTool
{
    public string Name=>"create_spds_schedule";
    public string Description=>"Create a native live SPDS schedule, optionally by duplicating an approved existing schedule. Sets exact mapped fields/headings, sheet/grid column widths, units/accuracy, alignment, font/size, sorting, totals and source filters. Timber scheme form 7 and forms 1/2 supported; steel mass matrix uses create_spds_table. Every field must resolve uniquely, no silently missing columns. Default preview=true. Inspect get_spds_table_profiles(category) and agree formatting/scope first.";
    public bool RequiresTransaction=>false;
    public bool RequiresNoTurnGroup=>true;
    public bool MutatesWithoutTransaction=>true;
    public InputSchema InputSchema=>NativeToolUtil.Schema(new()
    {
        ["profile"]=NativeToolUtil.Field("string","scheme_specification, timber_materials, timber_elements."),["standard_edition"]=NativeToolUtil.Field("string","2020 or 2026."),["category"]=NativeToolUtil.Field("string","Source category."),["name"]=NativeToolUtil.Field("string","Unique schedule name."),["existing_schedule_id"]=NativeToolUtil.Field("integer","Optional approved schedule to duplicate. Its field mappings are replaced; filters retained unless filters is supplied."),
        ["fields"]=NativeToolUtil.Any("Array in profile column order: {parameter_id? OR name?,heading?,width_mm?,unit?:mm|m|m2|m3|kg|t,accuracy?,total?:bool,alignment?:left|center|right}. A schedulable Count field can be resolved by its inspected name/parameter ID."),
        ["sort_fields"]=NativeToolUtil.Array("integer","Optional field indices for ascending sort/group."),["filters"]=NativeToolUtil.Any("Optional [{field_index,value}] equality filters; values are raw internal numeric units or text. Use to select steel/timber and building section."),["scope"]=NativeToolUtil.Field("string","filtered or entire_category; explicit entire_category allows an unfiltered schedule."),
        ["include_linked_files"]=NativeToolUtil.Field("boolean","Default false. Native live linked rows using fields already bound in the links; does not modify read-only linked documents."),
        ["is_itemized"]=NativeToolUtil.Field("boolean","Default false."),["font_name"]=NativeToolUtil.Field("string","Agreed installed font."),["text_height_mm"]=NativeToolUtil.Field("number","Default 2.5 mm."),["grand_total"]=NativeToolUtil.Field("boolean","Default false; set only when quantity/mass totals are meaningful."),["preview"]=NativeToolUtil.Field("boolean","Default true.")
    },"profile","standard_edition","category","name","fields","scope","font_name");
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        var doc=NativeToolUtil.Doc(app);var profile=ToolInput.RequiredString(input,"profile");var columns=Services.SpdsTables.Columns(profile);
        if(profile=="steel_rollup")throw new ToolInputException("The steel construction-group matrix needs create_spds_table.");
        var edition=ToolInput.RequiredString(input,"standard_edition");if(edition is not ("2020" or "2026"))throw new ToolInputException("Choose standard edition 2020/2026.");
        var fields=input["fields"].EnumerateArray().ToArray();if(fields.Length!=columns.Count)throw new ToolInputException("Each required profile column needs a field mapping.");
        var scope=ToolInput.RequiredString(input,"scope");if(scope is not ("filtered" or "entire_category"))throw new ToolInputException("scope must be filtered/entire_category.");
        var name=ToolInput.RequiredString(input,"name");Services.GeometryPreflight.Name(name);var size=ToolInput.OptionalDouble(input,"text_height_mm")??2.5;if(!double.IsFinite(size)||size<1.8||size>7)throw new ToolInputException("text_height_mm must be 1.8..7.");
        var preview=NativeToolUtil.Preview(input);var categoryId=input["category"].GetString()=="multi_category"?ElementId.InvalidElementId:new ElementId(CategoryResolve.Parse(input["category"].GetString()??""));
        var (result,warnings)=NativeToolUtil.Commit(doc,"Claude: SPDS schedule",preview,()=>
        {
            ViewSchedule s;
            if(input.TryGetValue("existing_schedule_id",out var existing))
            {var original=NativeToolUtil.Element(doc,existing.GetInt64()) as ViewSchedule??throw new ToolInputException("Not a ViewSchedule.");if(original.IsTemplate)throw new ToolInputException("Use an actual approved schedule, not a view template.");s=(ViewSchedule)doc.GetElement(original.Duplicate(ViewDuplicateOption.Duplicate));if(s.Definition.CategoryId!=categoryId)throw new ToolInputException("Existing schedule category differs from requested category.");s.ViewTemplateId=ElementId.InvalidElementId;}
            else s=ViewSchedule.CreateSchedule(doc,categoryId);
            s.Name=name;var definition=s.Definition;
            if(ToolInput.Flag(input,"include_linked_files")){if(!definition.CanIncludeLinkedFiles())throw new ToolInputException("This schedule category cannot include linked files.");definition.IncludeLinkedFiles=true;}
            // Filters/sort reference field IDs: snapshot approved ones only when reusing identical fields.
            var oldFilters=definition.GetFilters().ToArray();var available=definition.GetSchedulableFields();var added=new List<ScheduleField>();
            var oldOrder=definition.GetFieldOrder();definition.ClearFilters();definition.ClearSortGroupFields();
            for(int index=0;index<fields.Length;index++)
            {
                var f=fields[index];var candidates=available.Where(sf=>f.TryGetProperty("parameter_id",out var pid)?sf.ParameterId.Value==pid.GetInt64():f.TryGetProperty("name",out var fn)&&sf.GetName(doc).Equals(fn.GetString(),StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                if(candidates.Length!=1)throw new ToolInputException($"Column {index} is missing or ambiguous; inspect schedulable fields first.");
                var existingField=oldOrder.Select(definition.GetField).FirstOrDefault(sf=>sf.HasSchedulableField&&sf.GetSchedulableField().ParameterId==candidates[0].ParameterId&&sf.FieldType==candidates[0].FieldType);
                var field=existingField??definition.AddField(candidates[0]);if(added.Any(a=>a.FieldId==field.FieldId))throw new ToolInputException("Duplicate mapped field.");added.Add(field);
                field.IsHidden=false;field.ColumnHeading=f.TryGetProperty("heading",out var heading)?heading.GetString()??columns[index].Heading:columns[index].Heading;
                var width=f.TryGetProperty("width_mm",out var ww)?ww.GetDouble():columns[index].WidthMm;if(!double.IsFinite(width)||width<8)throw new ToolInputException("Column width must be at least 8 mm.");field.GridColumnWidth=width/Units.MmPerFoot;field.SheetColumnWidth=width/Units.MmPerFoot;
                var style=field.GetStyle();style.FontName=input["font_name"].GetString();style.TextSize=size*72/25.4;
                style.FontHorizontalAlignment=f.TryGetProperty("alignment",out var alignment)?alignment.GetString() switch {"center"=>HorizontalAlignmentStyle.Center,"right"=>HorizontalAlignmentStyle.Right,"left"=>HorizontalAlignmentStyle.Left,_=>throw new ToolInputException("Invalid alignment.")}:HorizontalAlignmentStyle.Left;
                var flags=style.GetCellStyleOverrideOptions();flags.Font=true;flags.FontSize=true;flags.HorizontalAlignment=true;style.SetCellStyleOverrideOptions(flags);field.SetStyle(style);
                if(f.TryGetProperty("unit",out var unit))
                {
                    var id=unit.GetString() switch {"mm"=>UnitTypeId.Millimeters,"m"=>UnitTypeId.Meters,"m2"=>UnitTypeId.SquareMeters,"m3"=>UnitTypeId.CubicMeters,"kg"=>UnitTypeId.Kilograms,"t"=>UnitTypeId.Tonnes,_=>throw new ToolInputException("Unsupported field unit.")};
                    var accuracy=f.TryGetProperty("accuracy",out var ac)?ac.GetDouble():.001;if(!double.IsFinite(accuracy)||accuracy<=0)throw new ToolInputException("accuracy must be positive.");
                    using var format=new FormatOptions(id){UseDefault=false,Accuracy=accuracy};field.SetFormatOptions(format);
                }
                if(f.TryGetProperty("total",out var total)&&total.GetBoolean()){if(!field.CanTotal())throw new ToolInputException("This field cannot be totalled.");field.DisplayType=ScheduleFieldDisplayType.Totals;}
            }
            foreach(var id in oldOrder.Where(id=>!added.Any(f=>f.FieldId==id)))definition.RemoveField(id);
            definition.SetFieldOrder(added.Select(f=>f.FieldId).ToList());
            if(input.TryGetValue("filters",out var filters))foreach(var item in filters.EnumerateArray())
            {
                var index=item.GetProperty("field_index").GetInt32();if(index<0||index>=added.Count)throw new ToolInputException("Filter field_index out of range.");var value=item.GetProperty("value");
                var filter=value.ValueKind==JsonValueKind.String?new ScheduleFilter(added[index].FieldId,ScheduleFilterType.Equal,value.GetString()!):value.TryGetInt32(out var integer)?new ScheduleFilter(added[index].FieldId,ScheduleFilterType.Equal,integer):new ScheduleFilter(added[index].FieldId,ScheduleFilterType.Equal,value.GetDouble());definition.AddFilter(filter);
            }
            else foreach(var filter in oldFilters)
            {if(added.Any(f=>f.FieldId==filter.FieldId))definition.AddFilter(filter);else throw new ToolInputException("A reused filter references an omitted field; specify replacement filters explicitly.");}
            if(scope=="filtered"&&definition.GetFilterCount()==0)throw new ToolInputException("Filtered scope needs at least one filter.");
            if(input.TryGetValue("sort_fields",out var sort))foreach(var idx in sort.EnumerateArray()){var i=idx.GetInt32();if(i<0||i>=added.Count)throw new ToolInputException("Sort field index out of range.");definition.AddSortGroupField(new ScheduleSortGroupField(added[i].FieldId,ScheduleSortOrder.Ascending));}
            definition.IsItemized=input.TryGetValue("is_itemized",out var itemized)&&itemized.GetBoolean();definition.ShowHeaders=true;definition.ShowTitle=true;
            definition.ShowGrandTotal=input.TryGetValue("grand_total",out var grand)&&grand.GetBoolean();
            // Dedicated text types apply to title/header/body without changing shared project styles.
            var seed=new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().FirstOrDefault()??throw new ToolInputException("No text type available.");
            var tt=(TextNoteType)seed.Duplicate("SPDS schedule "+Guid.NewGuid().ToString("N")[..8]);tt.get_Parameter(BuiltInParameter.TEXT_FONT).Set(input["font_name"].GetString());tt.get_Parameter(BuiltInParameter.TEXT_SIZE).Set(size/Units.MmPerFoot);
            s.BodyTextTypeId=tt.Id;s.HeaderTextTypeId=tt.Id;s.TitleTextTypeId=tt.Id;doc.Regenerate();
            ModelProvenance.Write(s,new {generator="spds_schedule",profile,standard_edition=edition,live=true,scope});
            return new {id=preview?(long?)null:s.Id.Value,name=s.Name,profile,standard_edition=edition,live=true,fields=added.Select(f=>new {id=f.FieldId.IntegerValue,heading=f.ColumnHeading,width_mm=f.SheetColumnWidth*Units.MmPerFoot}).ToArray(),filter_count=definition.GetFilterCount()};
        });
        return Services.Json.Serialize(new {preview,result,warnings,next_step="Place schedule on sheet, inspect export_image and verify row wrapping, borders, totals and title block before issue."});
    }
}
