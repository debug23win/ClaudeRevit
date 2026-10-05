namespace ClaudeRevit.Services;

public static class TrussLayout
{
    public sealed record Joint(string Key, double[] PointMm, bool Support);
    public sealed record Member(string Key, string Role, string Start, string End, double LengthMm);
    public sealed record Splice(string Key, string Chord, double Xmm, double SegmentLengthMm);
    public sealed record Plan(List<Joint> Joints, List<Member> Members, List<Splice> Splices, double[] SupportStationsMm);
    public static Plan Build(double[] spans, double width, double height, double panel, double module, int divisions, double stagger)
    {
        if (spans.Length is < 1 or > 20 || spans.Any(x=>!double.IsFinite(x)||x<=0) || new[]{width,height,panel,module}.Any(x=>!double.IsFinite(x)||x<=0) || divisions is < 1 or > 100 || !double.IsFinite(stagger) || stagger<=0 || stagger>=module/divisions)
            throw new ArgumentException("Positive finite dimensions required; 1..20 spans, divisions 1..100, stagger strictly inside one module subdivision.");
        var support=new List<double>{0};foreach(var span in spans)support.Add(support[^1]+span);
        var xs=new SortedSet<double>(support); var total=support[^1];
        if(total/panel>500 || total/(module/divisions)>1000)throw new ArgumentException("Layout limit: 500 panels or 1000 chord subdivisions.");
        // Global stations and global diagonal parity across the intermediate support.
        for(double x=panel;x<total;x+=panel)xs.Add(x);
        var stations=xs.ToArray();var joints=new List<Joint>();var members=new List<Member>();var splices=new List<Splice>();
        string K(int side,int i,bool top)=>$"s{side}:n{i}:{(top?"upper":"lower")}";
        var points=new Dictionary<string,double[]>();
        for(int side=0;side<2;side++)for(int i=0;i<stations.Length;i++)foreach(bool top in new[]{false,true})
        {var key=K(side,i,top);var point=new[]{stations[i],side*width,top?height:0};points[key]=point;joints.Add(new(key,point,support.Contains(stations[i])));}
        void Add(string role,string a,string b) { var key=$"{role}:{a}:{b}";members.Add(new(key,role,a,b,ConnectionMath.Norm(ConnectionMath.Sub(points[a],points[b])))); }
        for(int side=0;side<2;side++)
        {
            for(int i=0;i<stations.Length;i++)Add("post",K(side,i,false),K(side,i,true));
            for(int i=0;i<stations.Length-1;i++)
            {foreach(bool top in new[]{false,true})Add(top?"upper_chord":"lower_chord",K(side,i,top),K(side,i+1,top)); Add("diagonal",K(side,i,i%2==0),K(side,i+1,i%2!=0));}
        }
        for(int i=0;i<stations.Length;i++)foreach(bool top in new[]{false,true})Add("cross_member",K(0,i,top),K(1,i,top));
        for(int i=0;i<stations.Length-1;i++)foreach(bool top in new[]{false,true})
        {Add("cross_brace",K(0,i,top),K(1,i+1,top));Add("cross_brace",K(1,i,top),K(0,i+1,top));}
        double step=module/divisions;
        foreach(string chord in new[]{"lower","upper"})
        {
            double previous=0;double offset=chord=="upper"?stagger:step;
            for(double x=offset;x<total;x+=step){splices.Add(new($"{chord}:splice:{splices.Count}",chord,x,x-previous));previous=x;}
            splices.Add(new($"{chord}:end",chord,total,total-previous));
        }
        return new(joints,members,splices,support.ToArray());
    }
}
