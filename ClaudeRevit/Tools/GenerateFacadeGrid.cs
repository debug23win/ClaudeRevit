using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class GenerateFacadeGrid : IRevitTool
{
    public string Name => "generate_facade_grid";
    public string Description => "Place native, unhosted facade/module family instances on a parameter grid in one cancellable atomic Undo batch. origin_mm + column_step_mm*i + row_step_mm*j. Requires OneLevelBased non-hosted FamilySymbol and level_id. Rotation baked per instance. Hosted curtain panels require their native host tools instead. Max 5000 instances; preview defaults true. Optional provenance records uncertain module dimensions.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["symbol_id"]=NativeToolUtil.Field("integer","Loaded non-hosted OneLevelBased FamilySymbol."),["level_id"]=NativeToolUtil.Field("integer","Level ID."),
        ["origin_mm"]=NativeToolUtil.Any("[x,y,z]"),["column_step_mm"]=NativeToolUtil.Any("[x,y,z] grid vector."),["row_step_mm"]=NativeToolUtil.Any("[x,y,z] grid vector."),
        ["columns"]=NativeToolUtil.Field("integer","Positive count."),["rows"]=NativeToolUtil.Field("integer","Positive count."),["rotation_deg"]=NativeToolUtil.Field("number","Rotation around Z, default 0."),
        ["preview"]=NativeToolUtil.Field("boolean","Default true; false commits."),["provenance"]=NativeToolUtil.Any("Sources and dimension certainty.")
    },"symbol_id","level_id","origin_mm","column_step_mm","row_step_mm","columns","rows");
    public void Preflight(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        var doc=NativeToolUtil.Doc(app);var symbol=NativeToolUtil.Element(doc,ToolInput.RequiredLong(input, "symbol_id")) as FamilySymbol??throw new ToolInputException("symbol_id is not FamilySymbol.");
        if(symbol.Family.FamilyPlacementType!=FamilyPlacementType.OneLevelBased)throw new ToolInputException("Only non-hosted OneLevelBased families are supported.");
        if(NativeToolUtil.Element(doc,ToolInput.RequiredLong(input, "level_id")) is not Level)throw new ToolInputException("level_id is not Level.");
        var c=ToolInput.RequiredInt(input, "columns");var r=ToolInput.RequiredInt(input, "rows");if(c<1||r<1||(long)c*r>5000)throw new ToolInputException("Grid must have 1..5000 instances.");
        var column=NativeToolUtil.Point(ToolInput.Required(input, "column_step_mm"));var row=NativeToolUtil.Point(ToolInput.Required(input, "row_step_mm"));NativeToolUtil.Point(ToolInput.Required(input, "origin_mm"));
        if(c>1&&column.GetLength()<doc.Application.ShortCurveTolerance||r>1&&row.GetLength()<doc.Application.ShortCurveTolerance||c>1&&r>1&&column.CrossProduct(row).GetLength()<1e-9)throw new ToolInputException("Grid steps must be distinct, non-zero and not collinear.");
        if(!double.IsFinite(ToolInput.OptionalDouble(input,"rotation_deg")??0))throw new ToolInputException("Rotation must be finite.");
    }
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        Preflight(input,app);var doc=NativeToolUtil.Doc(app);var symbol=(FamilySymbol)NativeToolUtil.Element(doc,ToolInput.RequiredLong(input, "symbol_id"));var level=(Level)NativeToolUtil.Element(doc,ToolInput.RequiredLong(input, "level_id"));
        var origin=NativeToolUtil.Point(ToolInput.Required(input, "origin_mm"));var col=NativeToolUtil.Point(ToolInput.Required(input, "column_step_mm"));var row=NativeToolUtil.Point(ToolInput.Required(input, "row_step_mm"));var columns=ToolInput.RequiredInt(input, "columns");var rows=ToolInput.RequiredInt(input, "rows");var angle=(ToolInput.OptionalDouble(input,"rotation_deg")??0)*Math.PI/180;var preview=NativeToolUtil.Preview(input);
        var (ids,warnings)=NativeToolUtil.Commit(doc,"Claude: facade grid",preview,()=>
        {
            if(!symbol.IsActive){symbol.Activate();doc.Regenerate();}var ids=new List<long>();
            for(var r=0;r<rows;r++)for(var c=0;c<columns;c++)
            {
                ToolContext.ReportProgress(ids.Count,rows*columns,"Facade modules");var point=origin+col*c+row*r;
                var instance=doc.Create.NewFamilyInstance(point,symbol,level,StructuralType.NonStructural);
                if(Math.Abs(angle)>1e-9)ElementTransformUtils.RotateElement(doc,instance.Id,Line.CreateBound(point,point+XYZ.BasisZ),angle);
                ModelProvenance.Write(instance,ModelProvenance.Metadata(input,Name));ids.Add(instance.Id.Value);
            }
            return ids;
        });
        return Services.Json.Serialize(new { preview,representation="native FamilyInstance",created_count=preview?0:ids.Count,proposed_count=ids.Count,element_ids=preview?null:ids,warnings });
    }
}
