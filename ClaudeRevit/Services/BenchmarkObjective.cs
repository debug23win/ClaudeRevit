using System.Text.Json;

namespace ClaudeRevit.Services;

public sealed record ObjectiveCheck(string Name, string Status, string Detail, int Weight = 1);
public sealed record ObjectiveReport(string Version, IReadOnlyList<ObjectiveCheck> Checks)
{
    public int Failed => Checks.Count(c => c.Status == "failed");
    public int Incomplete => Checks.Count(c => c.Status == "incomplete");
    public bool Passed => Checks.Count > 0 && Failed == 0 && Incomplete == 0;
    public int Ceiling => Checks.Count == 0 ? 0 : (int)Math.Floor(100d * Checks.Where(c => c.Status == "passed").Sum(c => c.Weight) / Checks.Sum(c => c.Weight));
}

// These checks read the full independent probe BEFORE judge sampling. No model narration,
// English/localised display names or bounding boxes are accepted as proof of a rebar host.
public static class BenchmarkObjective
{
    public const string Version = "objective-v1";
    public static ObjectiveReport Evaluate(string task, string before, string after)
    {
        var checks = new List<ObjectiveCheck>();
        void Add(string name, bool? passed, string detail, int weight = 1) => checks.Add(new(name, passed.HasValue ? passed.Value ? "passed" : "failed" : "incomplete", detail, weight));
        try
        {
            using var bd = JsonDocument.Parse(before); using var ad = JsonDocument.Parse(after);
            var b = bd.RootElement; var a = ad.RootElement;
            if (b.TryGetProperty("probe_error", out _) || a.TryGetProperty("probe_error", out _))
            { Add("independent probe", null, "Probe unavailable; no objective result can be established."); return new(Version, checks); }
            double? N(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
            List<JsonElement>? Rows(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array &&
                !(root.TryGetProperty(name + "_truncated", out var t) && t.ValueKind == JsonValueKind.True) ? v.EnumerateArray().ToList() : null;
            List<JsonElement>? New(string name)
            {
                var old = Rows(b, name); var current = Rows(a, name); if (old == null || current == null) return null;
                var ids = old.Select(e => N(e, "id")).ToHashSet(); return current.Where(e => N(e, "id").HasValue && !ids.Contains(N(e, "id"))).ToList();
            }
            bool Near(double? value, double expected, double tol = 1) => value.HasValue && Math.Abs(value.Value - expected) <= tol;
            void Delta(string name, int count, int weight = 1) => Add(name + " delta", N(a, name) is { } x && N(b, name) is { } y ? Near(x - y, count, 0) : null, $"Expected {count} new native {name}.", weight);
            void RowCheck(string name, string description, Func<List<JsonElement>, bool> test, int weight = 1)
            { var rows = New(name); Add(description, rows == null || rows.Any(e => e.TryGetProperty("evidence_error", out _)) ? null : test(rows), "Independent native snapshots: " + name, weight); }
            double[]? Vector(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 3 ? v.EnumerateArray().Select(n => n.GetDouble()).ToArray() : null;
            double[]? Size(JsonElement row) => row.TryGetProperty("bounds", out var box) && Vector(box, "min_mm") is { } lo && Vector(box, "max_mm") is { } hi ? hi.Zip(lo, (x,y) => x-y).ToArray() : null;
            void Rebar()
            {
                var rows = New("rebar_elements"); var hosts = Rows(a, "family_instances")?.Concat(Rows(a, "floor_elements") ?? []).Select(e => N(e, "id")).ToHashSet();
                Add("native hosted reinforcement", rows == null || hosts == null ? null : rows.Count > 0 && rows.All(e => hosts.Contains(N(e, "host_id")) && N(e, "quantity") > 0), "Rebar must reference an existing native family/floor host.", 3);
                Add("centerlines inside host", rows == null ? null : rows.Count > 0 && rows.All(e =>
                {
                    if (!e.TryGetProperty("host_bounds", out var box) || Vector(box,"min_mm") is not { } lo || Vector(box,"max_mm") is not { } hi || !e.TryGetProperty("centerlines",out var lines)) return false;
                    var points = lines.EnumerateArray().SelectMany(s=>s.GetProperty("curves").EnumerateArray()).SelectMany(c=>c.GetProperty("points_mm").EnumerateArray()).ToArray();
                    return points.Length > 0 && points.All(p=>p.GetArrayLength()==3 && p.EnumerateArray().Select(v=>v.GetDouble()).Select((x,i)=>x>=lo[i]-1 && x<=hi[i]+1).All(x=>x));
                }), "Sampled native centerlines must remain within host bounds; this does not prove cover or all unsampled positions.", 2);
            }
            switch (task)
            {
                case "B0": Delta("levels",1); RowCheck("level_elements","named level elevation",r=>r.Count==1 && r[0].GetProperty("name").GetString()=="Bench B0" && Near(N(r[0],"elevation_m"),3.5,.001)); break;
                case "B1": Delta("walls",1); RowCheck("wall_elements","5000 mm straight wall",r=>r.Count==1 && r[0].GetProperty("curve").GetProperty("kind").GetString()=="Line" && Near(N(r[0].GetProperty("curve"),"length_mm"),5000,1)); break;
                case "B2": Delta("floors",1); RowCheck("floor_elements","4 x 3 m floor",r=>r.Count==1 && Near(N(r[0],"area_m2"),12,.05) && Size(r[0]) is { } s && Near(s[0],4000,1)&&Near(s[1],3000,1)); break;
                case "B3": Delta("grids",1); RowCheck("grid_elements","10000 mm straight grid",r=>r.Count==1 && r[0].GetProperty("curve").GetProperty("kind").GetString()=="Line" && Near(N(r[0].GetProperty("curve"),"length_mm"),10000,1)); break;
                case "B4": Delta("structural_columns",1); RowCheck("family_instances","400 x 400 native column",r=>r.Any(e=>N(e,"category_id")==-2001330 && Size(e) is { } s && Near(s[0],400,1)&&Near(s[1],400,1))); break;
                case "B5": Delta("materials",1); RowCheck("material_elements","named grey material",r=>r.Any(e=>e.GetProperty("name").GetString()=="Bench Concrete" && Vector(e,"rgb") is { } rgb && Near(rgb[0],rgb[1],1)&&Near(rgb[1],rgb[2],1))); break;
                case "B6": Delta("direct_shape_count",1); RowCheck("direct_shape_geometry","2 m cube geometry",r=>r.Count==1 && Size(r[0]) is { } s && s.All(v=>Near(v,2000,1)) && N(r[0].GetProperty("geometry"),"triangle_count")>0); break;
                case "L1":
                    Delta("walls",4); Delta("floors",1); Delta("roofs",1); Delta("doors",1);
                    RowCheck("floor_elements","40 m2 slab",r=>r.Count==1 && Near(N(r[0],"area_m2"),40,.1));
                    RowCheck("wall_elements","closed rectangular wall loop",r=>ClosedLoop(r,8000,5000)); break;
                case "L2": case "L5":
                    int floors=task=="L2"?5:10, columns=task=="L2"?20:120; Delta("floors",floors); Delta("structural_columns",columns,3);
                    RowCheck("floor_elements","storey elevations and floor areas",r=>r.Count==floors && r.All(e=>Near(N(e,"area_m2"),task=="L2"?144:180,.1)) &&
                        r.Select(e=>e.GetProperty("bounds").GetProperty("max_mm")[2].GetDouble()).Order().Select((z,i)=>Near(z,i*3500,5)).All(x=>x));
                    RowCheck("family_instances","column grid and sections",r=>ColumnGrid(r, task=="L2"?[0d,12000]:[0d,5000,10000,15000], task=="L2"?[0d,12000]:[0d,6000,12000],floors,task=="L5")); if(task=="L2")Delta("grids",4); break;
                case "L3": Delta("walls",4); RowCheck("wall_elements","selective wall retyping",r=>r.Count==4 && r.Select(e=>e.GetProperty("curve").GetProperty("length_mm").GetDouble()).Order().Zip(new[]{4000d,5000,7000,8000},(x,y)=>Near(x,y,1)).All(v=>v) && r.All(e=>
                    e.GetProperty("curve").GetProperty("length_mm").GetDouble()>6000 ? Near(N(e,"type_width_mm"),300,1) : !Near(N(e,"type_width_mm"),300,1))); break;
                case "L4": RowCheck("direct_shape_geometry","coarse half-cylinder surface",r=>r.Any(e=>Barrel(e)),3); break;
                case "R1": case "R3": case "R4": case "R5": case "D6":
                    Rebar();
                    if(task=="R3")RowCheck("rebar_elements","eight 20 mm longitudinal bars and stirrup zones",r=>r.Where(e=>Near(N(e,"diameter_mm"),20,.01)).Sum(e=>N(e,"quantity")??0)==8 && r.Count(e=>Near(N(e,"diameter_mm"),10,.01)&&Near(N(e,"spacing_mm"),100,1))>=2 && r.Any(e=>Near(N(e,"diameter_mm"),10,.01)&&Near(N(e,"spacing_mm"),200,1)),2);
                    if(task=="R4") { Delta("floors",1); RowCheck("openings","native slab opening",r=>r.Count>=1,2); RowCheck("rebar_elements","mesh, trimming and U-bar diameters",r=>r.Any(e=>Near(N(e,"diameter_mm"),12,.01))&&r.Any(e=>Near(N(e,"diameter_mm"),16,.01))); }
                    if(task=="R5")RowCheck("rebar_elements","native curved 16 mm centerlines",r=>r.Any(e=>Near(N(e,"diameter_mm"),16,.01)&&e.GetProperty("centerlines").EnumerateArray().Any(s=>s.GetProperty("curves").EnumerateArray().Any(c=>c.GetProperty("kind").GetString()=="Arc"))),2);
                    if(task=="D6")RowCheck("schedules","populated grouped rebar schedule",r=>r.Any(e=>e.GetProperty("name").GetString()=="Bench D6 Rebar" && N(e,"sort_group_count")>0 && e.GetProperty("fields").GetArrayLength()>=4 && N(e,"body_rows")>1)); break;
                case "R2":
                    RowCheck("floor_elements","native 24 m2 slab",r=>r.Any(e=>Near(N(e,"area_m2"),24,.1)));
                    var floorIds=Rows(a,"floor_elements")?.Select(e=>N(e,"id")).ToHashSet();
                    foreach(var name in new[]{"area_reinforcement_elements","path_reinforcement_elements"})RowCheck(name,name+" hosted in floor",r=>r.Count>0&&floorIds!=null&&r.All(e=>floorIds.Contains(N(e,"host_id"))),3); break;
                case "S1": Delta("structural_columns",2,2); Delta("structural_framing",1,2);
                    var connections=New("connection_elements"); var shapes=New("direct_shape_geometry");
                    Add("connected native or detailed joint", connections==null||shapes==null?null:connections.Any(e=>e.GetProperty("connected_element_ids").GetArrayLength()>=2)||shapes.Any(e=>N(e.GetProperty("geometry"),"solid_count")>=2&&HasMemberLinks(e)),"Native member links or multiple actual joint solids with explicit member IDs; dimensional completeness is checked by the independent judge.",2); break;
                case "D1":
                    RowCheck("sheets","two populated sheets",r=>r.Count==2 && r.Select(e=>e.GetProperty("number").GetString()).Order().SequenceEqual(new[]{"BD-101","BD-102"})&&r.All(e=>e.GetProperty("placed_view_ids").GetArrayLength()>0),2);
                    RowCheck("viewports","distinct sheet viewports",r=>r.Count>=2&&r.Select(e=>N(e,"sheet_id")).Distinct().Count()==2&&r.Select(e=>N(e,"view_id")).Distinct().Count()>=2); break;
                case "D2":
                    var schedules=New("schedules"); var exported=Rows(a,"schedule_exports");
                    RowCheck("schedules","wall schedule has required fields",r=>r.Any(e=>e.GetProperty("name").GetString()=="Bench D2 Walls"&&e.GetProperty("fields").GetArrayLength()>=3),2);
                    Add("CSV export verified",exported==null?null:exported.Any(e=>e.TryGetProperty("verified",out var verified)&&verified.ValueKind==JsonValueKind.True&&schedules!=null&&schedules.Any(s=>N(s,"id")==N(e,"schedule_id"))),"Actual file hash and exported cells must match the current native schedule."); break;
                case "D3":
                    var taskLevel=Rows(a,"level_elements")?.FirstOrDefault(e=>e.TryGetProperty("name",out var n)&&n.GetString()=="Bench D3");
                    var levelId=taskLevel.HasValue?N(taskLevel.Value,"id"):null;
                    var plan=New("plans")?.FirstOrDefault(e=>levelId.HasValue&&N(e,"level_id")==levelId); var planId=plan.HasValue?N(plan.Value,"id"):null;
                    RowCheck("tags","three tags in requested plan",r=>planId.HasValue&&r.Count(e=>N(e,"owner_view_id")==planId)>=3,2); RowCheck("dimensions","dimension in requested plan",r=>planId.HasValue&&r.Any(e=>N(e,"owner_view_id")==planId)); break;
                case "D4": Delta("walls",5); RowCheck("wall_elements","SHORT applied only below 4 m",r=>r.Count==5&&r.All(e=>(e.GetProperty("curve").GetProperty("length_mm").GetDouble()<4000-1e-6)==(e.GetProperty("comments").GetString()=="SHORT")),2); break;
                case "D5":
                    foreach(var name in new[]{"wall_elements","floor_elements","family_instances","direct_shape_geometry"})
                    {var old=Rows(b,name);var current=Rows(a,name);Add("preserved "+name,old==null||current==null?null:old.All(e=>current.Any(c=>N(c,"id")==N(e,"id"))),"Placed baseline geometry must not be deleted.");} break;
                case "F1": case "F2": case "F3": case "F4": case "F5":
                    if(!a.TryGetProperty("independent_flex",out var flex)||!flex.TryGetProperty("results",out var scenarios)||scenarios.ValueKind!=JsonValueKind.Array)Add("independent flex",null,"Family flex evidence unavailable.",3);
                    else
                    {
                        var tests=scenarios.EnumerateArray().ToArray();Add("all independent flex scenarios valid",tests.Length>0&&tests.All(s=>s.TryGetProperty("valid",out var valid)&&valid.ValueKind==JsonValueKind.True),"No failed parameter regeneration or missing solids.",3);
                        if(task is "F1" or "F2" or "F4")Add("geometry varies across scenarios",tests.Length>1&&tests.Where(s=>s.TryGetProperty("geometry",out _)).Select(s=>s.GetProperty("geometry").GetProperty("bounds").GetRawText()).Distinct().Count()>1,"Actual geometry, not parameter metadata, must vary.",2);
                        if(task=="F1")Add("exact family size scenarios",tests.All(s=>FamilyBox(s)),"Width/depth/height and volume must match each requested scenario.",2);
                    }
                    if(task=="F2")RowCheck("family_instances","four native nested children",r=>r.Count==4,2);
                    break;
                default: Add("task contract",null,"No deterministic contract is registered for this task.");break;
            }
        }
        catch(Exception ex) when(ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException)
        { Add("complete evidence",null,"Malformed or missing independent evidence: "+ex.Message,3); }
        return new(Version,checks);
    }
    public static BenchmarkVerdict Apply(BenchmarkVerdict judge, ObjectiveReport report)
    {
        if(!judge.Graded)return judge;
        if(report.Checks.All(c=>c.Status=="incomplete"))return new(false,0,"Objective evidence unavailable; no measured quality score. "+judge.Reason,false);
        return judge with { Pass=judge.Pass&&report.Passed, Score=Math.Min(judge.Score,report.Ceiling), Reason=$"[objective {report.Checks.Count(c=>c.Status=="passed")}/{report.Checks.Count}; failed {report.Failed}; incomplete {report.Incomplete}] "+judge.Reason };
    }
    private static bool ClosedLoop(List<JsonElement> rows,double width,double depth)
    {
        if(rows.Count!=4)return false;
        var points=rows.SelectMany(r=>new[]{r.GetProperty("curve").GetProperty("points_mm")[0],r.GetProperty("curve").GetProperty("points_mm")[4]}).Select(p=>p.EnumerateArray().Select(v=>v.GetDouble()).ToArray()).ToArray();
        bool Near(double x,double y)=>Math.Abs(x-y)<=1;
        return points.All(p=>points.Count(q=>p.Zip(q,Near).All(v=>v))==2)&&Near(points.Max(p=>p[0])-points.Min(p=>p[0]),width)&&Near(points.Max(p=>p[1])-points.Min(p=>p[1]),depth)&&rows.All(r=>r.GetProperty("curve").GetProperty("kind").GetString()=="Line");
    }
    private static bool HasMemberLinks(JsonElement element)
    {
        if(!element.TryGetProperty("provenance",out var p)||p.ValueKind!=JsonValueKind.Object)return false;
        if(p.TryGetProperty("provenance",out var nested)&&nested.ValueKind==JsonValueKind.Object)p=nested;
        return p.TryGetProperty("related_element_ids",out var ids)&&ids.ValueKind==JsonValueKind.Array&&ids.GetArrayLength()>=2;
    }
    private static bool ColumnGrid(List<JsonElement> rows,double[] xs,double[] ys,int levels,bool retyped)
    {
        var columns=rows.Where(e=>e.GetProperty("category_id").ValueKind==JsonValueKind.Number && e.GetProperty("category_id").GetInt64()==-2001330).ToArray();
        if(columns.Length!=xs.Length*ys.Length*levels)return false;
        var cells=new HashSet<string>();
        foreach(var e in columns)
        {
            var p=e.GetProperty("location").EnumerateArray().Select(v=>v.GetDouble()).ToArray();var x=Array.FindIndex(xs,v=>Math.Abs(v-p[0])<=1);var y=Array.FindIndex(ys,v=>Math.Abs(v-p[1])<=1);int z=(int)Math.Round(p[2]/3500);
            if(x<0||y<0||z<0||z>=levels||Math.Abs(p[2]-z*3500)>1||!cells.Add($"{x},{y},{z}"))return false;
            var b=e.GetProperty("bounds");double side=retyped&&Math.Abs(p[0]-15000)<1?500:400;
            if(Math.Abs(b.GetProperty("max_mm")[0].GetDouble()-b.GetProperty("min_mm")[0].GetDouble()-side)>1||Math.Abs(b.GetProperty("max_mm")[1].GetDouble()-b.GetProperty("min_mm")[1].GetDouble()-side)>1)return false;
        }
        return true;
    }
    private static bool Barrel(JsonElement e)
    {
        var b=e.GetProperty("bounds");var lo=b.GetProperty("min_mm").EnumerateArray().Select(v=>v.GetDouble()).ToArray();var hi=b.GetProperty("max_mm").EnumerateArray().Select(v=>v.GetDouble()).ToArray();
        var g=e.GetProperty("geometry");int triangles=g.GetProperty("triangle_count").GetInt32();if(triangles<=0||triangles>=2000)return false;
        int axis=Math.Abs(hi[0]-lo[0]-20000)<100?0:1, cross=1-axis;if(Math.Abs(hi[axis]-lo[axis]-20000)>100||Math.Abs(hi[cross]-lo[cross]-10000)>100||Math.Abs(lo[2]-4000)>100||Math.Abs(hi[2]-lo[2]-5000)>100)return false;
        var samples=g.GetProperty("surface_samples_mm").EnumerateArray().Select(v=>v.EnumerateArray().Select(n=>n.GetDouble()).ToArray()).ToArray();double center=(lo[cross]+hi[cross])/2;
        return samples.Length>=12&&samples.All(p=>Math.Abs(Math.Sqrt(Math.Pow(p[cross]-center,2)+Math.Pow(p[2]-4000,2))-5000)<=100)&&samples.Any(p=>p[2]>6000&&p[2]<8500);
    }
    private static bool FamilyBox(JsonElement scenario)
    {
        var sizes=new Dictionary<string,double[]>{["small"]=[300,200,240],["nominal"]=[1000,600,800],["large"]=[3000,1800,2400],["wide"]=[2500,200,2000],["deep"]=[300,2000,240],["repeat"]=[1000,600,800]};
        if(!sizes.TryGetValue(scenario.GetProperty("name").GetString()??"",out var expected)||!scenario.TryGetProperty("geometry",out var g))return false;
        var bounds=g.GetProperty("bounds").EnumerateArray().ToArray();if(bounds.Length!=1)return false;
        var actual=bounds[0].GetProperty("max_mm").EnumerateArray().Zip(bounds[0].GetProperty("min_mm").EnumerateArray(),(hi,lo)=>hi.GetDouble()-lo.GetDouble()).ToArray();
        return expected.Zip(actual,(x,y)=>Math.Abs(x-y)<=1).All(v=>v)&&Math.Abs(g.GetProperty("summed_solid_volume_m3").GetDouble()-expected.Aggregate(1d,(x,y)=>x*y)/1e9)<=1e-6;
    }
}
