using System.Text.Json;

namespace ClaudeRevit.Services;

// Revit-free contract and geometry math. Stable part keys, rather than array positions,
// identify the same bolt/plate on subsequent updates.
public sealed class ConnectionSpec
{
    public List<ConnectionPart> Parts { get; set; } = [];
    public List<ConnectionRule> Rules { get; set; } = [];
    public string? CalculationEvidence { get; set; }
    public string? DocumentationEvidence { get; set; }
    public static readonly JsonSerializerOptions Options = new(Json.Options) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public IReadOnlyList<ConnectionPart> OrderedParts()
    {
        if (Parts==null || Rules==null || Parts.Count is < 1 or > 500 || Rules.Count > 500 || Parts.Any(p=>p==null) || Rules.Any(r=>r==null)) throw new ArgumentException("Supply 1..500 nonnull parts and at most 500 nonnull rules per node.");
        var map = new Dictionary<string, ConnectionPart>(StringComparer.Ordinal);
        foreach (var p in Parts)
        {
            if (string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 128 || !map.TryAdd(p.Key, p)) throw new ArgumentException("Part keys must be unique, nonempty and <=128 characters.");
            if (p.Role is not ("member" or "plate" or "splice" or "bolt" or "hole" or "cope" or "rib" or "other")) throw new ArgumentException($"Unknown role for {p.Key}.");
            if (p.ElementId is <= 0 || p.FamilyTypeId is <= 0 || p.LevelId is <= 0) throw new ArgumentException("Element/type/level IDs must be positive.");
            if (p.PointMm != null) ConnectionMath.Vector(p.PointMm);
            if (p.OffsetMm != null) ConnectionMath.Vector(p.OffsetMm);
            if ((p.AxisX == null) != (p.AxisY == null)) throw new ArgumentException("Supply both axis_x and axis_y.");
            if (p.AxisX != null) ConnectionMath.Frame(p.AxisX, p.AxisY!);
            if (p.ElementId == null && p.FamilyTypeId == null) throw new ArgumentException($"{p.Key} needs element_id or family_type_id.");
            if (p.ElementId == null && p.PointMm == null && p.RelativeTo == null && p.FaceReference == null) throw new ArgumentException($"{p.Key} needs a placement point, relative_to or face_reference.");
            if (new[] { p.PointMm != null, p.RelativeTo != null, p.FaceReference != null }.Count(x => x) > 1) throw new ArgumentException("Point, relative_to and face_reference are alternative placement modes.");
            if (p.Parameters==null || p.CutTargets==null || p.Parameters.Count > 100 || p.Parameters.Any(p=>p==null)) throw new ArgumentException("Supply nonnull parameter/cut arrays, at most 100 parameters per part.");
            foreach (var param in p.Parameters)
            {
                if (param.Unit is not ("internal" or "mm") || string.IsNullOrWhiteSpace(param.Name) == string.IsNullOrWhiteSpace(param.Guid)) throw new ArgumentException("Each parameter needs exactly one name or guid and unit internal/mm.");
                if ((param.ValueFrom != null) == (param.Value.ValueKind != JsonValueKind.Undefined)) throw new ArgumentException("Specify value OR value_from, not both/neither.");
                if (param.ValueFrom is { } v && (string.IsNullOrWhiteSpace(v.PartKey) || string.IsNullOrWhiteSpace(v.Name) == string.IsNullOrWhiteSpace(v.Guid) || v.Scope is not ("instance" or "type") || !double.IsFinite(v.Scale) || !double.IsFinite(v.OffsetMm) || param.Unit != "mm")) throw new ArgumentException("value_from needs part_key, one parameter name/GUID, instance/type scope, finite scale/offset_mm and target unit mm.");
            }
        }
        var ordered = new List<ConnectionPart>(); var visiting = new HashSet<string>(); var done = new HashSet<string>();
        void Visit(ConnectionPart p)
        {
            if (done.Contains(p.Key)) return;
            if (!visiting.Add(p.Key)) throw new ArgumentException("relative_to contains a cycle.");
            if (p.RelativeTo != null) { if (!map.TryGetValue(p.RelativeTo, out var parent)) throw new ArgumentException($"Missing relative_to part {p.RelativeTo}."); Visit(parent); }
            foreach (var dependency in p.Parameters.Where(v => v.ValueFrom != null).Select(v => v.ValueFrom!.PartKey))
            { if (!map.TryGetValue(dependency, out var source)) throw new ArgumentException("Missing value_from source: " + dependency); Visit(source); }
            visiting.Remove(p.Key); done.Add(p.Key); ordered.Add(p);
        }
        foreach (var p in Parts) Visit(p);
        foreach (var p in Parts)
            foreach (var key in p.CutTargets) if (!map.ContainsKey(key) || key == p.Key) throw new ArgumentException($"Invalid cut target {key}.");
        foreach (var r in Rules)
        {
            if(r.StackParts==null||r.ForbiddenPlanes==null||r.ForbiddenPlanes.Any(p=>p==null))throw new ArgumentException("Rule lists cannot be null.");
            if (r.Kind is not ("clash" or "contact" or "bolt")) throw new ArgumentException("Rule kind must be clash/contact/bolt.");
            if (!map.ContainsKey(r.A) || !map.ContainsKey(r.B) || r.A == r.B) throw new ArgumentException("Rules need two different existing part keys a/b.");
            if (!double.IsFinite(r.ToleranceMm) || r.ToleranceMm < 0 || !double.IsFinite(r.MaxVolumeMm3) || r.MaxVolumeMm3 < 0 || !double.IsFinite(r.ExpectedOffsetMm)) throw new ArgumentException("Rule tolerances must be finite and nonnegative.");
            ConnectionMath.Unit(r.LocalAxisA); ConnectionMath.Unit(r.LocalAxisB);
            if (r.Kind == "contact" && string.IsNullOrWhiteSpace(r.FaceReference)) throw new ArgumentException("Contact rules require a stable planar face_reference on part b.");
            foreach (var key in r.StackParts) if (!map.ContainsKey(key)) throw new ArgumentException($"Unknown stack part {key}.");
            foreach (var plane in r.ForbiddenPlanes) { ConnectionMath.Vector(plane.OriginMm); ConnectionMath.Unit(plane.Normal); if (!double.IsFinite(plane.HalfWidthMm) || plane.HalfWidthMm < 0) throw new ArgumentException("Invalid forbidden zone width."); }
        }
        return ordered;
    }
}

public sealed class ConnectionPart
{
    public string Key { get; set; } = "";
    public string Role { get; set; } = "other";
    public long? ElementId { get; set; }
    public long? FamilyTypeId { get; set; }
    public long? LevelId { get; set; }
    public double[]? PointMm { get; set; }
    public double[]? AxisX { get; set; }
    public double[]? AxisY { get; set; }
    public string? RelativeTo { get; set; }
    public string? FaceReference { get; set; }
    public double[]? OffsetMm { get; set; }
    public List<ConnectionParameter> Parameters { get; set; } = [];
    public List<string> CutTargets { get; set; } = [];
}
public sealed class ConnectionParameter
{
    public string? Name { get; set; }
    public string? Guid { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Value { get; set; }
    public ConnectionValueFrom? ValueFrom { get; set; }
    public string Unit { get; set; } = "internal";
}
public sealed class ConnectionValueFrom
{
    public string PartKey { get; set; } = "";
    public string? Name { get; set; }
    public string? Guid { get; set; }
    public string Scope { get; set; } = "instance";
    public double Scale { get; set; } = 1;
    public double OffsetMm { get; set; }
}
public sealed class ConnectionRule
{
    public string Kind { get; set; } = "";
    public string A { get; set; } = "";
    public string B { get; set; } = "";
    public string? FaceReference { get; set; }
    public double ToleranceMm { get; set; } = 1;
    public double ExpectedOffsetMm { get; set; }
    public double MaxVolumeMm3 { get; set; } = 1;
    public double[] LocalAxisA { get; set; } = [0, 0, 1];
    public double[] LocalAxisB { get; set; } = [0, 0, 1];
    public string? BoltDiameterParameter { get; set; }
    public string? HoleDiameterParameter { get; set; }
    public string? GripParameter { get; set; }
    public List<string> StackParts { get; set; } = [];
    public List<ConnectionPlane> ForbiddenPlanes { get; set; } = [];
}
public sealed class ConnectionPlane
{
    public double[] OriginMm { get; set; } = [0, 0, 0];
    public double[] Normal { get; set; } = [1, 0, 0];
    public double HalfWidthMm { get; set; }
}
public static class ConnectionMath
{
    public static double[] Vector(double[] v) { if (v==null || v.Length != 3 || v.Any(x => !double.IsFinite(x))) throw new ArgumentException("Vectors need three finite coordinates."); return v; }
    public static double Dot(double[] a, double[] b) => a.Zip(b, (x, y) => x * y).Sum();
    public static double[] Sub(double[] a, double[] b) => a.Zip(b, (x, y) => x - y).ToArray();
    public static double[] Cross(double[] a, double[] b) => [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]];
    public static double Norm(double[] v) => Math.Sqrt(Dot(v,v));
    public static double[] Unit(double[] v) { Vector(v); var n = Norm(v); if (n < 1e-10) throw new ArgumentException("Axis is zero."); return v.Select(x=>x/n).ToArray(); }
    public static void Frame(double[] x, double[] y) { if (Math.Abs(Dot(Unit(x), Unit(y))) > 1e-6) throw new ArgumentException("Frame axes must be perpendicular."); }
    public static (double DistanceMm, double AngleDeg) Axes(double[] a, double[] u, double[] b, double[] v)
    {
        u=Unit(u);v=Unit(v);var delta=Sub(b,a);
        // A bolt/hole pair must be coaxial, not merely two crossing lines.
        return (Norm(Cross(delta,u)), Math.Acos(Math.Clamp(Math.Abs(Dot(u,v)),0,1))*180/Math.PI);
    }
    public static bool InForbiddenZone(double[] p, ConnectionPlane plane, double radiusMm, double toleranceMm) =>
        Math.Abs(Dot(Sub(Vector(p), Vector(plane.OriginMm)), Unit(plane.Normal))) < plane.HalfWidthMm + radiusMm + toleranceMm;
}
