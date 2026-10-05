using System.IO;
using System.Reflection;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using ClaudeRevit.Tools;

namespace ClaudeRevit.NativeTests;

internal static class GeometryFixtures
{
    public static object Bore(UIApplication app,JsonElement input)
    {
        var family=app.Application.OpenDocumentFile(input.GetProperty("section_file").GetString()!);
        try
        {
            var extrusion=new FilteredElementCollector(family).OfClass(typeof(Extrusion)).Cast<Extrusion>().Single();
            var method=typeof(CreateParametricSection).Assembly.GetType("ClaudeRevit.Tools.ConnectionOpeningGeometry",true)!.GetMethod("Inspect")!;
            JsonElement Inspect(XYZ origin,double diameter)=>JsonSerializer.SerializeToElement(method.Invoke(null,new object[]{extrusion,origin,XYZ.BasisZ,diameter,.1}));
            var good=Inspect(XYZ.Zero,200);var offset=Inspect(new XYZ(5/304.8,0,0),200);var oversize=Inspect(XYZ.Zero,280);
            if(!good.GetProperty("Verified").GetBoolean()||Math.Abs(good.GetProperty("DiameterMm").GetDouble()-276)>.001)throw new InvalidOperationException("Native through bore was not identified: "+good);
            if(offset.GetProperty("Verified").ValueKind==JsonValueKind.True||oversize.GetProperty("Verified").ValueKind==JsonValueKind.True)throw new InvalidOperationException("Misaligned/oversized bolt was accepted.");
            return new{passed=true,good,offset_rejected=offset,oversize_rejected=oversize,limitation="Actual bore geometry only; no capacity or thread engagement assertion."};
        }
        finally{family.Close(false);}
    }
    public static object ReferencePlacement(UIApplication app,JsonElement input)
    {
        var project=app.Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            FamilyInstance beam;FamilySymbol symbol;
            using(var tx=new Transaction(project,"QA native reference framing placement"))
            {
                tx.Start();var level=Level.Create(project,0);
                if(!project.LoadFamily(input.GetProperty("family_file").GetString()!,out var family))throw new InvalidOperationException("Cannot load reference framing.");
                symbol=(FamilySymbol)project.GetElement(family.GetFamilySymbolIds().First());symbol.Activate();project.Regenerate();
                beam=project.Create.NewFamilyInstance(Line.CreateBound(new XYZ(0,0,1000/304.8),new XYZ(6000/304.8,0,1000/304.8)),symbol,level,StructuralType.Beam);
                tx.Commit();
            }
            if(beam.Category.Id.Value!=(long)BuiltInCategory.OST_StructuralFraming||beam.StructuralType!=StructuralType.Beam)throw new InvalidOperationException("Reference is not native structural framing.");
            double Length()=>beam.get_Parameter(new Guid("b62d0a35-0f0f-432d-9d3d-e821093a7d02")).AsDouble()*304.8;
            double Width()
            {
                var all=new FilteredElementCollector(project).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(f=>f.Id==beam.Id||f.SuperComponent?.Id==beam.Id);
                var points=all.SelectMany(SyntheticFixtures.Solids).SelectMany(s=>s.Faces.Cast<Face>()).SelectMany(f=>f.Triangulate().Vertices).ToArray();
                return (points.Max(p=>p.Y)-points.Min(p=>p.Y))*304.8;
            }
            var originalLength=Length();var originalWidth=Width();
            using(var tx=new Transaction(project,"QA native section edit")){tx.Start();symbol.LookupParameter("Ширина").Set(260/304.8);tx.Commit();}
            var editedWidth=Width();if(Math.Abs(editedWidth-260)>.01||Math.Abs(editedWidth-originalWidth)<.01)throw new InvalidOperationException("Native framing width did not follow type dimension: "+editedWidth);
            using(var tx=new Transaction(project,"QA native placement length edit")){tx.Start();((LocationCurve)beam.Location).Curve=Line.CreateBound(new XYZ(0,0,1000/304.8),new XYZ(8000/304.8,0,1000/304.8));tx.Commit();}
            var editedLength=Length();if(Math.Abs(editedLength-originalLength-2000)>.01)throw new InvalidOperationException("Actual family placement length did not follow curve edit.");
            return new{passed=true,category=beam.Category.Name,structural_type=beam.StructuralType.ToString(),original_length_mm=originalLength,edited_length_mm=editedLength,original_width_mm=originalWidth,edited_width_mm=editedWidth,limitation="Downloaded box beam acceptance; this does not certify every generated structural template."};
        }
        finally{project.Close(false);}
    }
}
