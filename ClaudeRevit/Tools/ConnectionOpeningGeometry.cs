using Autodesk.Revit.DB;
namespace ClaudeRevit.Tools;

internal sealed record BoreEvidence(bool? Verified, double? DiameterMm, double? StartMm, double? EndMm, string Detail);
internal static class ConnectionOpeningGeometry
{
    public static BoreEvidence Inspect(Element plate, XYZ origin, XYZ axis, double requiredDiameterMm, double toleranceMm)
    {
        var solids=ConnectionNodes.Solids(plate);if(solids.Count==0)return new(null,null,null,null,"No native solid plate geometry.");
        var vertices=solids.SelectMany(s=>s.Faces.Cast<Face>()).SelectMany(f=>f.Triangulate().Vertices).Take(50001).ToArray();
        if(vertices.Length is 0 or >50000)return new(null,null,null,null,"Actual solid vertices exceed inspection limits or are absent.");
        double lo=vertices.Min(p=>(p-origin).DotProduct(axis))*Units.MmPerFoot,hi=vertices.Max(p=>(p-origin).DotProduct(axis))*Units.MmPerFoot;
        foreach(var face in solids.SelectMany(s=>s.Faces.Cast<Face>()).OfType<CylindricalFace>())
        {
            ToolContext.ThrowIfCancelled();
            if(Math.Abs(face.Axis.Normalize().DotProduct(axis))<Math.Cos(.1*Math.PI/180))continue;
            if((face.Origin-origin).CrossProduct(axis).GetLength()*Units.MmPerFoot>toleranceMm)continue;
            var uv=face.GetBoundingBox();var middle=(uv.Min+uv.Max)/2;var point=face.Evaluate(middle);
            var radial=point-face.Origin;radial-=face.Axis.Normalize()*radial.DotProduct(face.Axis.Normalize());
            if(radial.GetLength()<1e-9||face.ComputeNormal(middle).DotProduct(radial.Normalize())>=-.5)continue; // inner bore, not an external bolt cylinder
            double diameter=2*face.get_Radius(0).GetLength()*Units.MmPerFoot;
            var mesh=face.Triangulate();double start=mesh.Vertices.Min(p=>(p-origin).DotProduct(axis))*Units.MmPerFoot,end=mesh.Vertices.Max(p=>(p-origin).DotProduct(axis))*Units.MmPerFoot;
            if(diameter+1e-6<requiredDiameterMm)continue;
            if(start<=lo+toleranceMm&&end>=hi-toleranceMm)return new(true,diameter,start,end,"Actual inner cylindrical face penetrates the complete solid envelope along the bolt axis.");
        }
        return new(false,null,lo,hi,"No coaxial through bore of sufficient diameter exists in the actual plate solids; faceted/noncylindrical bores require another geometry check.");
    }
}
