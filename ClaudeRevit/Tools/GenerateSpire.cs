using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public sealed class GenerateSpire : IRevitTool
{
    public string Name => "generate_spire";
    public string Description => "Generate or replace a revolved spire from radius/elevation table profile_mm:[[radius,z],...], origin_mm:[x,y,0]. RFA creates an editable native Revolution; RVT creates explicit DirectShape geometry (not a structural BIM column). Parameters and provenance are stored for regeneration. element_id replaces a previous generated spire atomically, returns the new ID. preview defaults true. One Undo batch, no mesh code required.";
    public bool RequiresTransaction => false;
    public bool MutatesWithoutTransaction => true;
    public bool RequiresNoTurnGroup => true;
    public InputSchema InputSchema => NativeToolUtil.Schema(new()
    {
        ["profile_mm"]=NativeToolUtil.Any("Ascending elevation table [[radius,z],...], 2..200 entries, radius>=0."),["origin_mm"]=NativeToolUtil.Any("[x,y,0], default origin."),
        ["element_id"]=NativeToolUtil.Field("integer","Optional existing spire created by this generator to replace."),["preview"]=NativeToolUtil.Field("boolean","Default true; false commits."),["provenance"]=NativeToolUtil.Any("Sources and assumptions.")
    },"profile_mm");
    private static List<XYZ> Profile(IReadOnlyDictionary<string,JsonElement> input)
    {
        var rows=input["profile_mm"].EnumerateArray().Select(r=>r.EnumerateArray().Select(v=>v.GetDouble()).ToArray()).ToArray();
        if(rows.Length is <2 or >200||rows.Any(r=>r.Length!=2||r.Any(v=>!double.IsFinite(v))||r[0]<0)||rows.All(r=>r[0]==0))throw new ToolInputException("Invalid radius/elevation table.");
        for(var i=1;i<rows.Length;i++)if(rows[i][1]<=rows[i-1][1])throw new ToolInputException("Elevations must increase strictly.");
        if(rows.Skip(1).SkipLast(1).Any(r=>r[0]==0))throw new ToolInputException("An interior zero radius would split or pinch the solid; use separate spires.");
        var origin=input.TryGetValue("origin_mm",out var p)?NativeToolUtil.Point(p):XYZ.Zero;
        if(Math.Abs(origin.Z)>1e-9)throw new ToolInputException("origin_mm Z must be 0; elevations come from profile_mm.");
        var points=new List<XYZ>{origin+new XYZ(0,0,rows[0][1]/Units.MmPerFoot)};
        points.AddRange(rows.Select(r=>origin+new XYZ(r[0],0,r[1])/Units.MmPerFoot));
        points.Add(origin+new XYZ(0,0,rows[^1][1]/Units.MmPerFoot));
        return points.Where((point,i)=>i==0||point.DistanceTo(points[i-1])>1e-9).ToList();
    }
    public void Preflight(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        var doc=NativeToolUtil.Doc(app);var points=Profile(input);
        for(var i=0;i<points.Count;i++)if(points[i].DistanceTo(points[(i+1)%points.Count])<=doc.Application.ShortCurveTolerance)throw new ToolInputException("Profile segment is shorter than Revit's tolerance.");
        if(input.TryGetValue("element_id",out var id))
        {
            var metadata=ModelProvenance.Read(NativeToolUtil.Element(doc,id.GetInt64()));
            if(metadata==null||!metadata.Value.TryGetProperty("generator",out var generator)||generator.GetString()!=Name)throw new ToolInputException("Only a previous generate_spire element can be replaced.");
        }
    }
    public string Execute(IReadOnlyDictionary<string,JsonElement> input,UIApplication app)
    {
        Preflight(input,app);var doc=NativeToolUtil.Doc(app);var points=Profile(input);var preview=NativeToolUtil.Preview(input);
        var (result,warnings)=NativeToolUtil.Commit(doc,"Claude: spire",preview,()=>
        {
            ToolContext.ReportProgress(0,1,"Spire");var loop=new CurveLoop();var curves=new CurveArray();
            for(var i=0;i<points.Count;i++){var line=Line.CreateBound(points[i],points[(i+1)%points.Count]);loop.Append(line);curves.Append(line);}
            var origin=new XYZ(points[0].X,points[0].Y,0);Element element;
            if(doc.IsFamilyDocument)
            {
                var profiles=new CurveArrArray();profiles.Append(curves);
                var plane=SketchPlane.Create(doc,Plane.CreateByNormalAndOrigin(XYZ.BasisY,origin));
                element=doc.FamilyCreate.NewRevolution(true,profiles,plane,Line.CreateBound(origin,origin+XYZ.BasisZ),0,2*Math.PI);
            }
            else
            {
                var frame=new Frame(origin,XYZ.BasisX,XYZ.BasisY,XYZ.BasisZ);
                var solid=GeometryCreationUtilities.CreateRevolvedGeometry(frame,new List<CurveLoop>{loop},0,2*Math.PI);
                var shape=DirectShape.CreateElement(doc,new ElementId(BuiltInCategory.OST_GenericModel));shape.SetShape(new List<GeometryObject>{solid});element=shape;
            }
            ModelProvenance.Write(element,ModelProvenance.Metadata(input,Name));
            if(input.TryGetValue("element_id",out var id))doc.Delete(new ElementId(id.GetInt64()));
            return new { element_id=preview?(long?)null:element.Id.Value,representation=ModelProvenance.Representation(element) };
        });
        return Services.Json.Serialize(new { preview,result,warnings });
    }
}
