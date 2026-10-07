using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;

namespace ClaudeRevit.Tools;

public sealed class GenerateFloorStack : IRevitTool
{
    public string Name => "generate_floor_stack";
    public string Description => "Create native levels/floors for a stepped tower from a parameter table in ONE atomic Undo batch. Each storey supplies level_name,elevation_mm,contour_mm:[[x,y],...], optional holes_mm, scale,rotation_deg,offset_mm:[x,y]. Validates every name/contour before changes; snap_tolerance_mm is opt-in (0 default) and reports maximum displacement. preview=true rolls back; preview=false commits. Repeated elevations may reuse existing matching levels. Max 1000 storeys. Carries source/assumption provenance; not DirectShape.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["floor_type_id"]=NativeToolUtil.Field("integer","Loaded FloorType ID."),
        ["storeys"]=NativeToolUtil.Any("Array of {level_name,elevation_mm,contour_mm?,holes_mm?,scale?,rotation_deg?,offset_mm?}. Each contour closes automatically; omitted contours/holes use the root defaults."),
        ["contour_mm"]=NativeToolUtil.Any("Optional common contour [[x,y],...] reused by storeys without contour_mm."),
        ["holes_mm"]=NativeToolUtil.Any("Optional common hole contours, reused by storeys without holes_mm."),
        ["structural"]=NativeToolUtil.Field("boolean","Structural native floors; default false."),
        ["snap_tolerance_mm"]=NativeToolUtil.Field("number","Optional near-axis alignment, 0..10 mm. Default 0."),
        ["provenance"]=NativeToolUtil.Any("Optional sources/dimensions/assumptions object."),
        ["preview"]=NativeToolUtil.Field("boolean","Default true. Use false to commit.")
    },"floor_type_id","storeys");
    private sealed record Storey(string Name,double Elevation,List<CheckedContour> Contours);
    private static List<Storey> Plan(IReadOnlyDictionary<string,JsonElement> input,Document doc)
    {
        if (doc.IsFamilyDocument) throw new ToolInputException("Floor stack requires a project.");
        if (NativeToolUtil.Element(doc,ToolInput.RequiredLong(input, "floor_type_id")) is not FloorType) throw new ToolInputException("floor_type_id is not FloorType.");
        var snap=ToolInput.OptionalDouble(input,"snap_tolerance_mm")??0;
        var entries=ToolInput.RequiredArray(input, "storeys").EnumerateArray().ToArray();if (entries.Length is <1 or >1000) throw new ToolInputException("Supply 1..1000 storeys.");
        var levels=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToDictionary(l=>l.Name,StringComparer.OrdinalIgnoreCase);
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var plan=new List<Storey>();
        foreach (var entry in entries)
        {
            ToolContext.ThrowIfCancelled(); var name=entry.GetProperty("level_name").GetString()??"";GeometryPreflight.Name(name);
            if (!names.Add(name)) throw new ToolInputException("Repeated level name: "+name);
            var elevation=entry.GetProperty("elevation_mm").GetDouble();if (!double.IsFinite(elevation)) throw new ToolInputException("Elevation must be finite millimetres.");
            if (levels.TryGetValue(name,out var level) && Math.Abs(level.Elevation*Units.MmPerFoot-elevation)>0.1) throw new ToolInputException("Existing level has a different elevation: "+name);
            var scale=entry.TryGetProperty("scale",out var s)?s.GetDouble():1;var angle=(entry.TryGetProperty("rotation_deg",out var r)?r.GetDouble():0)*Math.PI/180;
            if (!double.IsFinite(scale)||scale<=0||!double.IsFinite(angle)) throw new ToolInputException("Scale must be positive and rotation finite.");
            var offset=entry.TryGetProperty("offset_mm",out var o)?o.EnumerateArray().Select(v=>v.GetDouble()).ToArray():new double[]{0,0};
            if (offset.Length!=2||offset.Any(v=>!double.IsFinite(v))) throw new ToolInputException("offset_mm requires [x,y].");
            CheckedContour Read(JsonElement contour) => GeometryPreflight.Contour(contour.EnumerateArray().Select(v=>
            { var xy=v.EnumerateArray().Select(x=>x.GetDouble()).ToArray();if(xy.Length!=2)throw new ToolInputException("Contour point must be [x,y] mm.");return new PlanPoint(offset[0]+scale*(xy[0]*Math.Cos(angle)-xy[1]*Math.Sin(angle)),offset[1]+scale*(xy[0]*Math.Sin(angle)+xy[1]*Math.Cos(angle))); }),doc.Application.ShortCurveTolerance*Units.MmPerFoot,snap);
            if(!entry.TryGetProperty("contour_mm",out var contour) && !input.TryGetValue("contour_mm",out contour))throw new ToolInputException("Supply a root or per-storey contour_mm.");
            var contours=new List<CheckedContour>{Read(contour)};
            if(entry.TryGetProperty("holes_mm",out var holes) || input.TryGetValue("holes_mm",out holes)) contours.AddRange(holes.EnumerateArray().Select(Read));
            GeometryPreflight.Holes(contours.Select(c=>c.Points).ToArray());
            plan.Add(new(name,elevation,contours));
        }
        return plan;
    }
    public void Preflight(IReadOnlyDictionary<string,JsonElement> input,UIApplication app) => Plan(input,NativeToolUtil.Doc(app));
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        var doc=NativeToolUtil.Doc(app);var plan=Plan(input,doc);var preview=NativeToolUtil.Preview(input);
        var (result,warnings)=NativeToolUtil.Commit(doc,"Claude: floor stack",preview,()=>
        {
            var rows=new List<object>();var done=0;
            var levels=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToDictionary(l=>l.Name,StringComparer.OrdinalIgnoreCase);
            foreach(var storey in plan)
            {
                ToolContext.ReportProgress(done++,plan.Count,"Floors");
                if(!levels.TryGetValue(storey.Name,out var level)){level=Level.Create(doc,storey.Elevation/Units.MmPerFoot);level.Name=storey.Name;levels[storey.Name]=level;}
                var loops=storey.Contours.Select(c=>{var loop=new CurveLoop();for(var i=0;i<c.Points.Count;i++){var a=c.Points[i];var b=c.Points[(i+1)%c.Points.Count];loop.Append(Line.CreateBound(new XYZ(a.X,a.Y,storey.Elevation)/Units.MmPerFoot,new XYZ(b.X,b.Y,storey.Elevation)/Units.MmPerFoot));}return loop;}).ToList();
                var floor=Floor.Create(doc,loops,new ElementId(ToolInput.RequiredLong(input, "floor_type_id")),level.Id,ToolInput.Flag(input,"structural"),null,0);
                ModelProvenance.Write(floor,new { generator=Name,level_name=storey.Name,elevation_mm=storey.Elevation,
                    max_vertex_shift_mm=storey.Contours.Max(c=>c.MaxShiftMm),provenance=input.TryGetValue("provenance",out var p)?(object)p:new { status="unknown" } });
                rows.Add(new { level_name=level.Name,level_id=preview?(long?)null:level.Id.Value,floor_id=preview?(long?)null:floor.Id.Value,elevation_mm=storey.Elevation,max_vertex_shift_mm=storey.Contours.Max(c=>c.MaxShiftMm) });
            }
            ToolContext.ReportProgress(plan.Count,plan.Count,"Floors");return rows;
        });
        return Services.Json.Serialize(new { preview,representation="native Floor + Level",created_count=preview?0:plan.Count,proposed_count=plan.Count,storeys=result,warnings });
    }
}
