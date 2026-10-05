namespace ClaudeRevit.Services;
public static class NestingSelection
{
    public static HashSet<string> Select(IEnumerable<(string Id,string? Parent)> instances,string policy)
    {
        if(policy is not ("explicit" or "parents_only" or "children_only"))throw new ArgumentException("Choose explicit, parents_only or children_only.");
        var map=instances.ToDictionary(x=>x.Id,x=>x.Parent,StringComparer.Ordinal);var ancestors=new Dictionary<string,HashSet<string>>();
        foreach(var id in map.Keys)
        {
            var chain=new HashSet<string>();var p=map[id];
            while(p!=null&&map.ContainsKey(p)){if(p==id||!chain.Add(p))throw new ArgumentException("Cyclic nested-instance references.");p=map[p];}
            ancestors[id]=chain;
        }
        var parents=ancestors.Values.SelectMany(v=>v).ToHashSet(StringComparer.Ordinal);
        return map.Keys.Where(id=>policy=="explicit"||policy=="parents_only"&&ancestors[id].Count==0||policy=="children_only"&&!parents.Contains(id)).ToHashSet(StringComparer.Ordinal);
    }
}
