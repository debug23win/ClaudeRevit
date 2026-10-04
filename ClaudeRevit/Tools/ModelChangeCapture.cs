using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace ClaudeRevit.Tools;

internal sealed class ModelChangeCapture
{
    private sealed record Info(string Category,string Representation);
    private static readonly Dictionary<string,Dictionary<long,Info>> Cache=new();
    private static ModelChangeCapture? _current;
    private readonly Document _doc;
    private readonly Dictionary<long,Info> _before;
    private readonly HashSet<long> _added=new(),_modified=new(),_deleted=new();
    private readonly Dictionary<long,Info> _seen=new();
    private static Info Snapshot(Element? e)=>new(e?.Category?.Name??"(no category)",e==null?"deleted":ModelProvenance.Representation(e));
    public ModelChangeCapture(Document doc,string key)
    {
        _doc=doc;
        if(!Cache.TryGetValue(key,out var cache))
        {
            cache=new FilteredElementCollector(doc).WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(),new ElementIsElementTypeFilter(true)))
                .ToElements().ToDictionary(e=>e.Id.Value,Snapshot);Cache[key]=cache;
        }
        _before=cache;_current=this;
    }
    public static void Changed(DocumentChangedEventArgs e)
    {
        var c=_current;
        if(c==null)
        {
            if(Cache.TryGetValue(Services.DocumentSessions.Key(e.GetDocument()),out var cache))
            {
                foreach(var id in e.GetAddedElementIds().Concat(e.GetModifiedElementIds()))cache[id.Value]=Snapshot(e.GetDocument().GetElement(id));
                foreach(var id in e.GetDeletedElementIds())cache.Remove(id.Value);
            }
            return;
        }
        if(!Services.DocumentSessions.Same(c._doc,e.GetDocument()))return;
        foreach(var id in e.GetAddedElementIds()){c._added.Add(id.Value);c._seen[id.Value]=Snapshot(c._doc.GetElement(id));}
        foreach(var id in e.GetModifiedElementIds()){c._modified.Add(id.Value);c._seen[id.Value]=Snapshot(c._doc.GetElement(id));}
        foreach(var id in e.GetDeletedElementIds())c._deleted.Add(id.Value);
    }
    public object Complete(bool rolledBack)
    {
        _current=null;
        bool Exists(long id)=>_doc.IsValidObject&&_doc.GetElement(new ElementId(id))!=null;
        var added=rolledBack?[]:_added.Where(id=>!_before.ContainsKey(id)&&Exists(id)).ToArray();
        var deleted=rolledBack?[]:_deleted.Where(id=>_before.ContainsKey(id)&&!Exists(id)).ToArray();
        var modified=rolledBack?[]:_modified.Where(id=>_before.ContainsKey(id)&&Exists(id)&&!deleted.Contains(id)).ToArray();
        object ByCategory(long[] ids,bool before)=>ids.GroupBy(id=>before?_before[id].Category:_seen.GetValueOrDefault(id,Snapshot(_doc.GetElement(new ElementId(id)))).Category).ToDictionary(g=>g.Key,g=>g.Count());
        var result=new { added_count=added.Length,modified_count=modified.Length,deleted_count=deleted.Length,
            added_by_category=ByCategory(added,false),modified_by_category=ByCategory(modified,false),deleted_by_category=ByCategory(deleted,true),
            added_by_representation=added.GroupBy(id=>_seen[id].Representation).ToDictionary(g=>g.Key,g=>g.Count()),
            added_ids=added,modified_ids=modified,deleted_ids=deleted,rolled_back=rolledBack };
        foreach(var id in added.Concat(modified))_before[id]=Snapshot(_doc.GetElement(new ElementId(id)));
        foreach(var id in deleted)_before.Remove(id);
        foreach(var key in Cache.Keys.Where(key=>key!=Services.DocumentSessions.CurrentDocumentKey&&Services.DocumentSessions.Find(key)==null).ToArray())Cache.Remove(key);
        return result;
    }
}
