namespace ClaudeRevit.Services;

public readonly record struct PlanPoint(double X, double Y);
public sealed record CheckedContour(IReadOnlyList<PlanPoint> Points, double MaxShiftMm);

public static class GeometryPreflight
{
    public static void Holes(IReadOnlyList<PlanPoint>[] loops)
    {
        static double Cross(PlanPoint a,PlanPoint b,PlanPoint c) => (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
        static bool Contains(IReadOnlyList<PlanPoint> polygon,PlanPoint p)
        {
            var inside=false;
            for(var i=0;i<polygon.Count;i++)
            { var a=polygon[i];var b=polygon[(i+1)%polygon.Count];if((a.Y>p.Y)!=(b.Y>p.Y)&&p.X<(b.X-a.X)*(p.Y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside; }
            return inside;
        }
        for(var i=1;i<loops.Length;i++)
        {
            if(loops[i].Any(p=>!Contains(loops[0],p)))throw new ArgumentException("A hole must lie strictly inside the outer contour.");
            for(var j=0;j<i;j++)
            {
                if(j>0&&(Contains(loops[j],loops[i][0])||Contains(loops[i],loops[j][0])))throw new ArgumentException("Holes overlap or nest.");
                for(var a=0;a<loops[i].Count;a++)for(var b=0;b<loops[j].Count;b++)
                {
                    var p=loops[i][a];var q=loops[i][(a+1)%loops[i].Count];var r=loops[j][b];var s=loops[j][(b+1)%loops[j].Count];
                    if(Cross(p,q,r)*Cross(p,q,s)<=0&&Cross(r,s,p)*Cross(r,s,q)<=0&&Math.Max(Math.Min(p.X,q.X),Math.Min(r.X,s.X))<=Math.Min(Math.Max(p.X,q.X),Math.Max(r.X,s.X))&&Math.Max(Math.Min(p.Y,q.Y),Math.Min(r.Y,s.Y))<=Math.Min(Math.Max(p.Y,q.Y),Math.Max(r.Y,s.Y)))
                        throw new ArgumentException("Contours intersect or touch.");
                }
            }
        }
    }
    public static void Name(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny("\\:{}[]|;<>?`~".ToCharArray()) >= 0 || name.Any(char.IsControl))
            throw new ArgumentException("Revit name is empty or contains a prohibited character: " + name);
    }

    public static CheckedContour Contour(IEnumerable<PlanPoint> points, double shortCurveMm, double snapMm = 0)
    {
        if (!double.IsFinite(snapMm) || snapMm < 0 || snapMm > 10) throw new ArgumentException("snap_tolerance_mm must be 0..10.");
        var original = points.ToList();
        if (original.Count > 1 && original[0] == original[^1]) original.RemoveAt(original.Count - 1);
        if (original.Count is < 3 or > 2000 || original.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
            throw new ArgumentException("A contour needs 3..2000 finite points in millimetres.");
        var clean = original.ToArray();
        // Only align a coordinate with an existing adjacent coordinate; report displacement.
        if (snapMm > 0)
            for (var i = 1; i < clean.Length; i++)
            {
                var p = clean[i]; var a = clean[i - 1];
                clean[i] = new(Math.Abs(p.X - a.X) <= snapMm ? a.X : p.X, Math.Abs(p.Y - a.Y) <= snapMm ? a.Y : p.Y);
            }
        double Cross(PlanPoint a, PlanPoint b, PlanPoint c) => (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
        bool On(PlanPoint a, PlanPoint b, PlanPoint p) => Math.Abs(Cross(a,b,p)) < 1e-6 &&
            p.X >= Math.Min(a.X,b.X)-1e-6 && p.X <= Math.Max(a.X,b.X)+1e-6 && p.Y >= Math.Min(a.Y,b.Y)-1e-6 && p.Y <= Math.Max(a.Y,b.Y)+1e-6;
        for (var i = 0; i < clean.Length; i++)
        {
            var a = clean[i]; var b = clean[(i+1)%clean.Length];
            if (Math.Sqrt((b.X-a.X)*(b.X-a.X)+(b.Y-a.Y)*(b.Y-a.Y)) <= shortCurveMm) throw new ArgumentException($"Contour edge {i} is shorter than Revit's tolerance ({shortCurveMm:0.###} mm).");
            for (var j = i+1; j < clean.Length; j++)
            {
                if (j==i+1 || i==0 && j==clean.Length-1) continue;
                var c=clean[j]; var d=clean[(j+1)%clean.Length];
                if (Cross(a,b,c)*Cross(a,b,d)<0 && Cross(c,d,a)*Cross(c,d,b)<0 || On(a,b,c) || On(a,b,d) || On(c,d,a) || On(c,d,b))
                    throw new ArgumentException($"Contour edges {i} and {j} intersect.");
            }
        }
        var area = clean.Select((p,i)=>p.X*clean[(i+1)%clean.Length].Y-p.Y*clean[(i+1)%clean.Length].X).Sum()/2;
        if (Math.Abs(area) <= shortCurveMm*shortCurveMm) throw new ArgumentException("Contour has zero or negligible area.");
        return new(clean, clean.Select((p,i)=>Math.Sqrt(Math.Pow(p.X-original[i].X,2)+Math.Pow(p.Y-original[i].Y,2))).Max());
    }
}
