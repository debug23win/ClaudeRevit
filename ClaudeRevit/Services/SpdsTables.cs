using System.Globalization;

namespace ClaudeRevit.Services;

public sealed record SpdsColumn(string Key,string Heading,double WidthMm);
public sealed record SpdsRow(string[] Cells,long[] SourceIds,string Kind="data");
public sealed record SpdsTable(string Profile,List<SpdsColumn> Columns,List<SpdsRow> Rows,double? TotalMassKg);
public sealed class SpdsItem
{
    public long SourceId { get; set; }
    public string Mark { get; set; }="";
    public string Designation { get; set; }="";
    public string Name { get; set; }="";
    public string Notes { get; set; }="";
    public string Profile { get; set; }="";
    public string Grade { get; set; }="";
    public string Size { get; set; }="";
    public string Group { get; set; }="";
    public string Unit { get; set; }="";
    public double Quantity { get; set; }=1;
    public double? MassKg { get; set; }
    public double? Amount { get; set; }
}
public static class SpdsTables
{
    public static readonly string[] Profiles=["steel_rollup","timber_materials","timber_elements","scheme_specification"];
    public static List<SpdsColumn> Columns(string profile,string[]? groups=null)=>profile switch
    {
        "steel_rollup"=>[new("profile","Профиль, ГОСТ/ТУ",30),new("grade","Марка металла, ГОСТ/ТУ",30),new("size","Размер профиля, мм",30),new("position","Поз.",10),..(groups??[]).Select(g=>new SpdsColumn(g,g+", т",15)),new("total","Общая масса, т",25)],
        "timber_materials"=>[new("name","Наименование",105),new("unit","Ед. изм.",15),new("quantity","Кол.",15),new("notes","Примечание",50)],
        "timber_elements"=>[new("mark","Марка",15),new("name","Наименование",65),new("quantity","Кол.",15),new("mass","Масса, кг",20),new("notes","Примечание",20)],
        "scheme_specification"=>[new("mark","Поз.",15),new("designation","Обозначение",60),new("name","Наименование",65),new("quantity","Кол.",10),new("mass","Масса ед., кг",15),new("notes","Примечание",20)],
        _=>throw new ArgumentException("Unknown SPDS table profile.")
    };
    private static string Num(double n,int decimals=3)=>Math.Round(n,decimals,MidpointRounding.AwayFromZero).ToString("0."+new string('#',decimals),CultureInfo.GetCultureInfo("ru-RU"));
    public static int CompareNatural(string a,string b)=>NaturalOrder.Instance.Compare(a,b);
    public static void ValidateItem(string profile,SpdsItem item)=>Build(profile,[item]);
    public static string RowKey(string profile,SpdsItem i)=>System.Text.Json.JsonSerializer.Serialize(profile switch
    {
        "steel_rollup" => new object?[]{i.Profile,i.Grade,i.Size},
        "timber_materials" => new object?[]{i.Name,i.Unit,i.Notes},
        "timber_elements" => new object?[]{i.Mark,i.Name,i.MassKg,i.Notes},
        "scheme_specification" => new object?[]{i.Mark,i.Designation,i.Name,i.MassKg,i.Notes},
        _ => throw new ArgumentException("Unknown SPDS profile.")
    });
    // Normalized model parameters. Unrounded mass remains physical kg until Revit formats
    // and totals it; never round each element before summing a schedule.
    public static Dictionary<string,object> ModelValues(string profile,SpdsItem i,string[] groups)
    {
        ValidateItem(profile,i);
        if(profile=="steel_rollup"&&!groups.Contains(i.Group,StringComparer.Ordinal))throw new ArgumentException("Construction group is outside the approved matrix columns: "+i.Group);
        var values=new Dictionary<string,object>{["row_key"]=RowKey(profile,i),["profile"]=i.Profile,["grade"]=i.Grade,["size"]=i.Size,["mark"]=i.Mark,["designation"]=i.Designation,["name"]=i.Name,["notes"]=i.Notes,["unit"]=i.Unit,["quantity"]=profile=="timber_materials"?i.Amount!.Value*i.Quantity:i.Quantity,["mass"]=i.MassKg??0,["total"]=i.Quantity*(i.MassKg??0)};
        for(int g=0;g<groups.Length;g++)values["group_"+g]=i.Group==groups[g]?i.Quantity*i.MassKg!.Value:0d;
        return values;
    }
    public static SpdsTable Build(string profile,IReadOnlyList<SpdsItem> items)
    {
        if(items.Count is <1 or >10000||items.Any(i=>i.SourceId<=0)||items.Select(i=>i.SourceId).Distinct().Count()!=items.Count)throw new ArgumentException("Supply 1..10000 distinct source element IDs.");
        if(items.Any(i=>!double.IsFinite(i.Quantity)||i.Quantity<=0||i.MassKg.HasValue&&(!double.IsFinite(i.MassKg.Value)||i.MassKg.Value<0||!double.IsFinite(i.MassKg.Value*i.Quantity))||i.Amount.HasValue&&(!double.IsFinite(i.Amount.Value)||i.Amount.Value<0||!double.IsFinite(i.Amount.Value*i.Quantity))))throw new ArgumentException("Quantities and measurements/products must be finite and nonnegative; counts must be positive.");
        var rows=new List<SpdsRow>();var total=items.All(i=>i.MassKg.HasValue)?items.Sum(i=>i.MassKg!.Value*i.Quantity):(double?)null;
        if(total.HasValue&&!double.IsFinite(total.Value)||!double.IsFinite(items.Sum(i=>i.Quantity))||!double.IsFinite(items.Sum(i=>(i.Amount??0)*i.Quantity)))throw new ArgumentException("Combined quantities overflow; narrow the table scope.");
        if(profile=="steel_rollup")
        {
            if(items.Any(i=>new[]{i.Profile,i.Grade,i.Size,i.Group}.Any(string.IsNullOrWhiteSpace)||i.MassKg==null))throw new ArgumentException("Steel rollup needs profile, grade, size, construction group and unit mass for every element; missing data must be clarified.");
            var groups=items.Select(i=>i.Group).Distinct().OrderBy(x=>x,StringComparer.Ordinal).ToArray();if(groups.Length>20)throw new ArgumentException("Split steel rollup into at most 20 construction groups.");
            string[] Mass(IEnumerable<SpdsItem> source)
            {var all=source.ToArray();return [..groups.Select(g=>Num(all.Where(i=>i.Group==g).Sum(i=>i.MassKg!.Value*i.Quantity)/1000,1)),Num(all.Sum(i=>i.MassKg!.Value*i.Quantity)/1000,1)];}
            int position=0;
            foreach(var p in items.GroupBy(i=>i.Profile).OrderBy(g=>g.Key,StringComparer.Ordinal))
            {
                foreach(var grade in p.GroupBy(i=>i.Grade).OrderBy(g=>g.Key,StringComparer.Ordinal))
                {
                    foreach(var size in grade.GroupBy(i=>i.Size).OrderBy(g=>g.Key,NaturalOrder.Instance))rows.Add(new([p.Key,grade.Key,size.Key,(++position).ToString(),..Mass(size)],size.Select(i=>i.SourceId).ToArray()));
                    rows.Add(new([p.Key,grade.Key,"Всего","",..Mass(grade)],grade.Select(i=>i.SourceId).ToArray(),"subtotal"));
                }
                rows.Add(new([p.Key,"","Итого","",..Mass(p)],p.Select(i=>i.SourceId).ToArray(),"subtotal"));
            }
            rows.Add(new(["Всего масса металла","","","",..Mass(items)],items.Select(i=>i.SourceId).ToArray(),"total"));
            foreach(var grade in items.GroupBy(i=>i.Grade).OrderBy(g=>g.Key,StringComparer.Ordinal))rows.Add(new(["В том числе",grade.Key,"","",..Mass(grade)],grade.Select(i=>i.SourceId).ToArray(),"grade_total"));
            return new(profile,Columns(profile,groups),rows,total);
        }
        if(items.Any(i=>string.IsNullOrWhiteSpace(i.Name)))throw new ArgumentException("Each source needs a name.");
        if(profile=="timber_materials")
        {
            if(items.Any(i=>string.IsNullOrWhiteSpace(i.Unit)||i.Amount==null))throw new ArgumentException("Material statement needs explicit units and amount per source.");
            foreach(var g in items.GroupBy(i=>(i.Name,i.Unit,i.Notes)).OrderBy(g=>g.Key.Name,StringComparer.Ordinal))rows.Add(new([g.Key.Name,g.Key.Unit,Num(g.Sum(i=>i.Amount!.Value*i.Quantity)),g.Key.Notes],g.Select(i=>i.SourceId).ToArray()));
        }
        else
        {
            if(items.Any(i=>string.IsNullOrWhiteSpace(i.Mark)||i.MassKg==null))throw new ArgumentException("Element/scheme specifications require a mark and mass for each source.");
            foreach(var g in items.GroupBy(i=>(i.Mark,i.Name,i.Designation,i.MassKg,i.Notes)).OrderBy(g=>g.Key.Mark,NaturalOrder.Instance))
            {var qty=Num(g.Sum(i=>i.Quantity));var mass=Num(g.Key.MassKg!.Value);rows.Add(new(profile=="scheme_specification"?[g.Key.Mark,g.Key.Designation,g.Key.Name,qty,mass,g.Key.Notes]:[g.Key.Mark,g.Key.Name,qty,mass,g.Key.Notes],g.Select(i=>i.SourceId).ToArray()));}
        }
        return new(profile,Columns(profile),rows,total);
    }
    private sealed class NaturalOrder:IComparer<string>
    {
        public static readonly NaturalOrder Instance=new();
        public int Compare(string? a,string? b)
        {
            a??="";b??="";int x=0,y=0;
            while(x<a.Length&&y<b.Length)
            {if(char.IsDigit(a[x])&&char.IsDigit(b[y])){int sx=x,sy=y;while(x<a.Length&&char.IsDigit(a[x]))x++;while(y<b.Length&&char.IsDigit(b[y]))y++;var ax=a[sx..x].TrimStart('0');var by=b[sy..y].TrimStart('0');int c=ax.Length.CompareTo(by.Length);if(c==0)c=string.CompareOrdinal(ax,by);if(c!=0)return c;}else{int c=a[x++].CompareTo(b[y++]);if(c!=0)return c;}}
            return a.Length.CompareTo(b.Length);
        }
    }
}
